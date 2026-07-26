using Microsoft.Data.Sqlite;

namespace OpenCodeDatabaseCleaner.Database;

public sealed class OpenCodeDb : IDisposable
{
    private readonly SqliteConnection _connection;

    public OpenCodeDb(string databasePath)
    {
        if (!File.Exists(databasePath))
        {
            throw new FileNotFoundException($"OpenCode database not found: {databasePath}", databasePath);
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        };

        _connection = new SqliteConnection(connectionString.ToString());
        _connection.Open();

        using var command = _connection.CreateCommand();
        command.CommandText = "PRAGMA secure_delete = ON";
        command.ExecuteNonQuery();
    }

    public CleanupPreview BuildCleanupPreview(long cutoffMilliseconds) =>
        new(BuildCleanupPlan(cutoffMilliseconds));

    public int ApplyCleanup(CleanupPreview preview, long cutoffMilliseconds)
    {
        using var transaction = _connection.BeginTransaction(deferred: false);

        try
        {
            var plan = BuildCleanupPlan(cutoffMilliseconds, transaction);
            if (!preview.Matches(plan))
            {
                throw new InvalidOperationException(
                    "The database changed while waiting for confirmation. Close OpenCode and run the cleanup again.");
            }

            foreach (var action in plan.Actions.OrderBy(ActionOrder))
            {
                using var command = _connection.CreateCommand();
                command.Transaction = transaction;

                if (action.Kind == ContentActionKind.Insert)
                {
                    command.CommandText = """
                        INSERT INTO part (id, message_id, session_id, time_created, time_updated, data)
                        VALUES (@id, @messageId, @sessionId, @timeCreated, @timeUpdated, @data)
                        """;
                    command.Parameters.AddWithValue("@messageId", action.MessageId!);
                    command.Parameters.AddWithValue("@sessionId", action.SessionId);
                    command.Parameters.AddWithValue("@timeCreated", action.TimeCreated!.Value);
                }
                else
                {
                    var versionCondition = action.Table == "event" ? string.Empty : " AND time_updated = @timeUpdated";
                    command.CommandText = action.Kind == ContentActionKind.Update
                        ? $"UPDATE {action.Table} SET data = @data WHERE id = @id{versionCondition}"
                        : $"DELETE FROM {action.Table} WHERE id = @id{versionCondition}";
                }

                command.Parameters.AddWithValue("@id", action.RowId);

                if (action.Kind is ContentActionKind.Insert or ContentActionKind.Update)
                {
                    command.Parameters.AddWithValue("@data", action.Data!);
                }

                if (action.TimeUpdated.HasValue)
                {
                    command.Parameters.AddWithValue("@timeUpdated", action.TimeUpdated.Value);
                }

                if (command.ExecuteNonQuery() != 1)
                {
                    throw new InvalidOperationException(
                        "The database changed after the preview. Close OpenCode and run the cleanup again.");
                }
            }

            transaction.Commit();
            return plan.ActionCount;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public void Vacuum()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "VACUUM";
        command.CommandTimeout = 0;
        command.ExecuteNonQuery();
    }

    public void CheckpointWal()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";

        using var reader = command.ExecuteReader();
        if (reader.Read() && reader.GetInt32(0) != 0)
        {
            throw new InvalidOperationException("The database is busy.");
        }
    }

    public void Dispose() => _connection.Dispose();

    private CleanupPlan BuildCleanupPlan(long cutoffMilliseconds, SqliteTransaction? transaction = null)
    {
        EnsureSupportedStorage(transaction);

        var anchors = GetEligibleMessageAnchors(cutoffMilliseconds, transaction);
        var plan = new CleanupPlan(anchors.Keys);

        AddMessageActions(plan, anchors, cutoffMilliseconds, transaction);
        AddPartActions(plan, anchors, cutoffMilliseconds, transaction);
        AddVisiblePlaceholderActions(plan, anchors, cutoffMilliseconds, transaction);
        AddEventActions(plan, anchors, transaction);
        return plan;
    }

    private void EnsureSupportedStorage(SqliteTransaction? transaction)
    {
        if (TableHasRows("session_message", transaction) || TableHasRows("session_input", transaction))
        {
            throw new NotSupportedException(
                "This database contains OpenCode V2 messages, which this version cannot safely clean.");
        }

        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1
                FROM part AS p
                LEFT JOIN message AS m ON m.id = p.message_id
                WHERE m.id IS NULL OR p.session_id IS NOT m.session_id)
            """;
        if (Convert.ToInt32(command.ExecuteScalar()) != 0)
        {
            throw new InvalidDataException("The database contains orphaned or incorrectly assigned message parts.");
        }

        command.CommandText = """
            SELECT EXISTS(
                SELECT 1
                FROM message AS m
                LEFT JOIN session AS s ON s.id = m.session_id
                WHERE s.id IS NULL)
            """;
        if (Convert.ToInt32(command.ExecuteScalar()) != 0)
        {
            throw new InvalidDataException("The database contains messages assigned to a missing session.");
        }

        command.CommandText = "SELECT EXISTS(SELECT 1 FROM event WHERE type LIKE 'session.next.%')";
        if (Convert.ToInt32(command.ExecuteScalar()) != 0)
        {
            throw new NotSupportedException(
                "This database contains OpenCode V2 events, which this version cannot safely clean.");
        }

        command.CommandText = """
            SELECT EXISTS(
                SELECT 1
                FROM event
                WHERE type = 'message.updated.1'
                  AND (json_type(data, '$.info.id') IS NOT 'text'
                       OR json_type(data, '$.info.sessionID') IS NOT 'text'
                       OR aggregate_id IS NOT json_extract(data, '$.info.sessionID')
                       OR json_type(data, '$.info.time.created') IS NOT 'integer'))
            """;
        if (Convert.ToInt32(command.ExecuteScalar()) != 0)
        {
            throw new InvalidDataException("The database contains malformed or incorrectly assigned message events.");
        }

        command.CommandText = """
            SELECT EXISTS(
                SELECT 1
                FROM event AS part_event
                WHERE part_event.type = 'message.part.updated.1'
                  AND (json_type(part_event.data, '$.part.id') IS NOT 'text'
                       OR json_type(part_event.data, '$.part.messageID') IS NOT 'text'
                       OR json_type(part_event.data, '$.part.sessionID') IS NOT 'text'
                       OR part_event.aggregate_id IS NOT json_extract(part_event.data, '$.part.sessionID')
                       OR (NOT EXISTS(
                               SELECT 1
                               FROM message
                               WHERE id = json_extract(part_event.data, '$.part.messageID')
                                 AND session_id = part_event.aggregate_id)
                           AND NOT EXISTS(
                               SELECT 1
                               FROM event AS message_event
                               WHERE message_event.aggregate_id = part_event.aggregate_id
                                 AND message_event.type = 'message.updated.1'
                                 AND json_extract(message_event.data, '$.info.id') =
                                     json_extract(part_event.data, '$.part.messageID')))))
            """;
        if (Convert.ToInt32(command.ExecuteScalar()) != 0)
        {
            throw new InvalidDataException("The database contains malformed or orphaned message-part events.");
        }

    }

    private bool TableHasRows(string tableName, SqliteTransaction? transaction)
    {
        using var existsCommand = _connection.CreateCommand();
        existsCommand.Transaction = transaction;
        existsCommand.CommandText =
            "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @tableName)";
        existsCommand.Parameters.AddWithValue("@tableName", tableName);
        if (Convert.ToInt32(existsCommand.ExecuteScalar()) == 0)
        {
            return false;
        }

        using var rowsCommand = _connection.CreateCommand();
        rowsCommand.Transaction = transaction;
        rowsCommand.CommandText = $"SELECT EXISTS(SELECT 1 FROM {tableName})";
        return Convert.ToInt32(rowsCommand.ExecuteScalar()) != 0;
    }

    private Dictionary<string, MessageAnchor> GetEligibleMessageAnchors(
        long cutoffMilliseconds,
        SqliteTransaction? transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT m.session_id,
                   m.id,
                   (SELECT p.id
                    FROM part AS p
                    WHERE p.message_id = m.id
                      AND json_extract(p.data, '$.type') = 'text'
                    ORDER BY COALESCE(json_extract(p.data, '$.synthetic'), 0),
                             p.time_created,
                             p.id
                    LIMIT 1)
            FROM message AS m
            WHERE m.time_created < @cutoff
            ORDER BY m.id
            """;
        command.Parameters.AddWithValue("@cutoff", cutoffMilliseconds);

        var anchors = new Dictionary<string, MessageAnchor>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var sessionId = reader.GetString(0);
            var messageId = reader.GetString(1);
            anchors.Add(messageId, new MessageAnchor(sessionId, reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return anchors;
    }

    private void AddMessageActions(
        CleanupPlan plan,
        IReadOnlyDictionary<string, MessageAnchor> anchors,
        long cutoffMilliseconds,
        SqliteTransaction? transaction)
    {
        if (anchors.Count == 0)
        {
            return;
        }

        using var command = CreateEligibleRowsCommand(
            "SELECT m.id, m.time_updated, m.data FROM message AS m",
            "m.id",
            cutoffMilliseconds,
            transaction);
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var id = reader.GetString(0);
            if (!anchors.TryGetValue(id, out var anchor))
            {
                continue;
            }

            var timeUpdated = reader.GetInt64(1);
            var sanitized = ContentSanitizer.SanitizeMessage(reader.GetString(2));
            if (sanitized is not null)
            {
                plan.AddUpdate(anchor.SessionId, "message", id, timeUpdated, sanitized);
            }
        }
    }

    private void AddPartActions(
        CleanupPlan plan,
        IReadOnlyDictionary<string, MessageAnchor> anchors,
        long cutoffMilliseconds,
        SqliteTransaction? transaction)
    {
        if (anchors.Count == 0)
        {
            return;
        }

        using var command = CreateEligibleRowsCommand(
            """
            SELECT p.id,
                   p.session_id,
                   p.message_id,
                   p.time_updated,
                   p.data,
                   json_extract(p.data, '$.type'),
                   p.id = (SELECT first_text.id
                           FROM part AS first_text
                           WHERE first_text.message_id = p.message_id
                             AND json_extract(first_text.data, '$.type') = 'text'
                           ORDER BY COALESCE(json_extract(first_text.data, '$.synthetic'), 0),
                                    first_text.time_created,
                                    first_text.id
                           LIMIT 1)
            FROM part AS p
            """,
            "p.message_id",
            cutoffMilliseconds,
            transaction);
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var id = reader.GetString(0);
            if (!anchors.TryGetValue(reader.GetString(2), out var anchor))
            {
                continue;
            }

            var timeUpdated = reader.GetInt64(3);
            var isRetainedText = reader.GetString(5) == "text" &&
                                 !reader.IsDBNull(6) && reader.GetInt64(6) != 0;

            if (!isRetainedText)
            {
                plan.AddDelete(anchor.SessionId, "part", id, timeUpdated);
                continue;
            }

            var placeholder = ContentSanitizer.CreatePlaceholderPart(reader.GetString(4));
            if (placeholder is not null)
            {
                plan.AddUpdate(anchor.SessionId, "part", id, timeUpdated, placeholder);
            }
        }
    }

    private void AddEventActions(
        CleanupPlan plan,
        IReadOnlyDictionary<string, MessageAnchor> anchors,
        SqliteTransaction? transaction)
    {
        if (anchors.Count == 0)
        {
            return;
        }

        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT e.id, e.aggregate_id, e.type, e.data
            FROM event AS e
            WHERE e.type IN ('message.updated.1', 'message.part.updated.1')
            """;
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var id = reader.GetString(0);
            var sessionId = reader.GetString(1);
            var eventType = reader.GetString(2);
            var data = reader.GetString(3);
            var (messageId, partId) = ContentSanitizer.GetEventIdentity(eventType, data);
            if (messageId is null ||
                !anchors.TryGetValue(messageId, out var anchor) ||
                anchor.SessionId != sessionId)
            {
                continue;
            }

            if (eventType == "message.updated.1")
            {
                var sanitized = ContentSanitizer.SanitizeMessageEvent(data);
                if (sanitized is not null)
                {
                    plan.AddUpdate(sessionId, "event", id, null, sanitized);
                }
            }
            else if (partId == anchor.PartId)
            {
                var placeholder = ContentSanitizer.CreatePlaceholderPartEvent(data);
                if (placeholder is not null)
                {
                    plan.AddUpdate(sessionId, "event", id, null, placeholder);
                }
            }
            else
            {
                plan.AddDelete(sessionId, "event", id, null);
            }
        }
    }

    private void AddVisiblePlaceholderActions(
        CleanupPlan plan,
        IReadOnlyDictionary<string, MessageAnchor> anchors,
        long cutoffMilliseconds,
        SqliteTransaction? transaction)
    {
        if (anchors.Count == 0)
        {
            return;
        }

        using var command = CreateEligibleRowsCommand(
            """
            SELECT m.id,
                   m.time_created,
                   m.time_updated,
                   'prt_cleaned_' || m.id,
                   (SELECT collision.message_id
                    FROM part AS collision
                    WHERE collision.id = 'prt_cleaned_' || m.id)
            FROM message AS m
            """,
            "m.id",
            cutoffMilliseconds,
            transaction,
            """
            NOT EXISTS(
                SELECT 1
                FROM part AS text_part
                WHERE text_part.message_id = m.id
                  AND json_extract(text_part.data, '$.type') = 'text')
            """);
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var messageId = reader.GetString(0);
            if (!anchors.TryGetValue(messageId, out var anchor))
            {
                continue;
            }

            var partId = reader.GetString(3);
            if (!reader.IsDBNull(4))
            {
                throw new InvalidDataException(
                    $"Cannot create cleanup placeholder '{partId}' because that part ID already exists.");
            }

            plan.AddPartInsert(
                anchor.SessionId,
                partId,
                messageId,
                reader.GetInt64(1),
                reader.GetInt64(2),
                ContentSanitizer.CreatePlaceholderPart());
        }
    }

    private SqliteCommand CreateEligibleRowsCommand(
        string select,
        string messageIdColumn,
        long cutoffMilliseconds,
        SqliteTransaction? transaction,
        string? where = null)
    {
        var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            {select}
            INNER JOIN message AS eligible_message ON eligible_message.id = {messageIdColumn}
            WHERE eligible_message.time_created < @cutoff
              {(where is null ? string.Empty : $"AND {where}")}
            """;
        command.Parameters.AddWithValue("@cutoff", cutoffMilliseconds);
        return command;
    }

    private static int ActionOrder(ContentAction action) => action.Table switch
    {
        "event" => 0,
        "part" => 1,
        "message" => 2,
        _ => throw new InvalidOperationException($"Unsupported cleanup table '{action.Table}'.")
    };

    private sealed record MessageAnchor(string SessionId, string? PartId);
}

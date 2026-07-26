using Microsoft.Data.Sqlite;
using OpenCodeDatabaseCleaner.Database;
using System.Text.Json.Nodes;
using Xunit;

namespace OpenCodeDatabaseCleaner.Tests;

public sealed class OpenCodeDbTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"opencode-cleaner-{Guid.NewGuid():N}.db");

    [Fact]
    public void CleanupCleansOldMessagesInMixedAgeSessionsWithoutChangingSessionMetadata()
    {
        CreateDatabase();

        using (var database = new OpenCodeDb(_databasePath))
        {
            var preview = database.BuildCleanupPreview(500);

            Assert.Equal(3, preview.MessageCount);
            Assert.Equal(6, preview.ProjectionRowCount);
            Assert.Equal(5, preview.EventRowCount);
            Assert.Equal(11, database.ApplyCleanup(preview, 500));

            var secondPreview = database.BuildCleanupPreview(500);
            Assert.Equal(0, secondPreview.ActionCount);
        }

        using var connection = OpenConnection();
        Assert.Equal(2L, Scalar<long>(connection, "SELECT COUNT(*) FROM message WHERE session_id = 'old'"));
        Assert.Equal(2L, Scalar<long>(connection, "SELECT COUNT(*) FROM part WHERE session_id = 'old'"));
        Assert.Equal(3L, Scalar<long>(connection, "SELECT COUNT(*) FROM event WHERE aggregate_id = 'old' AND type LIKE 'message.%'"));
        Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM message WHERE id = 'old-user'"));
        Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM message WHERE id = 'old-assistant'"));
        Assert.Equal(2L, Scalar<long>(connection, """
            SELECT COUNT(*)
            FROM message AS m
            WHERE m.session_id = 'old'
              AND (SELECT COUNT(*)
                  FROM part AS p
                  WHERE p.message_id = m.id
                    AND json_extract(p.data, '$.type') = 'text'
                    AND json_extract(p.data, '$.text') = '<cleaned>'
                    AND json_type(p.data, '$.synthetic') IS NULL
                    AND json_type(p.data, '$.ignored') IS NULL) = 1
              AND (SELECT COUNT(*) FROM part AS p WHERE p.message_id = m.id) = 1
            """));
        Assert.Equal(2L, Scalar<long>(connection, """
            SELECT COUNT(*)
            FROM part
            WHERE session_id = 'old'
              AND json_extract(data, '$.type') = 'text'
              AND json_extract(data, '$.text') = '<cleaned>'
            """));
        Assert.Equal(
            "<cleaned>",
            JsonNode.Parse(Scalar<string>(connection, "SELECT data FROM part WHERE id = 'old-user-text'"))!["text"]!.GetValue<string>());
        Assert.Equal(
            "<cleaned>",
            JsonNode.Parse(Scalar<string>(connection, "SELECT data FROM event WHERE id = 'old-user-text-event'"))!["part"]!["text"]!.GetValue<string>());

        var retainedMessage = JsonNode.Parse(Scalar<string>(connection, "SELECT data FROM message WHERE id = 'old-user'"))!;
        var retainedMessageEvent = JsonNode.Parse(
            Scalar<string>(connection, "SELECT data FROM event WHERE id = 'old-user-event'"))!["info"]!;
        AssertUserMessageSanitized(retainedMessage);
        AssertUserMessageSanitized(retainedMessageEvent);
        var retainedAssistant = JsonNode.Parse(
            Scalar<string>(connection, "SELECT data FROM message WHERE id = 'old-assistant'"))!;
        AssertAssistantMessageSanitized(retainedAssistant);
        AssertAssistantMessageSanitized(JsonNode.Parse(
            Scalar<string>(connection, "SELECT data FROM event WHERE id = 'old-assistant-event'"))!["info"]!);
        Assert.Equal("old-user", retainedAssistant["parentID"]!.GetValue<string>());
        AssertCostReporterMetadataPreserved(retainedAssistant);
        Assert.Null(JsonNode.Parse(
            Scalar<string>(connection, "SELECT data FROM part WHERE id = 'old-user-text'"))!["metadata"]);
        Assert.Null(JsonNode.Parse(
            Scalar<string>(connection, "SELECT data FROM event WHERE id = 'old-user-text-event'"))!["part"]!["metadata"]);
        Assert.Equal("Original old title", Scalar<string>(connection, "SELECT title FROM session WHERE id = 'old'"));
        Assert.Equal(250L, Scalar<long>(connection, "SELECT time_updated FROM session WHERE id = 'old'"));
        Assert.Equal("Original old title", JsonNode.Parse(
            Scalar<string>(connection, "SELECT data FROM event WHERE id = 'old-session-created-event'"))!["info"]!["title"]!.GetValue<string>());
        Assert.Equal("Original old title", JsonNode.Parse(
            Scalar<string>(connection, "SELECT data FROM event WHERE id = 'old-session-updated-event'"))!["info"]!["title"]!.GetValue<string>());
        Assert.Equal(0L, Scalar<long>(connection, """
            SELECT (SELECT COUNT(*) FROM message WHERE data LIKE '%<REMOVED_MESSAGE_CONTENT>%') +
                   (SELECT COUNT(*) FROM part WHERE data LIKE '%<REMOVED_MESSAGE_CONTENT>%') +
                   (SELECT COUNT(*) FROM event WHERE data LIKE '%<REMOVED_MESSAGE_CONTENT>%')
            """));

        Assert.Equal(2L, Scalar<long>(connection, "SELECT COUNT(*) FROM message WHERE session_id = 'mixed'"));
        Assert.Equal("Original mixed title", Scalar<string>(connection, "SELECT title FROM session WHERE id = 'mixed'"));
        Assert.Equal("<cleaned>", JsonNode.Parse(
            Scalar<string>(connection, "SELECT data FROM part WHERE id = 'mixed-old-text'"))!["text"]!.GetValue<string>());
        Assert.Equal("new mixed content", JsonNode.Parse(
            Scalar<string>(connection, "SELECT data FROM part WHERE id = 'mixed-new-text'"))!["text"]!.GetValue<string>());
        Assert.Equal("<cleaned>", JsonNode.Parse(
            Scalar<string>(connection, "SELECT data FROM event WHERE id = 'mixed-user-text-event'"))!["part"]!["text"]!.GetValue<string>());
        Assert.Equal("new mixed content", JsonNode.Parse(
            Scalar<string>(connection, "SELECT data FROM event WHERE id = 'mixed-assistant-text-event'"))!["part"]!["text"]!.GetValue<string>());
        Assert.Equal(2L, Scalar<long>(connection, "SELECT COUNT(*) FROM message WHERE session_id = 'recent'"));
        Assert.Equal("stale durable content", JsonNode.Parse(
            Scalar<string>(connection, "SELECT data FROM event WHERE id = 'disagree-text-event'"))!["part"]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void CleanupCleansAllMessagesWhenCutoffIsUnbounded()
    {
        CreateDatabase();

        using (var database = new OpenCodeDb(_databasePath))
        {
            var preview = database.BuildCleanupPreview(long.MaxValue);

            Assert.Equal(7, preview.MessageCount);
            Assert.Equal(preview.ActionCount, database.ApplyCleanup(preview, long.MaxValue));
            Assert.Equal(0, database.BuildCleanupPreview(long.MaxValue).ActionCount);
        }

        using var connection = OpenConnection();
        Assert.Equal(7L, Scalar<long>(connection, "SELECT COUNT(*) FROM message"));
        Assert.Equal(7L, Scalar<long>(connection, "SELECT COUNT(*) FROM part"));
        Assert.Equal(7L, Scalar<long>(connection, """
            SELECT COUNT(*)
            FROM message AS m
            WHERE (SELECT COUNT(*)
                   FROM part AS p
                   WHERE p.message_id = m.id
                     AND json_extract(p.data, '$.type') = 'text'
                     AND json_extract(p.data, '$.text') = '<cleaned>') = 1
              AND (SELECT COUNT(*) FROM part AS p WHERE p.message_id = m.id) = 1
            """));
        Assert.Equal(7L, Scalar<long>(connection, "SELECT COUNT(*) FROM event WHERE type = 'message.updated.1'"));
        Assert.Equal(6L, Scalar<long>(connection, """
            SELECT COUNT(*)
            FROM event
            WHERE type = 'message.part.updated.1'
              AND json_extract(data, '$.part.type') = 'text'
              AND json_extract(data, '$.part.text') = '<cleaned>'
            """));
        Assert.Equal(4L, Scalar<long>(connection, "SELECT COUNT(*) FROM session"));
        Assert.Equal("Original recent title", Scalar<string>(connection, "SELECT title FROM session WHERE id = 'recent'"));
    }

    [Fact]
    public void CleanupRemovesAssistantContentMetadataAndPreservesOperationalMetadata()
    {
        CreateDatabase();
        using (var connection = OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE message
                SET data = '{"role":"assistant","structured":{"answer":"private"},"error":{"data":{"message":"private error"}},"path":{"cwd":"private cwd","root":"private root"},"modelID":"test-model","providerID":"test-provider","cost":1.25,"tokens":{"input":10,"output":20}}'
                WHERE id = 'old-user';
                UPDATE event
                SET data = '{"info":{"id":"old-user","sessionID":"old","role":"assistant","structured":{"answer":"private"},"error":{"data":{"message":"private error"}},"path":{"cwd":"private cwd","root":"private root"},"modelID":"test-model","providerID":"test-provider","cost":1.25,"tokens":{"input":10,"output":20},"time":{"created":100}}}'
                WHERE id = 'old-user-event';
                """;
            command.ExecuteNonQuery();
        }

        using (var database = new OpenCodeDb(_databasePath))
        {
            var preview = database.BuildCleanupPreview(500);
            database.ApplyCleanup(preview, 500);
        }

        using var verificationConnection = OpenConnection();
        AssertAssistantMessageSanitized(JsonNode.Parse(
            Scalar<string>(verificationConnection, "SELECT data FROM message WHERE id = 'old-user'"))!);
        AssertAssistantMessageSanitized(JsonNode.Parse(
            Scalar<string>(verificationConnection, "SELECT data FROM event WHERE id = 'old-user-event'"))!["info"]!);
        AssertOperationalMetadataPreserved(JsonNode.Parse(
            Scalar<string>(verificationConnection, "SELECT data FROM message WHERE id = 'old-user'"))!);
        AssertOperationalMetadataPreserved(JsonNode.Parse(
            Scalar<string>(verificationConnection, "SELECT data FROM event WHERE id = 'old-user-event'"))!["info"]!);
    }

    [Fact]
    public void CleanupCleansAnOldProjectionMessageWhenUnrelatedDurableEventsRemain()
    {
        CreateDatabase();
        using (var connection = OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE message SET time_created = 100, time_updated = 100 WHERE session_id = 'disagree';
                UPDATE part SET time_created = 100, time_updated = 100 WHERE session_id = 'disagree';
                UPDATE event
                SET data = json_set(data, '$.info.id', 'durable-user')
                WHERE id = 'disagree-event';
                UPDATE event
                SET data = json_set(data, '$.part.id', 'durable-text', '$.part.messageID', 'durable-user')
                WHERE id = 'disagree-text-event';
                """;
            command.ExecuteNonQuery();
        }

        using (var database = new OpenCodeDb(_databasePath))
        {
            var preview = database.BuildCleanupPreview(500);

            Assert.Equal(4, preview.MessageCount);
            Assert.Equal(12, preview.ActionCount);
            Assert.Equal(12, database.ApplyCleanup(preview, 500));

            var secondPreview = database.BuildCleanupPreview(500);
            Assert.Equal(0, secondPreview.ActionCount);
        }

        using var verificationConnection = OpenConnection();
        Assert.Equal(2L, Scalar<long>(verificationConnection, "SELECT COUNT(*) FROM message WHERE session_id = 'old'"));
        Assert.Equal(2L, Scalar<long>(verificationConnection, "SELECT COUNT(*) FROM part WHERE session_id = 'old'"));
        Assert.Equal(1L, Scalar<long>(verificationConnection, "SELECT COUNT(*) FROM message WHERE session_id = 'disagree'"));
        Assert.Equal(1L, Scalar<long>(verificationConnection, "SELECT COUNT(*) FROM part WHERE session_id = 'disagree'"));
        Assert.Equal(2L, Scalar<long>(verificationConnection, "SELECT COUNT(*) FROM event WHERE aggregate_id = 'disagree'"));
        Assert.Equal("Original disagree title", Scalar<string>(verificationConnection, "SELECT title FROM session WHERE id = 'disagree'"));
        Assert.Equal(
            "<cleaned>",
            JsonNode.Parse(Scalar<string>(verificationConnection, "SELECT data FROM part WHERE id = 'disagree-text'"))!["text"]!.GetValue<string>());
        Assert.Equal(
            "stale durable content",
            JsonNode.Parse(Scalar<string>(verificationConnection, "SELECT data FROM event WHERE id = 'disagree-text-event'"))!["part"]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void CleanupRejectsMessageEventsWithoutAnIntegerCreationTime()
    {
        CreateDatabase();
        using (var connection = OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE event SET data = json_remove(data, '$.info.time.created') WHERE id = 'old-user-event'";
            command.ExecuteNonQuery();
        }

        using var database = new OpenCodeDb(_databasePath);
        Assert.Throws<InvalidDataException>(() => database.BuildCleanupPreview(500));
    }

    [Fact]
    public void CleanupRetainsEveryMessageRelationshipAndAddsEveryMissingPlaceholder()
    {
        CreateDatabase();
        using (var connection = OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO message VALUES
                    ('old-middle', 'old', 150, 150, '{"role":"assistant","parentID":"old-user","path":{"cwd":"private cwd","root":"private root"}}');
                INSERT INTO part VALUES
                    ('old-middle-reasoning', 'old-middle', 'old', 150, 150, '{"type":"reasoning","text":"private reasoning"}');
                """;
            command.ExecuteNonQuery();
        }

        using (var database = new OpenCodeDb(_databasePath))
        {
            var preview = database.BuildCleanupPreview(500);
            database.ApplyCleanup(preview, 500);
            Assert.Equal(0, database.BuildCleanupPreview(500).ActionCount);
        }

        using var verificationConnection = OpenConnection();
        Assert.Equal(3L, Scalar<long>(
            verificationConnection,
            "SELECT COUNT(*) FROM message WHERE session_id = 'old'"));
        Assert.Equal(3L, Scalar<long>(verificationConnection, """
            SELECT COUNT(*)
            FROM message AS m
            WHERE m.session_id = 'old'
              AND (SELECT COUNT(*)
                   FROM part AS p
                   WHERE p.message_id = m.id
                     AND p.session_id = m.session_id
                     AND json_extract(p.data, '$.type') = 'text'
                     AND json_extract(p.data, '$.text') = '<cleaned>') = 1
              AND (SELECT COUNT(*) FROM part AS p WHERE p.message_id = m.id) = 1
            """));
        Assert.Equal("old-user", JsonNode.Parse(Scalar<string>(
            verificationConnection,
            "SELECT data FROM message WHERE id = 'old-middle'"))!["parentID"]!.GetValue<string>());
        Assert.Equal("old", Scalar<string>(
            verificationConnection,
            "SELECT session_id FROM message WHERE id = 'old-middle'"));
    }

    [Fact]
    public void CleanupConvertsSyntheticOnlyAnchorToOrdinaryPlaceholder()
    {
        CreateDatabase();
        using (var connection = OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE part
                SET data = json_set(data, '$.synthetic', json('true'))
                WHERE id = 'old-user-text';
                UPDATE event
                SET data = json_set(data, '$.part.synthetic', json('true'))
                WHERE id = 'old-user-text-event';
                """;
            command.ExecuteNonQuery();
        }

        using (var database = new OpenCodeDb(_databasePath))
        {
            var preview = database.BuildCleanupPreview(500);
            database.ApplyCleanup(preview, 500);
            Assert.Equal(0, database.BuildCleanupPreview(500).ActionCount);
        }

        using var verificationConnection = OpenConnection();
        var retainedPart = JsonNode.Parse(Scalar<string>(
            verificationConnection,
            "SELECT data FROM part WHERE id = 'old-user-text'"))!;
        Assert.Equal("<cleaned>", retainedPart["text"]!.GetValue<string>());
        Assert.Null(retainedPart["synthetic"]);
    }

    [Fact]
    public void CleanupLeavesEventOnlyHistoryUntouched()
    {
        CreateDatabase();
        using (var connection = OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO message VALUES
                    ('current-only-user', 'old', 50, 50, '{"role":"user"}');
                INSERT INTO part VALUES
                    ('current-only-text', 'current-only-user', 'old', 50, 50, '{"type":"text","text":"current-only private content"}');
                INSERT INTO event VALUES
                    ('durable-only-message-event', 'old', 6, 'message.updated.1', '{"info":{"id":"durable-only-message","sessionID":"old","role":"assistant","time":{"created":150}}}'),
                    ('durable-only-text-event', 'old', 7, 'message.part.updated.1', '{"part":{"id":"durable-only-text","messageID":"durable-only-message","sessionID":"old","type":"text","text":"durable-only private content"}}');
                """;
            command.ExecuteNonQuery();
        }

        using (var database = new OpenCodeDb(_databasePath))
        {
            var preview = database.BuildCleanupPreview(500);

            database.ApplyCleanup(preview, 500);
        }

        using var verificationConnection = OpenConnection();
        Assert.Equal(
            "<cleaned>",
            JsonNode.Parse(Scalar<string>(
                verificationConnection,
                "SELECT data FROM part WHERE id = 'current-only-text'"))!["text"]!.GetValue<string>());
        Assert.Equal(2L, Scalar<long>(
            verificationConnection,
            "SELECT COUNT(*) FROM event WHERE id IN ('durable-only-message-event', 'durable-only-text-event')"));
    }

    [Fact]
    public void CleanupRepairsPreviouslyCleanedSessionWithPlaceholderOnNewestMessage()
    {
        CreateDatabase();
        using (var database = new OpenCodeDb(_databasePath))
        {
            var preview = database.BuildCleanupPreview(500);
            database.ApplyCleanup(preview, 500);
        }

        using (var connection = OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                DELETE FROM part WHERE message_id = 'old-assistant';
                UPDATE message SET data = '{"role":"user"}' WHERE id = 'old-assistant';
                INSERT INTO part VALUES
                    ('old-assistant-hidden-text', 'old-assistant', 'old', 200, 200, '{"type":"text","text":"hidden private content","synthetic":true}');
                """;
            command.ExecuteNonQuery();
        }

        using (var database = new OpenCodeDb(_databasePath))
        {
            var repairPreview = database.BuildCleanupPreview(500);

            Assert.Equal(3, repairPreview.MessageCount);
            Assert.Equal(1, repairPreview.ProjectionRowCount);
            Assert.Equal(1, repairPreview.ActionCount);
            Assert.Equal(1, database.ApplyCleanup(repairPreview, 500));
            Assert.Equal(0, database.BuildCleanupPreview(500).ActionCount);
        }

        using var verificationConnection = OpenConnection();
        Assert.Equal(1L, Scalar<long>(
            verificationConnection,
            "SELECT COUNT(*) FROM part WHERE message_id = 'old-assistant'"));
        Assert.Equal(
            "old-assistant-hidden-text",
            Scalar<string>(verificationConnection, "SELECT id FROM part WHERE message_id = 'old-assistant'"));
        Assert.Equal(
            "<cleaned>",
            JsonNode.Parse(Scalar<string>(
                verificationConnection,
                "SELECT data FROM part WHERE message_id = 'old-assistant'"))!["text"]!.GetValue<string>());
        Assert.Null(JsonNode.Parse(Scalar<string>(
            verificationConnection,
            "SELECT data FROM part WHERE message_id = 'old-assistant'"))!["synthetic"]);
    }

    [Fact]
    public void CleanupLeavesIncorrectlyAssignedSessionEventsUntouched()
    {
        CreateDatabase();
        using (var connection = OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE event
                SET data = json_set(data, '$.sessionID', 'another-session')
                WHERE id = 'old-session-updated-event'
                """;
            command.ExecuteNonQuery();
        }

        using var database = new OpenCodeDb(_databasePath);
        Assert.Equal(11, database.BuildCleanupPreview(500).ActionCount);
    }

    [Fact]
    public void CleanupRejectsMessagesAssignedToMissingSessions()
    {
        CreateDatabase();
        using (var connection = OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM session WHERE id = 'old'";
            command.ExecuteNonQuery();
        }

        using var database = new OpenCodeDb(_databasePath);
        Assert.Throws<InvalidDataException>(() => database.BuildCleanupPreview(500));
    }

    [Fact]
    public void CleanupRejectsMessageChangesAfterPreview()
    {
        CreateDatabase();
        using var database = new OpenCodeDb(_databasePath);
        var preview = database.BuildCleanupPreview(500);

        using (var connection = OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE part
                SET time_updated = 101
                WHERE id = 'old-user-text'
                """;
            command.ExecuteNonQuery();
        }

        Assert.Throws<InvalidOperationException>(() => database.ApplyCleanup(preview, 500));
    }

    [Fact]
    public void CleanupSkipsSessionsWithoutCurrentMessages()
    {
        CreateDatabase();
        using (var connection = OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO event VALUES
                    ('empty-message-event', 'empty', 1, 'message.updated.1', '{"info":{"id":"empty-message","sessionID":"empty","role":"user","time":{"created":100}}}'),
                    ('empty-text-event', 'empty', 2, 'message.part.updated.1', '{"part":{"id":"empty-text","messageID":"empty-message","sessionID":"empty","type":"text","text":"historical content"}}');
                """;
            command.ExecuteNonQuery();
        }

        using (var database = new OpenCodeDb(_databasePath))
        {
            var preview = database.BuildCleanupPreview(500);

            Assert.Equal(3, preview.MessageCount);
            Assert.Equal(5, preview.EventRowCount);
            Assert.Equal(11, database.ApplyCleanup(preview, 500));
        }

        using var verificationConnection = OpenConnection();
        Assert.Equal(2L, Scalar<long>(verificationConnection, "SELECT COUNT(*) FROM event WHERE aggregate_id = 'empty'"));
        Assert.Equal(
            "historical content",
            JsonNode.Parse(Scalar<string>(verificationConnection, "SELECT data FROM event WHERE id = 'empty-text-event'"))!["part"]!["text"]!.GetValue<string>());
    }

    public void Dispose()
    {
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    private void CreateDatabase()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE message (
                id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                time_created INTEGER NOT NULL,
                time_updated INTEGER NOT NULL,
                data TEXT NOT NULL);
            CREATE TABLE part (
                id TEXT PRIMARY KEY,
                message_id TEXT NOT NULL,
                session_id TEXT NOT NULL,
                time_created INTEGER NOT NULL,
                time_updated INTEGER NOT NULL,
                data TEXT NOT NULL);
            CREATE TABLE event (
                id TEXT PRIMARY KEY,
                aggregate_id TEXT NOT NULL,
                seq INTEGER NOT NULL,
                type TEXT NOT NULL,
                data TEXT NOT NULL);
            CREATE TABLE session (
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                time_updated INTEGER NOT NULL);

            INSERT INTO session VALUES
                ('old', 'Original old title', 250),
                ('mixed', 'Original mixed title', 800),
                ('recent', 'Original recent title', 800),
                ('disagree', 'Original disagree title', 900);

            INSERT INTO message VALUES
                ('old-user', 'old', 100, 100, '{"role":"user","system":"private instructions","format":{"type":"json_schema","schema":{"secret":"private"}},"summary":{"title":"private title","body":"private body","diffs":[{"file":"secret.txt","before":"before","after":"after","patch":"patch"}]},"modelID":"test-model","providerID":"test-provider","cost":1.25,"tokens":{"input":10,"output":20}}'),
                ('old-assistant', 'old', 200, 200, '{"role":"assistant","parentID":"old-user","structured":{"answer":"private"},"error":{"data":{"message":"private error"}},"path":{"cwd":"private cwd","root":"private root"},"modelID":"cost-model","providerID":"cost-provider","cost":2.5,"tokens":{"input":30,"output":40,"cache":{"read":50,"write":60}},"time":{"created":200,"completed":250}}'),
                ('mixed-user', 'mixed', 100, 100, '{"role":"user"}'),
                ('mixed-assistant', 'mixed', 800, 800, '{"role":"assistant"}'),
                ('recent-user', 'recent', 700, 700, '{"role":"user"}'),
                ('recent-assistant', 'recent', 800, 800, '{"role":"assistant"}'),
                ('disagree-user', 'disagree', 900, 900, '{"role":"user"}');

            INSERT INTO part VALUES
                ('old-user-text', 'old-user', 'old', 100, 100, '{"type":"text","text":"old private content","metadata":{"private":true}}'),
                ('old-tool', 'old-assistant', 'old', 200, 200, '{"type":"tool","state":{"output":"private output"}}'),
                ('mixed-old-text', 'mixed-user', 'mixed', 100, 100, '{"type":"text","text":"old mixed content"}'),
                ('mixed-new-text', 'mixed-assistant', 'mixed', 800, 800, '{"type":"text","text":"new mixed content"}'),
                ('recent-user-text', 'recent-user', 'recent', 700, 700, '{"type":"text","text":"recent question"}'),
                ('recent-assistant-text', 'recent-assistant', 'recent', 800, 800, '{"type":"text","text":"recent answer"}'),
                ('disagree-text', 'disagree-user', 'disagree', 900, 900, '{"type":"text","text":"current content"}');

            INSERT INTO event VALUES
                ('old-session-created-event', 'old', -1, 'session.created.1', '{"sessionID":"old","info":{"id":"old","title":"Original old title"}}'),
                ('old-user-event', 'old', 1, 'message.updated.1', '{"info":{"id":"old-user","sessionID":"old","role":"user","system":"private instructions","format":{"type":"json_schema","schema":{"secret":"private"}},"summary":{"title":"private title","body":"private body","diffs":[{"file":"secret.txt","before":"before","after":"after","patch":"patch"}]},"modelID":"test-model","providerID":"test-provider","cost":1.25,"tokens":{"input":10,"output":20},"time":{"created":100}}}'),
                ('old-user-text-event', 'old', 2, 'message.part.updated.1', '{"part":{"id":"old-user-text","messageID":"old-user","sessionID":"old","type":"text","text":"old private content","metadata":{"private":true}}}'),
                ('old-assistant-event', 'old', 3, 'message.updated.1', '{"info":{"id":"old-assistant","sessionID":"old","role":"assistant","structured":{"answer":"private"},"time":{"created":200}}}'),
                ('old-tool-event', 'old', 4, 'message.part.updated.1', '{"part":{"id":"old-tool","messageID":"old-assistant","sessionID":"old","type":"tool","state":{"output":"private output"}}}'),
                ('old-session-updated-event', 'old', 5, 'session.updated.1', '{"sessionID":"old","info":{"id":"old","title":"Original old title"}}'),
                ('mixed-user-event', 'mixed', 1, 'message.updated.1', '{"info":{"id":"mixed-user","sessionID":"mixed","role":"user","time":{"created":100}}}'),
                ('mixed-user-text-event', 'mixed', 2, 'message.part.updated.1', '{"part":{"id":"mixed-old-text","messageID":"mixed-user","sessionID":"mixed","type":"text","text":"old mixed content"}}'),
                ('mixed-assistant-event', 'mixed', 3, 'message.updated.1', '{"info":{"id":"mixed-assistant","sessionID":"mixed","role":"assistant","time":{"created":800}}}'),
                ('mixed-assistant-text-event', 'mixed', 4, 'message.part.updated.1', '{"part":{"id":"mixed-new-text","messageID":"mixed-assistant","sessionID":"mixed","type":"text","text":"new mixed content"}}'),
                ('recent-user-event', 'recent', 1, 'message.updated.1', '{"info":{"id":"recent-user","sessionID":"recent","role":"user","time":{"created":700}}}'),
                ('recent-user-text-event', 'recent', 2, 'message.part.updated.1', '{"part":{"id":"recent-user-text","messageID":"recent-user","sessionID":"recent","type":"text","text":"recent question"}}'),
                ('recent-assistant-event', 'recent', 3, 'message.updated.1', '{"info":{"id":"recent-assistant","sessionID":"recent","role":"assistant","time":{"created":800}}}'),
                ('recent-assistant-text-event', 'recent', 4, 'message.part.updated.1', '{"part":{"id":"recent-assistant-text","messageID":"recent-assistant","sessionID":"recent","type":"text","text":"recent answer"}}'),
                ('disagree-event', 'disagree', 1, 'message.updated.1', '{"info":{"id":"disagree-user","sessionID":"disagree","role":"user","time":{"created":100}}}'),
                ('disagree-text-event', 'disagree', 2, 'message.part.updated.1', '{"part":{"id":"disagree-text","messageID":"disagree-user","sessionID":"disagree","type":"text","text":"stale durable content"}}');
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection($"Data Source={_databasePath};Pooling=False");
        connection.Open();
        return connection;
    }

    private static T Scalar<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)command.ExecuteScalar()!;
    }

    private static void AssertUserMessageSanitized(JsonNode message)
    {
        Assert.Equal("user", message["role"]!.GetValue<string>());
        Assert.Null(message["system"]);
        Assert.Null(message["summary"]);
        Assert.Equal("json_schema", message["format"]!["type"]!.GetValue<string>());
        Assert.Empty(message["format"]!["schema"]!.AsObject());
        AssertOperationalMetadataPreserved(message);
    }

    private static void AssertAssistantMessageSanitized(JsonNode message)
    {
        Assert.Equal("assistant", message["role"]!.GetValue<string>());
        Assert.Null(message["structured"]);
        Assert.Null(message["error"]);
        Assert.Equal(string.Empty, message["path"]!["cwd"]!.GetValue<string>());
        Assert.Equal(string.Empty, message["path"]!["root"]!.GetValue<string>());
    }

    private static void AssertCostReporterMetadataPreserved(JsonNode message)
    {
        Assert.Equal("cost-model", message["modelID"]!.GetValue<string>());
        Assert.Equal("cost-provider", message["providerID"]!.GetValue<string>());
        Assert.Equal(2.5, message["cost"]!.GetValue<double>());
        Assert.Equal(30, message["tokens"]!["input"]!.GetValue<int>());
        Assert.Equal(40, message["tokens"]!["output"]!.GetValue<int>());
        Assert.Equal(50, message["tokens"]!["cache"]!["read"]!.GetValue<int>());
        Assert.Equal(60, message["tokens"]!["cache"]!["write"]!.GetValue<int>());
        Assert.Equal(200, message["time"]!["created"]!.GetValue<int>());
        Assert.Equal(250, message["time"]!["completed"]!.GetValue<int>());
    }

    private static void AssertOperationalMetadataPreserved(JsonNode message)
    {
        Assert.Equal("test-model", message["modelID"]!.GetValue<string>());
        Assert.Equal("test-provider", message["providerID"]!.GetValue<string>());
        Assert.Equal(1.25, message["cost"]!.GetValue<double>());
        Assert.Equal(10, message["tokens"]!["input"]!.GetValue<int>());
        Assert.Equal(20, message["tokens"]!["output"]!.GetValue<int>());
    }
}

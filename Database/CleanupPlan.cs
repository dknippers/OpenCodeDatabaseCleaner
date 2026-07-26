namespace OpenCodeDatabaseCleaner.Database;

public sealed class CleanupPreview
{
    private readonly HashSet<ContentActionTarget> _targets;

    internal CleanupPreview(CleanupPlan plan)
    {
        MessageCount = plan.MessageCount;
        ProjectionRowCount = plan.ProjectionRowCount;
        EventRowCount = plan.EventRowCount;
        _targets = plan.Actions.Select(ContentActionTarget.FromAction).ToHashSet();
    }

    public int MessageCount { get; }
    public int ProjectionRowCount { get; }
    public int EventRowCount { get; }
    public int ActionCount => _targets.Count;

    internal bool Matches(CleanupPlan plan) =>
        ActionCount == plan.ActionCount &&
        _targets.SetEquals(plan.Actions.Select(ContentActionTarget.FromAction));
}

internal sealed class CleanupPlan
{
    private readonly HashSet<string> _messageIds;
    private readonly List<ContentAction> _actions = [];

    internal CleanupPlan(IEnumerable<string> messageIds) =>
        _messageIds = messageIds.ToHashSet(StringComparer.Ordinal);

    internal int MessageCount => _messageIds.Count;
    internal int ProjectionRowCount => _actions.Count(action => action.Table is "message" or "part");
    internal int EventRowCount => _actions.Count(action => action.Table == "event");
    internal int ActionCount => _actions.Count;

    internal IReadOnlyList<ContentAction> Actions => _actions;

    internal void AddUpdate(string sessionId, string table, string rowId, long? timeUpdated, string data)
    {
        _actions.Add(new ContentAction(ContentActionKind.Update, sessionId, table, rowId, null, null, timeUpdated, data));
    }

    internal void AddDelete(string sessionId, string table, string rowId, long? timeUpdated)
    {
        _actions.Add(new ContentAction(ContentActionKind.Delete, sessionId, table, rowId, null, null, timeUpdated, null));
    }

    internal void AddPartInsert(
        string sessionId,
        string rowId,
        string messageId,
        long timeCreated,
        long timeUpdated,
        string data)
    {
        _actions.Add(new ContentAction(
            ContentActionKind.Insert,
            sessionId,
            "part",
            rowId,
            messageId,
            timeCreated,
            timeUpdated,
            data));
    }
}

internal enum ContentActionKind
{
    Insert,
    Update,
    Delete
}

internal sealed record ContentAction(
    ContentActionKind Kind,
    string SessionId,
    string Table,
    string RowId,
    string? MessageId,
    long? TimeCreated,
    long? TimeUpdated,
    string? Data);

internal sealed record ContentActionTarget(
    ContentActionKind Kind,
    string SessionId,
    string Table,
    string RowId,
    string? MessageId,
    long? TimeCreated,
    long? TimeUpdated,
    string? UnversionedData)
{
    public static ContentActionTarget FromAction(ContentAction action) =>
        new(
            action.Kind,
            action.SessionId,
            action.Table,
            action.RowId,
            action.MessageId,
            action.TimeCreated,
            action.TimeUpdated,
            action.Kind == ContentActionKind.Insert ||
            action.Table == "event" && action.Kind == ContentActionKind.Update
                ? action.Data
                : null);
}

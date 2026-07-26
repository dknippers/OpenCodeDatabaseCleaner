using OpenCodeDatabaseCleaner.Database;
using System.Globalization;

if (args.Length == 0)
{
    PrintUsage();
    return 0;
}

string? databasePath = null;
int? days = null;
var dryRun = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--days":
            if (i + 1 >= args.Length ||
                !int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedDays) ||
                parsedDays < 0)
            {
                return Fail("--days must be a non-negative whole number (0 cleans all messages).");
            }

            days = parsedDays;
            break;

        case "--db":
            if (i + 1 >= args.Length)
            {
                return Fail("--db must be followed by a database path.");
            }

            databasePath = args[++i];
            break;

        case "--dry-run":
            dryRun = true;
            break;

        case "--help" or "-h":
            PrintUsage();
            return 0;

        default:
            return Fail($"Unknown argument: {args[i]}");
    }
}

if (!days.HasValue)
{
    return Fail("No age specified. Use --days N.");
}

databasePath ??= Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    ".local", "share", "opencode", "opencode.db");

try
{
    var cutoffMilliseconds = days.Value == 0
        ? long.MaxValue
        : DateTimeOffset.UtcNow.AddDays(-days.Value).ToUnixTimeMilliseconds();
    using var database = new OpenCodeDb(databasePath);
    var preview = database.BuildCleanupPreview(cutoffMilliseconds);

    Console.WriteLine($"Database: {databasePath}");
    Console.WriteLine(days.Value == 0
        ? "Scope:    All messages"
        : $"Cutoff:   {DateTimeOffset.FromUnixTimeMilliseconds(cutoffMilliseconds):yyyy-MM-dd HH:mm:ss 'UTC'} (messages older than {days.Value} days)");
    Console.WriteLine();
    Console.WriteLine($"Messages selected for cleanup:       {preview.MessageCount:N0}");
    Console.WriteLine($"Current message/part rows to change: {preview.ProjectionRowCount:N0}");
    Console.WriteLine($"Historical event rows to change:     {preview.EventRowCount:N0}");

    if (dryRun)
    {
        Console.WriteLine();
        Console.WriteLine("Dry run only. Nothing was changed.");
        return 0;
    }

    if (preview.ActionCount == 0)
    {
        Console.WriteLine();
        Console.WriteLine("No cleanable message content was found. Nothing was changed.");
        database.CheckpointWal();
        return 0;
    }

    Console.WriteLine();
    Console.WriteLine(days.Value == 0
        ? "This permanently cleans all messages."
        : "This permanently cleans every message older than the cutoff.");
    Console.WriteLine("Every cleaned message gets a visible placeholder; session titles are unchanged.");
    Console.WriteLine("Per-message token and cost metadata is retained.");
    Console.Write("Type REMOVE to continue: ");

    if (!string.Equals(Console.ReadLine(), "REMOVE", StringComparison.Ordinal))
    {
        Console.WriteLine("Cleanup cancelled. Nothing was changed.");
        return 0;
    }

    var changedRows = database.ApplyCleanup(preview, cutoffMilliseconds);
    Console.WriteLine();
    Console.WriteLine($"Cleaned {preview.MessageCount:N0} messages by changing {changedRows:N0} database rows.");

    try
    {
        database.CheckpointWal();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("Messages were cleaned, but old data may remain in the SQLite WAL file.");
        Console.Error.WriteLine($"Close OpenCode and run the cleanup again: {ex.Message}");
        return 1;
    }

    try
    {
        Console.WriteLine("Reclaiming unused database space...");
        database.Vacuum();
        Console.WriteLine("Database vacuum completed.");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Messages were cleaned, but the database could not be vacuumed: {ex.Message}");
        return 1;
    }

    return 0;
}
catch (FileNotFoundException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Cleanup failed: {ex.Message}");
    return 1;
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    Console.Error.WriteLine();
    PrintUsage();
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("""
        Usage: OpenCodeDatabaseCleaner --days N [options]

        Cleans individual OpenCode messages older than N days. Use 0 to clean all messages.
        Every cleaned message displays <cleaned>; session titles are unchanged.

        Options:
          --days N       Minimum message age in whole days (0 cleans all messages)
          --db PATH      Path to opencode.db
                         (default: <home>/.local/share/opencode/opencode.db)
          --dry-run      Show how many messages would be affected without changing the database
          --help, -h     Show this help

        Running without arguments only shows this help. Applying a non-empty cleanup requires typing
        REMOVE after the preview is displayed, then attempts to reclaim unused disk space automatically.
        Close OpenCode before running cleanup.
        Databases containing OpenCode's newer V2 message storage are rejected rather than partially cleaned.
        """);
}

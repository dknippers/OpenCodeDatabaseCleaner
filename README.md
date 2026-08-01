# OpenCode Database Cleaner

Cleans selected messages in OpenCode's local SQLite history to visible placeholders while retaining per-message usage metadata.

OpenCode V1 stores current messages in `message` and `part`, with historical copies in `event`. This utility retains and sanitizes every current V1 `message` row older than the cutoff so message identities, parent relationships, tokens, cost, model identifiers, roles, and timestamps remain available. Every retained message has exactly one ordinary text part containing `<cleaned>`; the cleaner prefers its first non-synthetic text part, falls back to its first synthetic text part, or generates one, then deletes its other parts. Required V1 message fields remain structurally valid with sensitive values emptied. Matching durable V1 message and part events are sanitized or rewritten as `<cleaned>` text placeholders to mirror the retained message and placeholder part; no `event` row is ever deleted, so the event sequence stays contiguous. Session rows and session events are not changed. V2 data can coexist in the database but is never modified.

A current message is eligible when its `time_created` value is older than the cutoff. With `--days 0`, every current message is eligible regardless of timestamp. Newer messages in the same session are left untouched when the cutoff is non-zero, so a long-running session can retain recent history while its old messages display `<cleaned>`.

## Usage

```text
OpenCodeDatabaseCleaner --days N [options]

Options:
  --days N       Minimum message age in whole days (0 cleans all messages)
  --db PATH      Path to opencode.db
  --dry-run      Preview the cleanup without changing the database
  --help, -h     Show help
```

The default database is `.local/share/opencode/opencode.db` under the user's home directory (`%USERPROFILE%` on Windows). Running without arguments only displays help.

Preview messages older than 30 days:

```powershell
dotnet run -- --days 30 --dry-run
```

Clean messages older than 30 days and attempt to reclaim the freed disk space:

```powershell
dotnet run -- --days 30
```

Clean every current message, regardless of age:

```powershell
dotnet run -- --days 0
```

Before any cleanup, the utility displays the number of selected V1 messages, the number of V2 `session_message` rows matching the same date filter that will be skipped, current message and part rows to change, and historical event rows to change. The current-row count covers every update and deletion in V1 `message` and `part`. It proceeds only when `REMOVE` is entered exactly. The result repeats both the cleaned V1 message count and skipped matching V2 message count.

## Safety

- Close OpenCode before running cleanup.
- Back up the database if the history matters; cleanup cannot be undone, original part content is deleted, non-retained event content is replaced with `<cleaned>` placeholders, and sensitive fields are removed from every retained message row.
- `PRAGMA secure_delete` is enabled. After a committed cleanup, the utility truncates the WAL; if that fails, it reports that old data may remain there and exits with an error even though the cleanup is already committed.
- After a successful WAL checkpoint, the utility vacuums the database. This can be slow and temporarily needs additional free disk space. A vacuum failure is reported after the cleanup has already committed.
- V2 `session_message` rows, `session_input` rows, and `session.next.*` events are ignored and left unchanged. The skipped-message statistic counts only V2 `session_message` rows that match the date filter; it does not imply that any V2 content was cleaned.
- Other malformed V1 message, part, and message-event data is rejected rather than partially cleaned. Messages without a text part receive a generated placeholder.
- Durable events matching an old current message are cleaned with it. Non-retained part events are rewritten to `<cleaned>` text placeholders rather than deleted, so `event` rows and sequence numbers are preserved. Event-only history and session events are not changed. If durable events are replicated elsewhere, replaying an original copy can restore deleted content or parts; clean or disable every replica first. Replaying rewritten events on a replica may reproduce multiple `<cleaned>` text parts per message; conversation content is removed either way.
- OpenCode snapshot files outside the database are not changed.

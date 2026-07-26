# OpenCode Database Cleaner Agent Guide

## Scope

This file applies to the entire repository. The project is intentionally small and single-purpose. Prefer direct changes over abstractions that are not required by the cleanup contract.

## Purpose

OpenCode Database Cleaner is a synchronous .NET 10 command-line tool that destructively cleans old OpenCode V1 messages in a local SQLite database.

For each eligible message, the required result is:

- Every current `message` row retained and sanitized as a metadata record.
- Exactly one ordinary text `part` per current message containing `<cleaned>`: retain and sanitize the earliest non-synthetic text part, fall back to the earliest synthetic text part, or generate one when the message has no text part.
- Matching `message.updated.1` durable event rows sanitized as the retained message, and matching retained-text `message.part.updated.1` rows sanitized as the placeholder. Other V1 part events for that message are deleted.
- No other current parts for that message.
- Operational metadata on every current message, such as IDs, timestamps, role, model/provider identifiers, tokens, and cost, remains available.
- Conversation content and content-bearing metadata is deleted, not replaced with redaction markers.
- Session rows and session events are left unchanged.

This distinction matters: visible placeholders do not replace the current `message` rows. Every message remains as a metadata record so analytics tools can still read its usage data.

## Supported Storage

Only OpenCode V1 message storage is supported:

- Current projections are in `message` and `part`.
- Durable copies are in `event` with types `message.updated.1` and `message.part.updated.1`.

The cleaner deliberately rejects:

- Non-empty `session_message` or `session_input` tables.
- Any `session.next.*` events.
- Orphaned or incorrectly assigned parts.
- Malformed or incorrectly assigned V1 message events.

Do not weaken these checks or attempt partial V2 cleanup. A successful command must not imply content was removed when another supported-looking storage path still contains it.

## Repository Layout

| Path | Responsibility |
|---|---|
| `Program.cs` | CLI parsing, help, preview display, confirmation, cleanup invocation, and orchestration of WAL checkpoint and vacuum after cleanup. |
| `Database/OpenCodeDb.cs` | SQLite access, validation, per-message eligibility and anchor selection, action planning, transactional execution, concurrency checks, and the checkpoint/vacuum operations invoked by `Program`. |
| `Database/CleanupPlan.cs` | Cleanup actions and the immutable preview used to revalidate a confirmed operation. |
| `Database/ContentSanitizer.cs` | Parses retained message/part event JSON, removes known content-bearing metadata, creates the placeholder part, and extracts event identities. |
| `OpenCodeDatabaseCleaner.Tests/OpenCodeDbTests.cs` | End-to-end tests against temporary on-disk SQLite databases. The fixture documents the expected V1 schema and key edge cases. |
| `README.md` | User-facing behavior, usage, and operational warnings. Keep it aligned with behavior changes. |

Dependency flow is `Program` -> `OpenCodeDb` -> SQLite. `OpenCodeDb` also composes `CleanupPlan` for actions/previews and `ContentSanitizer` for JSON transformations; neither helper depends on SQLite.

## Cleanup Algorithm

`OpenCodeDb.BuildCleanupPlan` is the central orchestration method:

1. Validate that the database uses supported, internally consistent V1 storage.
2. Select every current message with `time_created` older than the cutoff. With `--days 0`, select every current message regardless of its timestamp. Newer messages in the same session remain untouched when the cutoff is non-zero.
3. Select each eligible message's earliest non-synthetic text part, falling back to its earliest synthetic text part.
4. Sanitize each eligible message. Retain and sanitize its selected text part and delete its other current parts.
5. Add a generated placeholder part to each eligible message without a text part.
6. Sanitize matching V1 message events and retained-part events; delete other V1 part events for the eligible message.

Do not extend cleanup to newer messages merely because they share a session with an eligible message.

## Sanitization Contract

`ContentSanitizer` transforms every current message row that survives cleanup. Retained text parts become exact placeholders; other parts and non-anchor events are deleted rather than individually redacted.

For every retained user message:

- Remove `system`.
- Remove `summary` entirely, including title, body, and diffs.
- Replace `format.schema` with an empty object when the format type is `json_schema`; the field is required by OpenCode's V1 schema.

For every retained assistant message:

- Remove `structured`.
- Remove `error` entirely.
- Replace required `path.cwd` and `path.root` values with empty strings.

For the retained text part:

- Set `text` to `<cleaned>`.
- Remove `metadata` entirely.

Apply the same transformation to matching durable event payloads. Preserve all other message properties, including operational metadata such as `modelID`, `providerID`, `cost`, and `tokens`.

Sanitization currently uses an explicit list of known content-bearing fields. When OpenCode adds or changes message fields, classify them deliberately and add tests. Unknown content-bearing fields are a privacy risk; do not silently assume they are harmless.

The old `<REMOVED_MESSAGE_CONTENT>` sentinel is not part of the design. Do not reintroduce it. Sensitive optional properties should be absent; required content-bearing containers must remain with empty values so retained JSON still satisfies OpenCode's V1 schema.

## Safety Invariants

- Build the full plan before writing anything.
- Apply all actions in one immediate transaction (`deferred: false`).
- Rebuild the plan inside that transaction and require it to match the user-confirmed preview.
- Keep `time_updated` equality checks on all `message` and `part` updates and deletes.
- Require every planned statement to affect exactly one row; otherwise roll back the entire transaction.
- Execute event actions before part actions and part actions before message actions.
- Keep `PRAGMA secure_delete = ON` when opening the database.
- After a committed cleanup, attempt WAL truncation and then vacuum only if the checkpoint succeeds. These operations can fail after the data transaction has already committed, so report their failures accurately.
- Continue rejecting malformed JSON, an unsupported role on any retained message, and a retained part that is not text.

The README tells users to close OpenCode before cleanup. Concurrency checks are still mandatory; instructions are not a substitute for enforcing consistency.

## Event Semantics

Event rows are durable historical copies, not disposable cache entries. Leaving matching message or retained-part events unsanitized would preserve deleted conversation content. Non-retained part events for an eligible message are deleted because its sanitized current message row retains the required operational metadata without duplicating it in durable history.

Only V1 message event types whose embedded message identity matches an eligible current message are changed. Session events and event-only history are not changed. If a new event type can contain conversation content, treat that as a storage-format change and update validation, planning, sanitization, tests, and documentation together.

The event schema does not enforce uniqueness by embedded message or part identity. Cleanup sanitizes every event row matching an eligible message or retained part, so duplicate matching event rows all remain.

Sessions represented only by events and no current `message` rows are skipped. Snapshot files and external event replicas are outside this tool's scope.

## Build And Test

Target framework: `net10.0`.

Restore dependencies when needed:

```powershell
dotnet restore
```

Compile without locking the executable output and filter warnings unless warnings are the subject of the task:

```powershell
dotnet msbuild /t:Compile | Select-String -Not ': warning'
```

Run all tests:

```powershell
dotnet test "OpenCodeDatabaseCleaner.Tests/OpenCodeDatabaseCleaner.Tests.csproj"
```

Show CLI help:

```powershell
dotnet run --project "OpenCodeDatabaseCleaner.csproj" -- --help
```

Preview a cleanup against a disposable or backed-up database:

```powershell
dotnet run --project "OpenCodeDatabaseCleaner.csproj" -- --days 30 --db "C:\path\to\opencode.db" --dry-run
```

Use `--days 0` to clean every current message, including messages with future timestamps.

Never run destructive manual tests against the user's real OpenCode database. Tests must use temporary fixtures or an explicit disposable copy.

## Tests

Tests use xUnit and real temporary SQLite files rather than mocks. `OpenCodeDbTests.CreateDatabase` creates the V1 schema and representative sessions:

- `old`: fully eligible messages.
- `mixed`: contains old and recent messages; only its old message is cleaned.
- `recent`: entirely recent and remains untouched.
- `disagree`: used to test an old current message alongside unrelated durable history.
- `empty`: added by a test to verify event-only sessions are skipped.

For cleanup behavior changes, test both current projection rows and durable event rows. At minimum, verify:

- Surviving and deleted row counts, including every current message row.
- Retained message and part identities.
- Exact placeholder text.
- Absence of sensitive properties and obsolete markers.
- Preservation of required operational metadata.
- Idempotency: a second preview has no cleanup actions for already-cleaned messages.
- Unrelated and recent messages, session rows, and session events remain unchanged.

Add focused tests for malformed data and unsupported storage whenever validation changes.

## Code Conventions

- Use file-scoped namespaces, nullable reference types, and implicit usings.
- Prefer `sealed` classes and records unless inheritance is required.
- Use `var` when the type is obvious from the initializer.
- Use `StringComparer.Ordinal` for database identity dictionaries and sets.
- Keep SQL parameterized. Table names may only come from closed internal sets, never user input.
- Keep the implementation synchronous; the tool has no async architecture.
- Return `null` from sanitization methods when serialized JSON would not change. This keeps repeated cleanup previews idempotent.
- Throw `FileNotFoundException` when the requested database is missing, `InvalidDataException` for malformed or internally inconsistent database content, `NotSupportedException` for recognized unsupported storage versions, and `InvalidOperationException` for concurrency or apply failures.
- Avoid general-purpose JSON transformation frameworks. The small explicit sanitizer is easier to audit for privacy behavior.
- Do not edit or commit generated `bin/` or `obj/` content.

## Change Checklist

Before completing a change:

1. Confirm whether it changes eligibility, anchor selection, retained JSON, event behavior, preview counts, or transaction ordering.
2. Update current-row and event-row behavior together when they represent the same content.
3. Update `README.md` for any user-visible or safety-relevant change.
4. Add or update temporary-database tests.
5. Run compile and test commands.
6. Inspect `git diff` for accidental generated files, secrets, real database paths, or unrelated changes.

Privacy claims must be backed by deletion logic and tests, not comments or UI behavior alone.

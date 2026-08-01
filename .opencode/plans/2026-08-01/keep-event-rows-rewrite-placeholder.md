# Keep event rows: rewrite instead of delete

## Task

In `D:\code\OpenCodeDatabaseCleaner`, change the V1 cleanup so that **no `event` rows are ever
deleted**. Currently `OpenCodeDb.AddEventActions` deletes every `message.part.updated.1` durable
event whose embedded `part.id` is not the retained anchor text part of an eligible message
(`OpenCodeDb.cs:408`, `plan.AddDelete(sessionId, "event", id, null)`). Deleting journal rows creates
`event.seq` gaps and breaks the sync/replay path (`packages/core/src/event.ts:295-301` dies on
"Sequence mismatch ... expected latest + 1"). Replace that delete with an in-place **UPDATE** of the
row's `data` to a schema-valid `message.part.updated.1` payload carrying a `<cleaned>` text part, so
the row, `aggregate_id`, `seq`, and `type` are preserved while the conversation content is removed.

This changes only the `event` behavior. The `part`-table deletes (`OpenCodeDb.cs:345`) are current
projections and stay as deletes (no journal/seq implication).

## Current behavior (exact)

`AddEventActions` (`OpenCodeDb.cs:357-411`) iterates all `message.updated.1` / `message.part.updated.1`
rows:

- `message.updated.1` -> sanitize `data` in place (`plan.AddUpdate`, never deleted).
- `message.part.updated.1` where `partId == anchor.PartId` (retained first-text part) -> replace
  `data` with the `<cleaned>` placeholder (`plan.AddUpdate`).
- `message.part.updated.1` where `partId != anchor.PartId` (any other part: tool, reasoning,
  step-start/finish, file/patch/snapshot, subtask, non-first text) -> **`plan.AddDelete`** (line 408).

`message.part.updated.1` payload schema: `{ sessionID: SessionID, part: Part, time: Finite }`
(`packages/schema/src/v1/session.ts:612-620`). Events referencing a message not present in the
current `message` table (event-only history) are skipped entirely and are out of scope.

## Design

For a non-anchor part event, replace `data` with:

```json
{
  "sessionID": "<original sessionID>",
  "part": {
    "id": "<original part id>",
    "sessionID": "<original sessionID>",
    "messageID": "<original messageID>",
    "type": "text",
    "text": "<cleaned>"
  },
  "time": <original time, kept when present>
}
```

Rationale:

- Keeps the row, `aggregate_id`, `seq`, and `type` intact -> journal stays contiguous and replay/sync
  (whenever enabled) does not die on sequence checks.
- Keeps the payload decodable against the `message.part.updated.1` schema (a `text` part only requires
  `id`/`sessionID`/`messageID`/`type`/`text`; `time` is optional on text parts).
- Removes all conversation content and content-bearing metadata (`state.output`, `state.input`,
  `reasoning.text`, `step.snapshot`, etc.).
- Idempotency: if the part already equals this placeholder shape, return `null` (no action), so a
  second preview yields 0 actions (existing invariant).

### Documented tradeoff

If a sync replica later replays these rewritten events, the projector will upsert a `<cleaned>` text
part per rewritten event, so a replayed message may regain multiple `<cleaned>` text parts (the
"exactly one text part per message" invariant holds only for the locally cleaned projection). Content
is gone either way, and sync is experimental and off by default. This is the literal behavior the
request asks for ("only update with `<cleaned>` text").

### Considered alternative (not chosen)

Per-part-type blanking (preserve `part.type`, empty only the content fields, e.g. `tool` keeps
`status`/`callID`/`tool` with `input:{}`, `output:"", title:"", metadata:{}`). Keeps type fidelity on
replay but is substantially more code and tests for no content benefit. Noted for future use if
replay fidelity ever matters.

## Changes

### 1. `Database/ContentSanitizer.cs`

Add a method, e.g.:

```csharp
public static string? CreatePlaceholderPartEventForRemovedPart(string data)
```

- Parse `data` as a JSON object.
- Read `part.id`, `part.sessionID`, `part.messageID` (must be non-null strings; the storage
  validation in `EnsureSupportedStorage` already guarantees they exist).
- Build the placeholder part `{ id, sessionID, messageID, type: "text", text: "<cleaned>" }`.
- If `JsonNode.DeepEquals(existingPart, placeholderPart)` -> return `null` (idempotent, no action).
- Otherwise set `root["part"] = placeholderPart`, keep `root["sessionID"]` and `root["time"]`
  unchanged, and return `root.ToJsonString()`.

Keep the existing `CreatePlaceholderPartEvent` for the retained-anchor branch unchanged.

### 2. `Database/OpenCodeDb.cs` — `AddEventActions` (lines 398-409)

Replace the `else` delete branch:

```csharp
else
{
    plan.AddDelete(sessionId, "event", id, null);
}
```

with:

```csharp
else
{
    var placeholder = ContentSanitizer.CreatePlaceholderPartEventForRemovedPart(data);
    if (placeholder is not null)
    {
        plan.AddUpdate(sessionId, "event", id, null, placeholder);
    }
}
```

No changes to `CleanupPlan`, `ContentActionTarget`, `ApplyCleanup`, `ActionOrder`, or
`EnsureSupportedStorage`:

- Event updates already have no `time_updated` version guard (`OpenCodeDb.cs:64`) and the preview
  `Matches` check already compares the rewritten `data` for event updates via `UnversionedData`
  (`CleanupPlan.cs:112-114`) — now it also covers these formerly-deleted rows, which strengthens the
  optimistic-concurrency check.
- `EventRowCount` and `ActionCount` semantics are unchanged (a delete became an update; counts move by
  the same amount).
- The post-cleanup `EnsureSupportedStorage` shape check still passes: rewritten events have valid
  `part.id`/`part.messageID`/`part.sessionID` strings, `aggregate_id == part.sessionID`, and the
  message still exists in `message` with the matching `session_id`.

### 3. Tests — `OpenCodeDatabaseCleaner.Tests/OpenCodeDbTests.cs`

- `CleanupCleansOldMessagesInMixedAgeSessionsWithoutChangingSessionMetadata`:
  - `SELECT COUNT(*) FROM event WHERE aggregate_id = 'old' AND type LIKE 'message.%'` must become
    **4** (was 3) — `old-tool-event` is now retained.
  - Add assertions that `old-tool-event` `data.part.type == "text"`, `data.part.text == "<cleaned>"`,
    and the original `state.output` is gone.
  - `ActionCount` (11) is unchanged.
- `CleanupCleansAllMessagesWhenCutoffIsUnbounded`:
  - `SELECT COUNT(*) FROM event WHERE type = 'message.part.updated.1' AND part.text = '<cleaned>'`
    must become **7** (was 6).
  - `SELECT COUNT(*) FROM event WHERE type = 'message.updated.1'` stays 7.
- `CleanupCleansAnOldProjectionMessageWhenUnrelatedDurableEventsRemain`: unchanged — the
  `disagree-text-event` references a message id not in the anchors (`durable-user`), so it is skipped,
  not touched.
- `CleanupLeavesEventOnlyHistoryUntouched` / `CleanupSkipsSessionsWithoutCurrentMessages`: unchanged
  (those events are skipped, not deleted).
- `CleanupRepairsPreviouslyCleanedSessionWithPlaceholderOnNewestMessage`,
  `CleanupConvertsSyntheticOnlyAnchorToOrdinaryPlaceholder`,
  `CleanupCountsAndLeavesV2DataUntouched`, `CleanupRejects*`: action counts are unchanged (delete->update
  preserves count); no edits expected unless assertions reference `old-tool-event`.
- Add one focused test: after a full `--days 0`-style cleanup of a mixed session, assert
  **no `event` row for the aggregate is deleted** (count before == count after) and every
  `message.part.updated.1` event has `part.text == "<cleaned>"`; then assert a second preview yields 0
  actions (idempotency of the rewrite).

### 4. Docs

- `README.md`:
  - Line ~5: "Matching durable V1 message and part events are sanitized or deleted" ->
    "sanitized or rewritten to `<cleaned>` text-placeholder payloads".
  - Safety section line ~51: add that non-retained part events are rewritten in place (row, seq, and
    type preserved) rather than deleted, so `event` sequence stays contiguous; keep the existing
    replica/replay warning and add that replayed rewritten events may reproduce multiple `<cleaned>`
    text parts.
- `AGENTS.md`:
  - Line ~15: "Other V1 part events for that message are deleted" -> "rewritten as `<cleaned>` text
    placeholder events".
  - Line ~68: "other parts and non-anchor events are deleted" -> "non-anchor events are rewritten as
    `<cleaned>` text placeholders".
  - Line ~110-116 (Event Semantics): update the rationale — non-retained part events are retained as
    placeholders to preserve journal continuity and sequence numbers while removing content.
  - Event Semantics: note the invariant that cleanup never deletes `event` rows; it only updates `data`.

## Verification

- `dotnet msbuild /t:Compile | Select-String -Not ': warning'`
- `dotnet test "OpenCodeDatabaseCleaner.Tests/OpenCodeDatabaseCleaner.Tests.csproj"`
- Manual dry-run against a disposable copy of a real DB:
  `dotnet run --project "OpenCodeDatabaseCleaner.csproj" -- --days 0 --db <copy> --dry-run`
  then confirm (via sqlite3 on the copy) that:
  - total `event` row count per aggregate is unchanged after apply,
  - no `event.data` contains the original content markers,
  - `event_sequence.seq` still equals `MAX(event.seq)` per aggregate,
  - a second preview reports 0 actions.

## Out of scope

- V2 storage (`session_message`, `session_input`, `session.next.*`) — unchanged.
- `part`-table deletes — unchanged (projections, no seq impact).
- Per-part-type content blanking (alternative above) — only if replay fidelity becomes a requirement.

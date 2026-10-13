# GridFight entry after quest unlock

## Cause

Completing quests exposes the GridFight weekly score panel. Its timer uses the
current CycleScore end timestamp and exits the owning entrance page on expiry.
`CycleScoreRewardGetDataCsReq` had no handler, so the generic fallback returned
an empty successful response. The client failed in `CycleScore.Sync` and the
weekly panel did not receive a valid period.

## Changes

- Return a non-null CycleScore snapshot covering the current local calendar week
  (Monday 00:00 to the following Monday), using the existing server time provider.
- Reject unsupported CycleScore reward claim/destruction requests explicitly.
- Send exactly one successful `GridFightFinishTutorialScRsp` for the existing
  fully unlocked tutorial model.
- Preserve stored quest progress and finish timestamps in login/query responses.
- Filter missing or inconsistent scene sub-mission references without changing
  persisted mission completion or claimed rewards.
- Return failure for nonexistent interaction targets.
- Return the actual AND/OR result for scene saved-value conditions.

Unknown 4.6 protocol fields retain their original names and defaults. No database
migration, quest rollback, hardcoded prop-state bypass or client patch is included.
Temporary tracing and Lua diagnostics are excluded.

## Validation

- GridFight assert-based regression: 240 checks passed, including real KCP opcode
  and unique responses, all quest states, repeated queries, week boundaries,
  fake time, invalid inputs and preserved completion/claimed records.
- Read-only pre-unlock and current SQLite backup replay uses isolated memory and
  temporary SQLite, skips account tables and repeats the original GM sequence.
- Gacha r6 regression and Release build passed.
- Native client: operator confirmed entrance, game start and battle completion.
- Native re-entry after another login and another unlock sequence: NOT RUN.

```powershell
dotnet run --project tests/GridFightRegression/GridFightRegression.csproj -c Release -- <Resource.bin> [SQLite backup under Ops/Backups]
dotnet run --project tests/GachaRegression/GachaRegression.csproj -c Release -- <Resource.bin>
dotnet publish Program/Program.csproj -c Release -o <separate output directory>
```

## Limits and dependency

The CycleScore response is an empty read-only calendar-week snapshot. Persisted
weekly scoring and reward operations remain unimplemented; writes return failure.
The calendar-week window is a compatibility fallback, not an implementation of
all official reward reset rules.

This branch builds on the gacha r6 fixes in upstream PR #5, which must be preserved.
The native acceptance used the same functional patch with temporary metadata
tracing; cleanup only removes that tracing. Local runtime activity dates, backups,
client code extracts and operational scripts are not included in this PR.

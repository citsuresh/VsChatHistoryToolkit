# Design Decisions

## 2026-11-10 — Keep chat-store reads read-only and WAL-aware

- **Decision:** Open query paths with SQLite read-only mode and `PRAGMA query_only=ON`; do not use immutable mode. Keep writes limited to the explicit session rename and undo commands.
- **Rationale:** The toolkit can inspect the live Visual Studio database, including WAL-backed rows, without copying or mutating it during reads; the narrow write surface limits unintended changes.
- **Alternatives considered:** Opening the database immutable or copying it before reading were not chosen because they can miss live WAL state or create unnecessary copies of local chat data.

## 2026-11-10 - Dedicated numeric latest-turn snapshots (toolkit-turn-gaps)

- **Decision:** Add `usage-turns <id> [--limit N]` with newest distinct usage indices returned ascending and whole-session totals from the same SQL snapshot. Keep `usage --by-turn` and its first-100 shape unchanged.
- **Rationale:** Polling clients can replace numeric rows by stable turn index after 100+ turns without fetching titles/messages or assuming contiguous indices. Per-turn endpoints use valid usage-event timestamps across main/subagents; invalid or missing timestamps stay explicitly incomplete rather than being inferred from a clock or message.
- **Alternatives considered:** Extending the existing usage result would also expose model/session identifiers and couple callers to its other aggregates; retrieving `turns`/`show` would unnecessarily read chat content. Neither is needed for this bounded numeric interface.

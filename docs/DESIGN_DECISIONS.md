# Design Decisions

## 2026-11-10 — Keep chat-store reads read-only and WAL-aware

- **Decision:** Open query paths with SQLite read-only mode and `PRAGMA query_only=ON`; do not use immutable mode. Keep writes limited to the explicit session rename and undo commands.
- **Rationale:** The toolkit can inspect the live Visual Studio database, including WAL-backed rows, without copying or mutating it during reads; the narrow write surface limits unintended changes.
- **Alternatives considered:** Opening the database immutable or copying it before reading were not chosen because they can miss live WAL state or create unnecessary copies of local chat data.

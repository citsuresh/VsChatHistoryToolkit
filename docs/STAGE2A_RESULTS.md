# Stage 2a results: read-only checks against the real store

Run on 2026-10-30 against the local `%LOCALAPPDATA%\Microsoft\VisualStudio\CopilotCli` store, with Visual Studio running, from a normal process. Counts and timings only: no titles, repository names, paths or chat text appear here or were written to disk. Nothing was written to the store.

Timings are wall-clock for a whole `chathistory` process and include about 0.3 to 0.65 s of .NET start-up (`--help` took 0.37 s in the same run).

## Store size

| Item | Value |
| --- | --- |
| Sessions in `session-store.db` | 165 |
| Turns | 3072 |
| Session folders in `session-state` | 176 (175 with `workspace.yaml`, 152 with `events.jsonl`) |
| Folders without a database row | 11 |
| Database rows without a folder | 0 |
| Sessions with no turns (no end time) | 16 |
| Sessions with `end` earlier than `start` | 0 |
| Turns per session | min 0, median 7, max 264 |
| `events.jsonl` size | median 1.85 MB, 90th percentile 19 MB, max 64 MB |

Coverage stayed "session-store.db only": the 11 folders without a row are not visible to any command.

## Commands

| Command | Result | Time |
| --- | --- | --- |
| `stats` | 165 sessions, 3072 turns; 13 repository rows, 17 folder rows, 4 month rows; not truncated | 0.27 s |
| `sessions --limit 20` / `100` / `1000` | 20 / 100 / 100 rows; the limit is clamped to 100 and `truncated` is true | 0.39 to 0.78 s |
| `sessions` month by month | the union of month windows is 165 distinct sessions, equal to `stats` | |
| `sessions --from/--to`, no-match `--repo` | 5 rows / 0 rows, exit 0 | 0.29 s |
| `show` for all 165 sessions | 0 failures; median 0.46 s, max 2.8 s (the slowest are the large `events.jsonl` files, which are read to the end when there is no solution) | |
| `search` for five common words, limit 100 | 100 hits each, `truncated` true | 0.31 to 0.41 s |
| `search` with punctuation-only input | exit 1 (no searchable word); other special-character queries exit 0 | |
| `turns` on the 264-turn session, default | 10 turns, 30 882 output chars, `truncated` true | 0.41 s |
| `turns --range 0-1000000 --max-chars 100000` | still 10 turns, 42 360 output chars (text budget 40 000 plus JSON), `truncated` true | |
| `turns --index 0 --max-chars 200` | 1 turn, longest field 201 characters (200 plus the ellipsis) | |
| `checkpoints` on the 15-checkpoint session | 11 061 chars default; 63 108 chars with `--full` (budget 60 000 plus JSON), `truncated` true | 0.45 s |
| unknown id / `..` / bad `--range` | exit 2 `not_found` / exit 1 `usage` / exit 1 `usage` | |

`show` reports `truncated: true` for 107 of 165 sessions. That is expected: the stored `summary` is often a long first message or IDE context text and is cut to 200 characters.

Title sources: 152 sessions have a title from `workspace.yaml` `name:`, 13 have none (no `name:` line and an empty summary). 150 sessions have `user_named: false` and 15 have `true`.

## Store files

After all commands, `session-store.db`, `session-store.db-wal` and `session-store.db-shm` had the same size and modification time as before, so the reads neither wrote to nor checkpointed the store, and Visual Studio was not disturbed.

## Assumptions from Stage 1

| # | Assumption | Result |
| --- | --- | --- |
| 1 | `PRAGMA user_version` is 8 | **Wrong.** It is 0. The schema number is the single row of a `schema_version` table (8). The first run failed with a schema mismatch; the guard now reads the table and falls back to `user_version` only when the table does not exist. |
| 2 | `search_index.source_id` maps a hit to its session | **Moot.** The table has its own `session_id` column, which is now used. All 500 sampled hits resolved. `source_id` is `<session id>:<suffix>` for every source type. |
| 3 | `snippet()` works on `search_index` | **Confirmed.** 490 of 500 snippets carry the `[` `]` match markers; the longest is 201 characters. |
| 4 | The `Solution:` line is in `user.message` events | **Confirmed.** An independent scan finds it in `user.message` events of 81 sessions and the toolkit found the same 81, with none unparseable. Another 43 sessions only mention "Solution:" in system, tool or assistant events, which are correctly ignored. |
| 5 | Visual Studio reads a double-quoted `name:` | **Supported.** Visual Studio's own files hold 90 double-quoted, 10 single-quoted and 52 plain `name:` values (23 files have no `name:` line). No file has a byte-order mark or CR bytes (175 checked), as the toolkit assumes. |
| 6 | `File.Replace` on `workspace.yaml` works while Visual Studio runs | **Not tested.** Needs a real rename (Stage 2b). |

## Not covered

Stage 2b is not done: a rename and undo on a test session you choose, with a Visual Studio restart between steps, the active or loaded chat case, and whether a renamed chat is rewritten by Visual Studio.

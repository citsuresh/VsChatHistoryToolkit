# chathistory CLI contract

Executable: `chathistory` (project `VsChatHistoryToolkit.Cli`). Output is JSON on stdout unless `--text` is given. In `--text` mode every printed line is cleaned: line breaks and tabs inside chat text become spaces and other control characters (including terminal escape sequences) are shown as visible symbols, so stored chat text cannot drive the terminal or fake output lines. JSON output is not altered; the serializer escapes it.
Property names are `snake_case`; null properties are omitted.

## Envelope (every successful result)

```json
{ "ok": true, "command": "search", "coverage": "session-store.db only", "truncated": false, "...": "..." }
```

- `coverage` is always `"session-store.db only"`.
- `truncated` is `true` when any row cap, character cap or the total character budget cut something. Per-item `truncated` flags exist on turns.

## Errors

JSON mode: printed to stdout. `--text` mode: printed to stderr as `error (<code>): <message>`.

```json
{ "ok": false, "coverage": "session-store.db only", "error": { "code": "not_found", "message": "..." } }
```

| Exit | `error.code` | Meaning |
| --- | --- | --- |
| 0 | | success |
| 1 | `usage` | bad arguments, bad id shape, empty or over-long title, no searchable words |
| 2 | `not_found` | no such session row, no session folder or `workspace.yaml`, nothing to undo |
| 3 | `store_unavailable`, `schema_mismatch` | database missing or cannot be opened; the schema version is not 8 (the `schema_version` table must hold exactly one row, and its version is read; `PRAGMA user_version` is used only when that table does not exist; the message names the found version and where it came from) |
| 4 | `refused` | session looks active or recently used (no `--force`); undo target changed since the rename; `workspace.yaml` not valid UTF-8 |
| 5 | `write_failed` | database locked, log not writable, YAML replace failed, or commit failed. The message states what state was left |
| 6 | `internal` | unexpected error |

## Global options

`--text`, `--help`, `--root <folder>` (store root; default `%LOCALAPPDATA%\Microsoft\VisualStudio\CopilotCli`, or env `VSCHATHISTORY_ROOT`), `--log-dir <folder>` (undo log; default `%LOCALAPPDATA%\VsChatHistoryToolkit`, or env `VSCHATHISTORY_LOGDIR`). Use `--` to end options (for titles that start with `-`).

## Caps

| Cap | Value |
| --- | --- |
| `--limit` default / maximum | 20 / 100 |
| Search terms / characters per term | 10 / 64 |
| Search snippet | 200 characters |
| Title shown in lists | 160 characters |
| Turns per call | 10 (default range `0-9`) |
| `--max-chars` per message field (default / maximum) | 1500 / 4000 |
| Turn text per call (all fields together) | 40 000 characters |
| Checkpoints per call | 50 |
| Checkpoint overview / each `--full` field | 600 / 2000 characters |
| Checkpoint text per call | 60 000 characters |
| `stats` rows per group (also `usage` by model / by month) | 25 |
| `usage --by-turn` turns | 100 |
| `usage-turns` newest usage-bearing turns (`--limit` default / maximum) | 20 / 100 |
| `files` file path (also the echoed `--path` text) | 400 characters |
| `files` tool name | 160 characters |
| `usage-top` chats (`--limit` default / maximum) | 20 / 100 |
| `usage-breakdown` rows (`--limit` default / maximum) | 100 / 500 |
| `doctor` id lists (rows without a folder, folders without a row) / largest `events.jsonl` files | 25 / 10 |
| `doctor` id or folder name | 160 characters |
| `events` bytes / lines / single line | 128 MiB / 500 000 / 1 000 000 bytes |
| `events` event types, tool names, permission kinds, skills, compactions, shutdowns, models per shutdown | 25 / 25 / 25 / 25 / 25 / 10 / 25 |
| `activity` days (`--limit` default / maximum) | 20 / 100 |
| `rollup` chats (`--limit` default / maximum) / total scan time / total bytes scanned | 20 / 100 / 60 s / 512 MiB |
| `rollup` event types, tools, skills, permission kinds, compaction triggers / top chats by compactions | 25 each / 5 |

Every character cap cuts the text to that many characters and appends one ellipsis character when it cuts, so a capped string can be one character longer than the cap. Caller values above a maximum are clamped and `truncated` is set. Whole chats are never printed.

## Commands

### `search <terms...> [--limit N]`
Words and numbers only; punctuation separates terms and every term is quoted for FTS5 (AND semantics). FTS5 matches word tokens, not substrings.

`hits[]`: `session_id` (read from the `search_index.session_id` column; null only if that column is empty), `source_type`, `source_id`, `snippet`. Also `query`, `terms`, `limit`, `count`.

### `sessions [--repo] [--cwd] [--branch] [--from] [--to] [--limit N]`
`--repo/--cwd/--branch`: "contains", case-insensitive for ASCII letters (SQLite `LIKE`; other letters match only in the same case). `--from/--to`: `yyyy-MM-dd` or ISO-8601 (UTC); a date-only `--to` includes that whole day. Fractional seconds in a bound are kept (compared to the millisecond). They filter on the session end (last turn, falling back to `created_at` when there are no turns). Newest first.

`sessions[]`: `id`, `title`, `title_source` (`name` = workspace.yaml `name:`, `summary`, or `none`), `repository`, `cwd`, `branch`, `start` (`created_at`), `end` (`MAX(turns.timestamp)`, UTC ISO string, omitted without turns), `turn_count`.

### `show <id>`
`session` (same fields as above), `host_type`, `summary`, `user_named`, `solution` (a name; one longer than 200 characters is cut to 200 characters plus a trailing ellipsis character and sets `truncated`; or `"unknown"`), `solution_source` (`events.jsonl`; `none` when the whole file was scanned and no `Solution:` line exists; `incomplete` when the scan stopped at a cap, in which case `truncated` is also `true` and `"unknown"` is not proof of absence), `workspace_yaml_present`, `checkpoints[]` (`number`, `title`). The solution is read from the `Solution:` line in the IDE context of `user.message` events and is never inferred from folders or chat text.

### `turns <id> [--index i | --range a-b] [--max-chars N]`
`turns[]`: `index`, `timestamp`, `user_message`, `assistant_response`, `user_message_chars`, `assistant_response_chars` (original lengths), `truncated`. Also `total_turns`, `count`.

### `checkpoints <id> [--full]`
Default: `number`, `title`, `overview`, `created_at`. With `--full` also `history`, `work_done`, `technical_details`, `important_files`, `next_steps`.

### `usage <id> [--by-turn]`
Token and cost figures of one session, summed from the per-request rows of the `assistant_usage_events` table (read-only, like every read). `recorded` is `true` when the session has rows; `total`, `main` (requests without an `agent_id`) and `subagents` (requests with one) are usage totals; `first_at` / `last_at` are the earliest and latest `created_at`; `peak_input_tokens` and `last_input_tokens` are the prompt size of main-chat requests only (no `agent_id`): the largest `input_tokens`, and the `input_tokens` of the latest main request (by `created_at`, then `rowid`); both are omitted (null) when the session has no main requests; `by_model[]` (`{model, totals}`, busiest first, top 25); with `--by-turn`, `by_turn[]` (`{turn_index, totals, peak_input_tokens}`, the first 100 distinct indices ascending with gaps preserved, `truncated` set when more; `peak_input_tokens` is the largest main-request prompt of that turn and is omitted when the turn has only subagent requests). This existing shape and first-100 behavior are unchanged; use `usage-turns` for the latest window with per-turn timestamps.

A usage totals object has `requests`, `input_tokens`, `output_tokens`, `cache_read_tokens`, `cache_write_tokens`, `reasoning_tokens`, `nano_aiu`, `credits`, `duration_ms`. Missing numbers count as 0. If adding the main and subagent figures would overflow a 64-bit integer, that figure stops at the maximum (for `credits`, the largest double) and `truncated` is set. If SQLite itself overflows while summing the stored rows (an integer overflow, or a `credits` sum that is infinite or not a number), `usage` fails with `store_unavailable` (exit 3) and `stats` leaves its `usage` section out and sets `truncated`. Values are exactly as stored:
- `input_tokens` is the whole prompt of each request, so summing it over a chat counts the re-sent context again on every request; it is a measure of tokens processed, not of the chat's length. In the real store `cache_read_tokens` plus `cache_write_tokens` never exceeded `input_tokens` in any row, which suggests (not proven) that input includes the cached tokens.
- `nano_aiu` is the stored cost unit (`total_nano_aiu`). Its relation to the credits shown in the chat window has not been verified, so it is reported as stored and never converted.
- `credits` is the sum of `request_multiplier` (a missing value counts as 0), rounded to 2 decimals; for example 11 requests of multiplier 1.0 give 11.0 and 4 requests of multiplier 15.0 give 60.0. It matched the credits shown in the chat footers in 7 of 7 turns checked, including one turn with subagent requests (two Opus requests at 15.0 plus two subagent requests at 1.0 gave 32.0), so subagent requests count: `total`, `by_model`, `by_turn`, `usage-top`, `activity` and `stats` include them, and `total` is the figure to quote (`main` and `subagents` show the split). `request_multiplier` is optional: when the table lacks it, `credits` is omitted (null) everywhere, never reported as 0, and `recorded` stays `true`. Known edge: `usage-top --by credits` ranks on SQLite's `ROUND(sum, 2)` while the shown `credits` are rounded in .NET, and the two can differ on an exact half-cent sum (for example 1.005 or 0.285), which needs multipliers with three or more decimals (the stored multipliers are 0.33, 1, 7.5 and 15), so two chats could then be ordered by that difference instead of by session id.
- A store without the table (or without a column this tool reads) and a session without rows are normal answers: `recorded` is `false` and `note` says why. An unknown session id is `not_found` (exit 2).

### `usage-turns <id> [--limit N]`
Numeric-only snapshot of a selected session's newest usage-bearing turns, read from `assistant_usage_events`. The caller supplies the exact current-session id; this command does not discover it or invoke `turns`/`show`. It checks only that a session row exists, reads usage-event numeric fields and timestamps, and never reads session titles, message tables, workspace files or event logs. No session id, model name, agent id, repository, message text, cwd or path fields are returned.

Result: the standard envelope, `recorded`, `note` (when applicable), `limit`, `count`, `total`, `by_turn[]`. `total` is the usage totals object described under `usage`, summed over **all requests in the session**, not just the latest window. Main and subagent requests both count in each turn and the total. The total and returned turns come from one SQL statement/read snapshot; clients do not need N per-turn calls.

- `--limit`: default 20, maximum 100. Select the greatest N distinct stored `turn_index` values and return those **ascending**. Indices remain stable and gaps are not filled or renumbered. Newest means greatest turn index, not greatest event timestamp or latest inserted row. Turns without usage events are absent, even if they have messages. More usage-bearing turns, or a clamped limit, sets `truncated` and adds a note; it never limits `total`. For unavailable/empty usage, a clamped limit still sets `truncated` and appends the fixed sentence `Requested limit was clamped to 100.` to the explanatory note.
- Each `by_turn[]` item has `turn_index`, `totals`, nullable `peak_input_tokens` (largest main-request input for that turn, null without main requests), nullable `first_at`/`last_at`, and `timestamps_complete`.
- `first_at` / `last_at` are the earliest/latest valid `created_at` **usage-event instants in that turn**, across main and subagent requests, converted to UTC and formatted `yyyy-MM-ddTHH:mm:ss.fffZ` (sub-millisecond digits discarded). Accepted source timestamps have an ISO date and time through seconds, optional 1-7 fractional digits, and `Z` or an offset; an absent offset is interpreted as UTC. These are stored usage-record times, not message start, reply completion, polling time or a duration estimate. A replacement event's current stored timestamp is authoritative; no historical timestamp is inferred.
- Missing/empty/invalid timestamp values do not remove usage requests. Endpoints use only valid timestamps and are null when no valid timestamp exists; `timestamps_complete` is true only when every event of the turn has a valid timestamp. Date-only, time-only and relative values such as `now` are not accepted. Null properties are omitted in JSON, following the global serializer rule; clients must treat missing endpoints as unknown, never synthesize them from polling or neighboring turns.
- `credits` retains the existing usage rules: sum `request_multiplier` over all main/subagent requests and round once per aggregate to two decimals; NULL values count as zero when the column exists. If the column is absent, credits are unknown (null/omitted) in both `total` and every turn, never fabricated as zero. Separately rounded turn sums may differ from the globally rounded session total.
- Polling returns **replacement snapshots**, not deltas: replace entries by `turn_index`, including the still-streaming newest turn, rather than appending duplicates or adding successive snapshots. Updates/replacements of stored events change that turn's values; additional requests for the same turn are summed. A rolling-window client should also drop entries that leave the returned window if displaying only the latest N.

A missing/unusable usage table (required columns are the usage numeric columns, `session_id`, `turn_index`, `agent_id` and `created_at`; `model` and a request row id are not required), or a session without usage events, returns `recorded: false`, an explanatory `note`, no `total` and empty `by_turn[]`. Unknown session is `not_found` (exit 2), without echoing its id. Malformed ids, missing/extra positional arguments, non-integer/non-positive limits and unsupported options are `usage` errors (exit 1). Only `--limit` and global `--root`, `--log-dir`, `--text`, `--help` are accepted; boolean flags take no value. Store/schema errors and aggregate integer or non-finite credit overflow fail with exit 3, including overflow outside the latest window. No existing command's options or output are changed.

### `usage-top [--from DATE] [--to DATE] [--by requests|tokens|aiu|credits] [--limit N]`
Chats ranked by usage, from the `assistant_usage_events` table (read-only). `--from/--to` filter each request on its `created_at` with the same parsing as `sessions` (`yyyy-MM-dd` or ISO-8601, UTC; a date-only `--to` includes that whole day), so a chat's figures cover only its requests inside the range. Request timestamps are normalized to UTC before applying the half-open bounds, including when a stored timestamp has an offset rather than a trailing `Z`. `--by` (case-insensitive): `aiu` (default, sum of `total_nano_aiu`), `tokens` (sum of `input_tokens` plus sum of `output_tokens`), `requests` (request count) or `credits` (sum of `request_multiplier`); main and subagent requests both count. Highest first, ties broken by session id; at most `--limit` chats (default 20, maximum 100; `truncated` set when more or when clamped). With `--by credits` on a store without `request_multiplier`, the answer stays `recorded: true`, chats are listed by session id and `note` says credits are not available. Any other `--by` value, a bad date or a positional argument is a `usage` error (exit 1).

Result:

### `usage-breakdown [--from DATE] [--to DATE] [--repo TEXT] [--model TEXT] [--group-by day,repo,model] [--limit N]`
Joint-filter usage aggregates from `assistant_usage_events` (read-only); no per-chat calls are needed. All filters apply to each request before grouping, and main and subagent requests both count:
- `--from/--to`: the same parsing as `usage-top` (`yyyy-MM-dd` or ISO-8601, normalized to UTC, millisecond precision). The range is half-open (`created_at >= from` and `created_at < to`); a date-only `--to` becomes the next midnight and includes that whole day. Request timestamps are normalized to UTC for filtering and day grouping; session start/end and turn timestamps do not affect these figures.
- `--repo`: contains on the owning session's stored repository, with the same trimming, literal `%`, `_` and `\` handling, and ASCII case-insensitive SQLite `LIKE` rules as `sessions --repo`. A request without an owning session row has no repository for filtering, but still counts without this filter.
- `--model`: contains on the stored model, ordinal case-insensitive; wildcard characters are literal. A blank repository/model filter means no restriction. Neither filter value is echoed.
- `--group-by`: any non-empty comma-separated combination of `day`, `repo`, `model` (default all three); names are case-insensitive and trimmed, and duplicate, empty or unknown names are usage errors. `group_by[]` is returned in canonical `day,repo,model` order. `day` is the UTC date of the request, `repo` is the session repository (`unknown` for NULL/empty or a missing session), and `model` is the stored model (`unknown` for NULL/empty).

`--model` and `--group-by` are supported only by `usage-breakdown`; using either with another verb is a `usage` error (exit 1), not an ignored filter.

Result:

Rows are ordered by `day` descending when grouped by day, then `nano_aiu` descending, then repository and model keys ascending (ordinal SQLite ordering; omitted keys do not participate). At most `--limit` rows are returned (default 100, maximum 500). More rows, or a clamped limit, sets `truncated` and adds a `note` saying that `total` still covers all filtered requests. An untruncated result's additive metrics reconcile with `total`; distinct `chats` is **not additive** across days/models because the same chat can appear in several groups. As with other usage commands, separately rounded credits can differ from a globally rounded sum when stored multipliers have more than two decimals. Without extra repo/model filters, request/token/cost totals agree with the corresponding untruncated `usage-top`/`activity` figures for the same range (subject to that rounding distinction); `activity.chats` instead counts sessions with turns.

Privacy: no session ids, titles, message text, cwd or file paths are returned; repository appears only as a selected row group key, never in the envelope or `total`. A store without the usage table (or a needed column) is a normal answer: `recorded: false`, an explanatory `note`, no `total` and empty `rows[]`. A usable table with no matching requests returns `recorded: true`, zero totals and empty `rows[]`. An aggregate integer overflow or non-finite credit total fails with `store_unavailable` (exit 3). Bad `--group-by`, a bad date, a non-positive/non-integer limit or any positional argument is a `usage` error (exit 1).

Example: `chathistory usage-breakdown --from 2026-01-01 --to 2026-01-31 --repo toolkit --model opus --group-by day,model --limit 100`.

### `files <id> [--limit N]` and `files --path <text> [--limit N]`
Files edited or created in chats, read from the `session_files` table (read-only). Exactly one of `<id>` or `--path` is required; neither, both, or an empty `--path` is a `usage` error (exit 1). Both forms print `"command": "files"`.

- `files <id>`: `session_id`, `recorded`, `note`, `limit`, `count`, `files[]` (`file_path`, `tool_name`, `turn_index`, `first_seen_at`), ordered by `turn_index` then row `id`, at most `--limit` rows (`truncated` set when more). An unknown session id is `not_found` (exit 2).
- `files --path <text>`:
- A store without the `session_files` table (or without a column this tool reads: `id`, `session_id`, `file_path`, `tool_name`, `turn_index`, `first_seen_at`), and for `files <id>` a chat without rows, are normal answers: `recorded` is `false` and `note` says why.

### `stats`
`total_sessions`, `total_turns`, `by_repository[]`, `by_cwd[]`, `by_month[]` (each `{key, sessions}`; month is the session start `yyyy-MM`; empty values are `unknown`). When the usage table exists, also `usage`: `sessions_with_usage`, `total` (usage totals over all requests), `by_model[]` (top 25) and `by_month[]` (`{month, totals}` from each request's `created_at`, newest first, top 25). It is omitted when the table is absent or lacks a column this tool reads.

### `doctor`
A read-only health report. It opens the database read-only, lists the folders directly under `session-state` and reads the size of each `events.jsonl`; it never opens chat files for reading, and never writes, moves or deletes anything. Result: `session_rows`, `session_state_folder_present`, `session_state_listed`, `session_folders` (folders directly under `session-state`), `rows_without_folder` and `folders_without_row` (full counts; ids are compared case-insensitively) with `rows_without_folder_ids[]` and `folders_without_row_names[]` (ordinal order, at most 25 each, `truncated` set when more), `sessions_without_turns`, `sessions_without_usage` (omitted when the usage table is absent or lacks a column this tool reads), and `largest_events_files[]` (`{session_id, bytes}`: the folder name and file size only, largest first, ties by name, at most 10, `truncated` set when more files exist). A missing `session-state` folder is a known state: `session_state_folder_present` is `false`, `session_state_listed` stays `true`, there are 0 folders and every session row counts as without a folder. When `session-state` exists but cannot be listed, `session_state_listed` is `false`, `session_folders`, `rows_without_folder` and `folders_without_row` are omitted (unknown, never a guess from a partial listing), the two id lists and `largest_events_files[]` are empty, and `truncated` is set. An `events.jsonl` whose size cannot be read is left out of `largest_events_files[]` and sets `truncated`.

### `events <id>`
`scan` contains `complete`, `bytes_scanned` and `lines_scanned`; an overlong line is counted but not parsed, and any scan cap makes `complete` false and `truncated` true. `event_counts` counts a line by its top-level `type` field without validating the rest of the line, so malformed lines with a readable type are included. `aborts`, `subagents_started`, `compaction_count`, `compactions[]`, `shutdowns[]`, `tools`, `permission_kinds` and `skills` count or include only lines that parse as valid JSON objects. `event_counts`, `tools`, `permission_kinds` and `skills` are ordered by count descending then name, at most 25 each. Event type, tool, permission, skill, trigger, shutdown and model names are capped at 160 characters, setting `truncated` when cut. Only the event type is inspected for other lines; selected event types are parsed for the listed metadata. Result fields also include the first 25 `compactions[]` and first 10 `shutdowns[]`; each shutdown's `models[]` has at most 25 items. Missing fields are omitted. Tool arguments, messages and file paths are never returned.

### `activity [--from DATE] [--to DATE] [--limit N]`
Database-only daily activity, grouped by UTC date from `turns.timestamp`, newest day first. Date bounds use the `sessions` parsing rules (a date-only `--to` includes that whole day) and apply to turn timestamps and usage request `created_at`. Each `days[]` item has `day`, `turns` and `chats` (distinct sessions with turns that day); when a usable `assistant_usage_events` table exists, it also has `requests`, `nano_aiu` and, when that table has `request_multiplier`, `credits` (as in `usage`) for that day. Days present only in usage are included with zero turns/chats; days with no usage requests have zero usage counts. At most `--limit` days (default 20, maximum 100); `truncated` is set when more days exist or the limit is clamped. A SQLite integer overflow while summing fails with `store_unavailable` (exit 3) regardless of which days are returned; a `credits` sum that is infinite or not a number is checked only in the days returned (within `--limit`), where it also fails with `store_unavailable`.

### `rollup [--from DATE] [--to DATE] [--limit N]`
Combines the `events` summaries of the most recent chats. Chats are chosen like `sessions`: the same date filter on the session end (last turn, falling back to `created_at`), newest first (ties by session id), at most `--limit` chats (default 20, maximum 100); `truncated` is set when more chats match or the limit is clamped. Each chat's `events.jsonl` is scanned one after another with the `events` scanner and its caps. Budgets: 60 seconds of wall-clock time and 512 MiB in total across all scans. The limits apply when each scan starts: before each scan the elapsed time (a scan does not start once 60 seconds or more have passed) and the file size (counted at most at the 128 MiB per-file cap; a scan does not start when that size would take the total past 512 MiB) are checked; when either check fails, no further scan starts, every remaining chat counts in `chats_skipped`, and `truncated` is set. A scan that has already started always finishes, so the total time can pass 60 seconds slightly, and the bytes read can pass the counted size when a file grows between the size check and the scan. A log that cannot be read (including a folder named `events.jsonl`, or a stored session id that is not a valid id) is also counted in `chats_skipped` and sets `truncated`; it never fails the command and no path is printed. A store with no chats gives zero counts.

Result: `from`, `to` (normalized bounds, omitted when not given), `limit`, `chats_considered` (= `chats_scanned` + `chats_skipped` + `chats_without_log`), `chats_scanned`, `chats_skipped`, `chats_without_log` (no `events.jsonl`), `chats_with_incomplete_scan` (scanned chats whose `scan.complete` is false), `chats_without_shutdown` (scanned completely and containing no parsed `session.shutdown`), `bytes_scanned` (budget bytes used: the larger of the counted file size and the bytes read, per chat); `event_counts[]` (`type`, `count`), `tools[]`, `skills[]` and `permission_kinds[]` (`name`, `count`): sums across chats of each chat's `events` top 25, then the top 25 by count descending then name (`truncated` set when more names exist); `aborts` and `subagents_started` (summed); `compactions` with `count` (sum of each chat's full `compaction_count`), `by_trigger[]` (`name`, `count`; a missing trigger is `(none)`), `median_pre_compaction_tokens`, `max_pre_compaction_tokens` and `median_tokens_removed`. `by_trigger[]` and the three token figures cover only the first 25 compactions of each chat (the ones `events` returns), while `count` uses the full count, so for a chat with more than 25 compactions the `by_trigger[]` counts add up to less than `count` (that chat's `events` result is truncated, which also sets `truncated` here). Token values that are missing are ignored; with an even number of values the median is the mean of the two middle values rounded down; the token figures are omitted when there are no values. `shutdown_totals` (`premium_requests`, `nano_aiu`, `lines_added`, `lines_removed`) adds up every `session.shutdown` event of each chat (at most 10 per chat, a missing value counted as 0), because each event covers only its own segment of the chat: a resumed chat has several. `top_chats_by_compactions[]` (`session_id`, `count`; chats with at least one compaction, by count descending then session id, at most 5). Any scanned chat whose own `events` result is truncated also sets `truncated`. A sum that would overflow a 64-bit integer (in the totals, the compaction count or the merged name counts) stays at the largest value and sets `truncated`. Message text, tool arguments and paths are never returned.

### `rename <id> "<title>" [--force] [--dry-run]`
The only write. Exactly one session, matched by exact id.

- Title: control characters removed (line breaks and tabs become spaces), trimmed, must be 1 to 120 characters (longer is refused, not cut).
- Steps, in order: validate (folder, `workspace.yaml`, database row, schema, active check); append a `pending` entry to the undo log with old and new values for both places; take the database write lock (`BEGIN IMMEDIATE`, waiting up to 10 seconds because Visual Studio also writes to the database while a chat is in use); update `sessions.summary`; replace `workspace.yaml` atomically (temp file in the same folder plus `File.Replace`); commit; append an `applied` entry.
- `workspace.yaml`: only the `name:` and `user_named:` lines change; all other bytes (encoding, BOM, line endings, final newline) are kept. Missing keys are appended. `name:` is written as a plain scalar when that is unambiguous and as a double-quoted YAML scalar otherwise (titles containing `:`, `#`, quotes, backslashes, leading `-` or `[`, YAML words such as `true` or `null`, or numbers).
- Active check: refused (exit 4) if `events.jsonl` or `workspace.yaml` changed in the last 10 minutes, or the newest turn is under 10 minutes old, unless `--force`. A modification time that this tool itself wrote is ignored.
- `--dry-run`: runs every check and returns the plan; opens the database read-only, writes nothing, logs nothing.
- Safety checks (exit 4 `refused`): the session-state folder, session folder or `workspace.yaml` is a symbolic link or junction (or cannot be inspected); `workspace.yaml` has more than one top-level `name:` or `user_named:` line; after the database write lock is taken, the YAML bytes or the summary differ from what was read (another process wrote meanwhile; run the command again).
- Failure behaviour: lock, missing folder or row, or an unwritable YAML leaves everything unchanged and says so. If the commit fails after the YAML was replaced, the YAML is restored only when it still equals the bytes this rename wrote; if someone else changed it since, it is left as it is and the message says so. If the YAML replace succeeded but its verification fails, the database is rolled back and the message says the YAML may differ from before.
- File replace: swapping in the new `workspace.yaml` is retried up to 5 times over about 0.4 seconds, because antivirus, indexers or Visual Studio can hold a file for a moment. Before each retry the file is compared with what was read earlier; if another program changed it meanwhile, nothing is changed and exit 4 (`refused`) is returned, so a later attempt never overwrites that edit. If every attempt fails, nothing is changed and exit 5 is returned.
- Limit: the link and stale-snapshot checks guard against mistakes and ordinary concurrent use, not against a local program that deliberately swaps folders for links during the few milliseconds of a rename.

Result: `session_id`, `dry_run`, `applied`, `forced`, `active_signals[]`, `old_title`, `new_title`, `before` and `after` (`name_line`, `user_named_line`, `summary`; each shown value is cut to 200 characters and then `truncated` is true, while the undo log keeps the full values), optional `warnings[]`, and `note`, which is always `"New title appears after Visual Studio restarts."` (the `--text` output also ends with that line). `warnings` is set when the change was applied but the final undo-log entry could not be written; `undo-rename` may then not find it, and the earlier `pending` entry in the log still holds the old values.

### `undo-rename <id> [--force] [--dry-run]`
Restores the old values of the latest applied rename of that session that has not been undone yet (repeat to step back further). Refused (exit 4) if the title, `user_named` line or summary no longer equals what the rename wrote, unless `--force`. Same active check, steps, result shape and `note` as `rename`.

## Undo log

`%LOCALAPPDATA%\VsChatHistoryToolkit\rename-log.jsonl`, one JSON object per line: `op_id`, `at`, `kind` (`rename` or `undo-rename`), `session_id`, `status` (`pending`, `applied`, `failed`), `before`, `after`, `undoes`, `forced`, `error`, `yaml_mtime_ticks`. It contains titles and summaries, so treat it as confidential. It is never committed (`.gitignore`).

## Checks against the real store

Stage 2a (read-only commands against the real store, with Visual Studio running) is documented in [STAGE2A_RESULTS.md](STAGE2A_RESULTS.md): counts and timings only, no chat text. What it settled:

1. The schema number is in the `schema_version` table (value 8); `PRAGMA user_version` is 0 in the real store. The guard was changed to match (it was wrong before the check).
2. `search_index` has its own `session_id` column, so hits are attributed without any lookup; `source_id` is `<session id>:<suffix>` for every source type seen.
3. `snippet()` works on `search_index`.
4. The `Solution:` line is found in `user.message` events only, and the toolkit's result matches an independent count exactly.
5. Visual Studio itself writes `name:` as plain, double-quoted and single-quoted scalars, so quoted values are normal.

Stage 2b (a real `rename` and `undo-rename` on the active review chat, Visual Studio running) is documented in [STAGE2B_RESULTS.md](STAGE2B_RESULTS.md): the replace of `workspace.yaml` works while Visual Studio runs, only the two YAML lines and the summary change, Visual Studio does not rewrite `name:` after the chat is used, and undo restores the file byte for byte. A second run left the chat renamed across a Visual Studio restart: the restarted chat list showed the new title and the file contents were unchanged by the restart; undo then restored the original bytes.

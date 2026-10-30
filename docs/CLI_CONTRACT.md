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
| `stats` rows per group | 25 |

Every character cap cuts the text to that many characters and appends one ellipsis character when it cuts, so a capped string can be one character longer than the cap. Caller values above a maximum are clamped and `truncated` is set. Whole chats are never printed.

## Commands

### `search <terms...> [--limit N]`
Words and numbers only; punctuation separates terms and every term is quoted for FTS5 (AND semantics). FTS5 matches word tokens, not substrings.

`hits[]`: `session_id` (read from the `search_index.session_id` column; null only if that column is empty), `source_type`, `source_id`, `snippet`. Also `query`, `terms`, `limit`, `count`.

### `sessions [--repo] [--cwd] [--branch] [--from] [--to] [--limit N]`
`--repo/--cwd/--branch`: case-insensitive "contains". `--from/--to`: `yyyy-MM-dd` or ISO-8601 (UTC); a date-only `--to` includes that whole day. They filter on the session end (last turn, falling back to `created_at` when there are no turns). Newest first.

`sessions[]`: `id`, `title`, `title_source` (`name` = workspace.yaml `name:`, `summary`, or `none`), `repository`, `cwd`, `branch`, `start` (`created_at`), `end` (`MAX(turns.timestamp)`, UTC ISO string, omitted without turns), `turn_count`.

### `show <id>`
`session` (same fields as above), `host_type`, `summary`, `user_named`, `solution` (a name; one longer than 200 characters is cut to 200 characters plus a trailing ellipsis character and sets `truncated`; or `"unknown"`), `solution_source` (`events.jsonl`; `none` when the whole file was scanned and no `Solution:` line exists; `incomplete` when the scan stopped at a cap, in which case `truncated` is also `true` and `"unknown"` is not proof of absence), `workspace_yaml_present`, `checkpoints[]` (`number`, `title`). The solution is read from the `Solution:` line in the IDE context of `user.message` events and is never inferred from folders or chat text.

### `turns <id> [--index i | --range a-b] [--max-chars N]`
`turns[]`: `index`, `timestamp`, `user_message`, `assistant_response`, `user_message_chars`, `assistant_response_chars` (original lengths), `truncated`. Also `total_turns`, `count`.

### `checkpoints <id> [--full]`
Default: `number`, `title`, `overview`, `created_at`. With `--full` also `history`, `work_done`, `technical_details`, `important_files`, `next_steps`.

### `stats`
`total_sessions`, `total_turns`, `by_repository[]`, `by_cwd[]`, `by_month[]` (each `{key, sessions}`; month is the session start `yyyy-MM`; empty values are `unknown`).

### `rename <id> "<title>" [--force] [--dry-run]`
The only write. Exactly one session, matched by exact id.

- Title: control characters removed (line breaks and tabs become spaces), trimmed, must be 1 to 120 characters (longer is refused, not cut).
- Steps, in order: validate (folder, `workspace.yaml`, database row, schema, active check); append a `pending` entry to the undo log with old and new values for both places; take the database write lock (`BEGIN IMMEDIATE`); update `sessions.summary`; replace `workspace.yaml` atomically (temp file in the same folder plus `File.Replace`); commit; append an `applied` entry.
- `workspace.yaml`: only the `name:` and `user_named:` lines change; all other bytes (encoding, BOM, line endings, final newline) are kept. Missing keys are appended. `name:` is written as a plain scalar when that is unambiguous and as a double-quoted YAML scalar otherwise (titles containing `:`, `#`, quotes, backslashes, leading `-` or `[`, YAML words such as `true` or `null`, or numbers).
- Active check: refused (exit 4) if `events.jsonl` or `workspace.yaml` changed in the last 10 minutes, or the newest turn is under 10 minutes old, unless `--force`. A modification time that this tool itself wrote is ignored.
- `--dry-run`: runs every check and returns the plan; opens the database read-only, writes nothing, logs nothing.
- Safety checks (exit 4 `refused`): the session-state folder, session folder or `workspace.yaml` is a symbolic link or junction (or cannot be inspected); `workspace.yaml` has more than one top-level `name:` or `user_named:` line; after the database write lock is taken, the YAML bytes or the summary differ from what was read (another process wrote meanwhile; run the command again).
- Failure behaviour: lock, missing folder or row, or an unwritable YAML leaves everything unchanged and says so. If the commit fails after the YAML was replaced, the YAML is restored only when it still equals the bytes this rename wrote; if someone else changed it since, it is left as it is and the message says so. If the YAML replace succeeded but its verification fails, the database is rolled back and the message says the YAML may differ from before.
- File replace: swapping in the new `workspace.yaml` is retried up to 5 times over about 0.4 seconds, because antivirus, indexers or Visual Studio can hold a file for a moment. Before each retry the file is compared with what was read earlier; if another program changed it meanwhile, nothing is changed and exit 4 (`refused`) is returned, so a later attempt never overwrites that edit. If every attempt fails, nothing is changed and exit 5 is returned.
- Limit: the link and stale-snapshot checks guard against mistakes and ordinary concurrent use, not against a local program that deliberately swaps folders for links during the few milliseconds of a rename.

Result: `session_id`, `dry_run`, `applied`, `forced`, `active_signals[]`, `old_title`, `new_title`, `before` and `after` (`name_line`, `user_named_line`, `summary`), optional `warnings[]`, and `note`, which is always `"New title appears after Visual Studio restarts."` (the `--text` output also ends with that line). `warnings` is set when the change was applied but the final undo-log entry could not be written; `undo-rename` may then not find it, and the earlier `pending` entry in the log still holds the old values.

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

Still open until Stage 2b (needs a rename on a test session you pick, with Visual Studio restarts): that Visual Studio keeps a rewritten `workspace.yaml` (`File.Replace` while Visual Studio is running), the active or loaded chat case, and whether using a renamed chat makes Visual Studio rewrite `name:`.
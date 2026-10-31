# Roadmap

Ideas for later, found by surveying `session-store.db` and the `events.jsonl` logs (field names and counts only; no chat text was read). Done so far: `usage`, `files`, peak and last prompt size.

## Next, small and database-only
- **Cross-chat ranking** (`usage-top`): chats ranked by requests, tokens or `nano_aiu` in a date range. Today an agent runs `sessions` and then `usage` on a few candidates.
- **Health report** (`doctor`, read-only, deletes nothing): biggest chats by log size, chats with no usage rows, and mismatches between the database and the session folders (167 session rows, 154 folders, 151 chats with turns, 129 with usage rows when surveyed).
- **Activity**: turns per day, active time per chat from timestamps, abort counts.

## Needs a bounded scan of `events.jsonl` (logs total about 1 GB, largest 64 MB)
- **Compaction history**: how many times a chat compacted, prompt size before and after, tokens removed, trigger, token limit (200k or 272k).
- **Session-end facts**: `codeChanges` (lines added and removed), `totalPremiumRequests`, per-model `requests.cost`, shutdown type, log size.
- **Tool and skill usage**: tools used per chat (powershell, view, grep were the most used), skills invoked per chat and overall, permission kinds, subagent runs, aborts.

## Open questions
- **Credits (checked 2026-10-31, 94 chats, read-only):** `nano_aiu` is the token-metered cost and is consistent: a chat's `session.shutdown.totalNanoAiu` equals the sum of its usage rows in 58 of 94 chats (the rest were resumed, so the database has more rows). It is **not** the "credits" shown in the chat window: AIU per premium-request cost unit ranged from 0.14 to 1,235 across chats, so no conversion exists. The credit-like figure is the premium-request cost (`totalPremiumRequests`, per-model `requests.cost`), which appears only in `session.shutdown` events in `events.jsonl`, per chat and not per reply. That the per-reply "N credits" footer equals a change in that figure is likely but not proven. `nano_aiu` stays reported as stored.
- **Date bounds** (fixed 2026-10-31): `--from` / `--to` now keep fractional seconds.
- **Chat size in tokens**: `peak_input_tokens` covers it for chats with usage rows; about a quarter of chats have none.

## Out of scope for now
Exporting whole chats (conflicts with the capped, excerpt-only rule), a WPF viewer, an MCP server, and anything that writes beyond `rename` and `undo-rename`.

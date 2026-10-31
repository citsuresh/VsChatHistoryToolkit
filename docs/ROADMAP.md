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
- **Credits**: does `nano_aiu` (or the session-end `requests.cost`) match the "N credits" shown after each reply? `nano_aiu` is token-priced (`costPerBatch` per million tokens in the compaction events). Until checked, it is reported as stored.
- **Chat size in tokens**: `peak_input_tokens` covers it for chats with usage rows; about a quarter of chats have none.

## Out of scope for now
Exporting whole chats (conflicts with the capped, excerpt-only rule), a WPF viewer, an MCP server, and anything that writes beyond `rename` and `undo-rename`.

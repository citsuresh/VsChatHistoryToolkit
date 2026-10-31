# Roadmap

Ideas found by surveying `session-store.db` and the `events.jsonl` logs (field names and counts only; no chat text was read).

## Done
`usage`, peak and last prompt size, `files`, `usage-top`, `doctor`, `events` (compactions, session-end facts, tool, skill and permission counts, aborts, subagents), `activity`, fractional-second date bounds, and `credits` (sum of `request_multiplier`) in the usage commands.

## Findings worth keeping
- **Credits (checked 2026-10-31, read-only):** `nano_aiu` is the token-metered cost and is consistent: a chat's `session.shutdown.totalNanoAiu` equals the sum of its usage rows in 58 of 94 chats (the rest were resumed, so the database has more rows). It is **not** the "credits" shown in the chat window: AIU per premium-request cost unit ranged from 0.14 to 1,235 across chats, so no conversion exists.
- **Per-reply credits:** the chat footer's credits equal the sum of `request_multiplier` over the reply's requests for main-chat requests (6 of 6 turns checked: for example 11 requests of 1.0 gave 11.0, 4 requests of 15.0 gave 60.0), so `usage` reports it as `credits`. Subagent requests carry stored multipliers, but the session-end data shows they cost 0.0, so subagent credits are unverified.
- **Chat size in tokens:** `peak_input_tokens` covers it for chats with usage rows; about a quarter of chats have none.
- **Event logs:** about 1 GB in total, largest 64 MB. A full `events` scan of the largest took 4.5 s with the 128 MiB cap.

## Ideas not built
- **Tool and skill trends across chats:** `events` is per chat; a cross-chat roll-up would need a bounded scan of many logs and a cache.
- **Compaction analysis across chats:** how often chats compact and how full they are when they do, from the same logs.
- **`doctor` extras:** chats whose log has no session-end event (crashed or still running), duplicates in `session_files`.
- **Subagent credits:** compare subagent request multipliers with the footers on a turn that used subagents, to settle whether they are billed.

## Out of scope for now
Exporting whole chats (conflicts with the capped, excerpt-only rule), a WPF viewer, an MCP server, and anything that writes beyond `rename` and `undo-rename`.

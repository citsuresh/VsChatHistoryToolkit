# Roadmap

Ideas found by surveying `session-store.db` and the `events.jsonl` logs (field names and counts only; no chat text was read).

## Done
`usage`, peak and last prompt size, `files`, `usage-top`, `doctor`, `events` (compactions, session-end facts, tool, skill and permission counts, aborts, subagents), `activity`, fractional-second date bounds, and `credits` (sum of `request_multiplier`) in the usage commands.

`rollup`: a cross-chat roll-up of the `events` summaries of the most recent chats (top tools, skills, permission kinds and event types, compaction counts, triggers and token medians, session-end totals, chats without a session-end event), bounded to 60 s and 512 MiB in total and without a cache.

`usage-turns` (`toolkit-turn-gaps`): numeric-only newest-20 usage-bearing turn snapshots (maximum 100), stable indices with gaps, UTC usage-event endpoints and whole-session totals; existing `usage --by-turn` remains first-100. Covered by generated-store tests only, without querying real chat data.

## Findings worth keeping
- **Credits (checked 2026-10-31, read-only):** `nano_aiu` is the token-metered cost and is consistent: a chat's `session.shutdown.totalNanoAiu` equals the sum of its usage rows in 58 of 94 chats (the rest were resumed, so the database has more rows). It is **not** the "credits" shown in the chat window: AIU per premium-request cost unit ranged from 0.14 to 1,235 across chats, so no conversion exists.
- **Per-reply credits:** the chat footer's credits equal the sum of `request_multiplier` over all of the reply's requests, main and subagent (7 of 7 turns checked: 11 requests of 1.0 gave 11.0; 4 requests of 15.0 gave 60.0; two Opus requests at 15.0 plus two subagent requests at 1.0 gave 32.0), so `usage` reports it as `credits` and `total` is the figure to quote. The session-end `totalPremiumRequests` is a different, much smaller figure (it matched neither the main nor the main-plus-subagent sum on any of 38 chats with subagents), so it is not the footer credits.
- **Chat size in tokens:** `peak_input_tokens` covers it for chats with usage rows; about a quarter of chats have none.
- **Event logs:** about 1 GB in total, largest 64 MB. A full `events` scan of the largest took 4.5 s with the 128 MiB cap.

## Ideas not built
- **`doctor` extras:** chats whose log has no session-end event (crashed or still running), duplicates in `session_files`.

## Out of scope for now
Exporting whole chats (conflicts with the capped, excerpt-only rule), a WPF viewer, an MCP server, and anything that writes beyond `rename` and `undo-rename`.

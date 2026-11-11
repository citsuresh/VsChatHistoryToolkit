# Project State

This file is overwritten (not appended) at the end of each working session.

## Current Focus
- `toolkit-turn-gaps`: additive `usage-turns <id> [--limit N]` for numeric newest-20 turn snapshots, real UTC usage-event endpoints and whole-session totals. Existing `usage --by-turn` stays first-100.

## Open Tasks / Known Issues
- Independent audit found clamping metadata missing on unavailable/empty `usage-turns` results. Authorized fix (`toolkit-clamp-fix`) preserves the signal and fixed note on both early-return paths, with 12 regression cases covering missing/unusable tables and empty sessions. Final build succeeded and the full generated-fixture suite passed 467 tests (previous baseline 455).
- Registered graph remains at commit `6e1aa5d`; it has not been rebuilt for the newer usage commands.
- CopilotUsageMeter and its submodule are outside this task; no real chat data or stores are used for validation.

## Recently Changed Files
- Core: `ChatStore.cs`, `Results.cs`.
- CLI: `CliRunner.cs`, `ParsedArgs.cs`, `TextFormatter.cs`.
- Tests: new `UsageTurnsTests.cs` and `UsageTurnsCliTests.cs` using generated fixtures only.
- Documentation: `README.md`, `docs/CLI_CONTRACT.md`, `docs/CODE_SUMMARY.md`, `docs/DESIGN_DECISIONS.md` and this file.

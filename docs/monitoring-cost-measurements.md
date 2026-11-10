# Cost of supervising a Copilot chat

Date: 2026-10-31. Copilot chat: the one in the `VsChatHistoryToolkit` Visual Studio window (hwnd 0x61B30). Supervisor: Claude Code (Sonnet 5.5) with the agent-orchestrator scripts. Monitoring sub-agents: general-purpose agents on the `haiku` model. Watcher: `Watch-CopilotChatV2.ps1 -RequireBusyFirst -PollIntervalSeconds 5 -TimeoutSeconds 300 -NoBeep -NoDetail`.

**What was measured.** Scenarios A, B, C, D and E (twice) were all run. They did not reach the intended durations: the Copilot chat had switched back to GPT-6 Luna, which answered B in well under a minute and C (ten files, one at a time) in about a minute, not the planned 3 to 6 and 10 to 20 minutes. So **the controlled scenarios never exercised a `Timeout` loop**; only the four real build and fix rounds (on Claude Opus 5.5, 76 to 424 s) did, and those are longer and messier than a test prompt, not a controlled comparison. The watcher's `ElapsedSeconds` starts when the watcher starts, so it understates the true wall time by the delay before the monitor ran.

**Caveats.** Spend deltas are rough: the counter lags and includes this chat's own calls. The main-chat context figures include every call made in that window (sending, committing, reading usage), not only monitoring. One sample per row.

## Results

| Scenario | Wall time (s) | Sub-agent tokens | Sub-agent tool calls | Main-chat context delta (tokens) | Spend delta (USD) | Notes |
| --- | --- | --- | --- | --- | --- | --- |
| A. One-word reply, sub-agent | 50 | 49,256 | 2 (1 watch run + hand-back) | +4,632 | +0.15 | Idle on the first run. Includes a commit and a send call in the same window. |
| E. Same prompt, polled in this chat | 50 | none | 1 watch call in this chat | +2,357 | +0.06 | 11 polls, probe median 129 ms, max 944 ms. Includes the send call and one usage read. |
| B. Five largest .md files summarised, sub-agent | watcher 6 (reply was already done when it started) | 49,399 | 2 | +5,485 | +0.21 | Valid five-file summary, GPT-6 Luna, 3.0 credits. Spend baseline included a +$1.77 jump from a three-hour idle gap (see conclusion 6). |
| C. Ten files reviewed one at a time, sub-agent | watcher 35 (true wall roughly 1 to 1.5 min) | 49,164 | 2 | +2,902 | +0.15 | All ten files reviewed ("File 10 of 10"), GPT-6 Luna, 11.0 credits. Copilot reported a real toolkit issue (`NormalizeBound` truncates fractional seconds). |
| D. Question card, two sub-agents | watcher 3 to the card, then 51 for the rest | 49,255 + 49,000 | 2 + 2 | +8,057 | +0.37 | Card detected: `ChoicePrompt`, "Which colour do you prefer?", options Red, Green, Blue. The selection script found a fourth option, "Other", that the watcher did not list. Answered Green with `Select-ChoicePromptOption.ps1`; final reply "Green", 2.0 credits. Context delta includes my card calls. |
| E. B's prompt, polled in this chat | 15 | none | 1 watch call in this chat | +2,138 | +0.00 | 4 polls, probe median 567 ms, max 1,555 ms. Spend unchanged at the counter's resolution. |
| Real build 3 (events + activity), sub-agent: **long wait, two Timeout loops** | about 628 (301 + 302 + 25) | 49,743 | 4 (3 watch runs + hand-back) | not isolated | not isolated | The longest wait measured. Agent duration 665 s. Two `Timeout` results, then Idle. Tokens are no higher than for a 50-second watch. |
| Real fix round 3 (events findings), sub-agent | 137 | 48,946 | 2 | not isolated | not isolated | Idle on the first run. Agent duration 150 s. |
| Real build 1 (files + prompt size), sub-agent | about 337 | 50,598 | 3 (2 watch runs + hand-back) | not isolated | not isolated | Timeout at 305 s, then Idle at 32 s. Agent duration 380 s. |
| Real fix round 1, sub-agent | 76 | 49,182 | 2 | not isolated | not isolated | Idle on the first run. Agent duration 85 s. |
| Real build 2 (usage-top + doctor), sub-agent | about 402 | 49,393 | 3 | not isolated | not isolated | Timeout at 301 s, then Idle at 101 s. Agent duration 424 s. |
| Real fix round 2, sub-agent | 156 | 48,911 | 2 | not isolated | not isolated | Idle on the first run. Agent duration 165 s. |

Whole rounds, for scale (everything in the round: Copilot builds, two or three review rounds, tests, commits, sub-agents): features 1 and 2 cost $1.01 of spend and +24,270 tokens of main context; `usage-top` and `doctor` cost $0.93 and +20,624 tokens. No failures occurred: no foreground-activation failure during sends, no empty reply, no ProbeError. One review reply was missed by the wrapper (the chat list is virtualized and showed 3 rendered items) and was read from the transcript directly.

## Prompts used

- Scenarios A and E, identical: `Test only, change nothing and run no tools that change files. Reply with the single word DONE.`
- Real rounds: the build and fix prompts for each feature, sent with `Send-CopilotPrompt.ps1` (not repeated here; they name this repo's files).
- Sub-agent instruction: run the watcher command above, repeat on `Timeout` up to a cap, never send or click, report each State and ElapsedSeconds and its tool-call count.

## Conclusion

1. **A sub-agent has a fixed cost of about 49,000 tokens, whatever it waits for.** Five runs from 50 s to 424 s used 48,911 to 50,598 tokens. The waiting itself is nearly free; the cost is starting the agent (its prompt, tools and context).
2. **For a wait that fits in one watch call (up to about 5 minutes), polling in the main chat is cheaper.** Scenario E added about 2.4k tokens to the main context and about $0.06 of spend for the whole send, watch and usage-read sequence, against 49k sub-agent tokens and about $0.15 for the same prompt with a sub-agent (the extra roughly $0.09 is a rough estimate of the sub-agent's price).
3. **A sub-agent keeps the waiting out of the main context.** A watch call that ends in `Timeout` adds a few hundred tokens to the main context, and every main-chat call re-reads the whole context (here about 400k tokens, mostly cached). With many repeated polls (a 20-minute run is four or five 300-second calls) the main-chat route grows, and re-reads the context each time, while the sub-agent stays at about 49k. The break-even is not measured. A rough guess from these numbers is somewhere around five or more polls, which is about 25 minutes, and it depends on how large the main context is: the bigger it is, the sooner a sub-agent pays off.
4. **Practical rule from this data:** poll in the main chat for waits under about 10 minutes; use a sub-agent for long runs, when the main context is large, or when the main chat has other work to do while waiting. The cheapest option for long runs may be one watch call with a long `-TimeoutSeconds`, if the tool's own time limit allows it (this setup caps a call at about 10 minutes).
5. **The extra runs agree.** Ten sub-agent runs now (50 s to 424 s, including the two-agent scenario D) used 48,911 to 50,598 tokens each, and the in-chat polls of B's and A's prompts added about 2.1k to 2.4k tokens of main context against 2.9k to 5.5k for the sub-agent versions of the same kind of prompt (those include the extra calls that launch and read the sub-agent).
6. **An idle gap costs more than any monitoring choice.** Before scenario B the spend counter jumped $1.77 after about three hours of inactivity, against $0.06 to $0.37 per scenario. The likely cause is that the next call after the cache expired had to re-write this chat's whole context (about 410k tokens). Keeping a long, large session warm, or starting a fresh small session for supervision work, matters more than sub-agent versus in-chat polling.
7. **A long wait with repeated `Timeout` loops, measured on a real build (not a controlled prompt):** 628 s of watching with two `Timeout` results used 49,743 tokens, the same as a 50-second watch. So the sub-agent cost really is fixed (twelve runs from 50 s to 628 s, 48.9k to 50.6k tokens). What is still not measured is the in-chat cost of the same long wait (each `Timeout` call adds a few hundred tokens and re-reads the whole main context), so the exact break-even is not known. On a 485k-token main context, three in-chat watch calls would re-read roughly 1.4 million cached tokens; the sub-agent's 49.7k tokens are on a model that bills far less per token, so for long waits in a large session the sub-agent is very likely cheaper, but that is an estimate, not a measurement.
8. **Card handling observations:** the watcher's detail extraction listed three options where the card had four (it missed "Other"); the selection script listed all four. A supervisor should read the options from the selection script before answering, not from the watcher.

## Live checks of the watcher paths (2026-10-31)

| Path | Result | Evidence |
| --- | --- | --- |
| Single question card | Pass | Scenario D: card detected as `ChoicePrompt`, answered with `Select-ChoicePromptOption.ps1`, final reply correct. |
| Multi-question card (Next question, then Submit) | Pass | Two-question card: watcher reported `MorePages: true`; answering page one reported `NextQuestion` ("Which animal do you prefer?") with the card still open; answering page two closed the card and echoed both answers; reply "green fox". |
| `Timeout` loop | Pass | Happened for real in long builds (301 s and 302 s), the monitor re-ran the watcher and got `Idle` at the end. |
| `WindowGone` | Pass (earlier session) | Validated with a bad window handle. A real mid-wait window close was not tried (it would close a working Visual Studio window). |
| End-of-wait beep | Pass | One short beep heard when the watcher finished (user confirmed). |
| Switching to a new chat mid-wait | **Limitation found** | The user clicked New chat about 9 s into a task. The stop button vanished, the watcher kept probing the new empty chat without errors (probes got faster: about 140 ms), and after its 20 s quiet period it reported `Idle` with the note "No stop button and no prompt for the quiet period", while the original chat was still working. The watcher follows the visible chat pane, not one conversation. The note text differs from a normal finish ("the chat ends in a completion footer"), and the documented rule applies: always confirm the reply with `Read-CopilotReply.ps1` before trusting `Idle`. |
| `ProbeError` | Not tested | Needs repeated probe failures while the window still exists; no safe way to induce it. |
| Stale-pane retry (probe exception on a replaced pane) | Not triggered | Switching chats did not raise a probe exception; the pane was found again on the next probe. |
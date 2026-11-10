# Publishing the toolkit

When the user asks to publish this toolkit, run the repository script `scripts\agent\Publish-VsChatHistoryToolkit.ps1` rather than manually building or copying outputs.

From the repository root, use:

```powershell
& '.\scripts\agent\Publish-VsChatHistoryToolkit.ps1' -RepositoryPath (Get-Location).Path -SharedPath 'C:\Shared'
```

Pass both paths explicitly because the script's defaults refer to the original checkout and `C:\Shared`. Honor any source or destination path explicitly requested by the user instead.

The script publishes a self-contained Windows x64 Release build into the timestamp-free `C:\Shared\VsChatHistoryToolkit-win-x64` subfolder, copies the usage documentation, and verifies the executable. Report the output path only after the script succeeds. Do not copy local chat history into the package.

<!-- project-memory-management-graph: skill-version=24 -->
## Persistent Project Memory

Before exploring code for a new task, read `docs/CODE_SUMMARY.md` and `docs/DESIGN_DECISIONS.md` if present. When the user asks what was done before, refers to prior work, or asks what's next, read `docs/PROJECT_STATE.md` and `docs/ROADMAP.md` if present. Read `docs/domain-lookup-patterns.md` before manual exploration when a question depends on domain conventions not represented in the graph. Read `docs/FEATURE_CHECKLIST.md` when planning or adding a feature if it exists.

### Solution graph registry

| Solution path relative to repo root | Slug | Graph file | Dependencies file |
| --- | --- | --- | --- |
| `VsChatHistoryToolkit.slnx` | `vschathistorytoolkit` | `docs/full-graph.vschathistorytoolkit.json` | `docs/project-dependencies.vschathistorytoolkit.json` |

Query registered graphs through `C:\MyFiles\Git\GraphTools\tools\Invoke-GraphTools.ps1`, invoked as `C:\MyFiles\Git\GraphTools\tools\Invoke-GraphTools.ps1 -Tool Query -- <args>`. Never read a graph wholesale.

### Graph-first lookups

Before using general search to locate or relate C# classes, interfaces, enums, methods, callers, or callees, query the registered solution graph first, including for explanatory questions such as "how does X work?" Determine the current IDE solution only when it is available from context; otherwise treat it as unknown. Search the matching graph first when known, then other registered graphs. Label results with their solution and say if a symbol appears in multiple graphs.

Use the wrapper templates:

```powershell
C:\MyFiles\Git\GraphTools\tools\Invoke-GraphTools.ps1 -Tool Query -- --graph "C:\MyFiles\Git\VsChatHistoryToolkit\docs\full-graph.vschathistorytoolkit.json" --symbol "<fully-qualified-id>"
C:\MyFiles\Git\GraphTools\tools\Invoke-GraphTools.ps1 -Tool Query -- --graph "C:\MyFiles\Git\VsChatHistoryToolkit\docs\full-graph.vschathistorytoolkit.json" --symbol "<fully-qualified-id>" --direction callers
C:\MyFiles\Git\GraphTools\tools\Invoke-GraphTools.ps1 -Tool Query -- --graph "C:\MyFiles\Git\VsChatHistoryToolkit\docs\full-graph.vschathistorytoolkit.json" --symbol "<fully-qualified-id>" --direction callees
C:\MyFiles\Git\GraphTools\tools\Invoke-GraphTools.ps1 -Tool Query -- --graph "C:\MyFiles\Git\VsChatHistoryToolkit\docs\full-graph.vschathistorytoolkit.json" --list-symbols --project "<project name>"
```

If no registered graph exists, a graph file is missing, a query fails, or no graph answers the specific question, state that explicitly before falling back to normal search; include full errors for failures. A symbol-not-found response may mean the graph is stale. If graph/domain investigation reveals a recurring friction point, briefly mention it at the end of the response; do not proactively search for such issues.

### Memory maintenance

- Update `docs/CODE_SUMMARY.md` only when projects, structural classes/services, responsibilities, or project dependencies change. Keep the pointer to `docs/KEY_FLOWS.md`; record traced multi-symbol flows in that file as short arrow chains, not in the summary.
- Append dated, non-obvious decisions to `docs/DESIGN_DECISIONS.md`; never rewrite older entries.
- At session end, overwrite `docs/PROJECT_STATE.md` with the current focus, open tasks/issues, and recently changed files if it exists. Update `docs/ROADMAP.md` only when priorities deliberately change.
- `docs/domain-lookup-patterns.md`, `docs/FEATURE_CHECKLIST.md`, and `docs/KNOWN_OPEN_FINDINGS.md` are user-curated; do not create or change them without explicit user direction. Before adding an explicitly requested known finding, check for a similar entry and ask whether it is a recurrence or a new issue.

- Reusable scripts: before writing code for a repeatable mechanical step (filtering a log, applying the same edit to many files, extracting data from several files), check `docs/SCRIPTS.md` if it exists and run a script that fits, as its index line says; if the index line says it modifies or deletes files, ask the user before each run, and check its output before relying on it. If you wrote working code for such a step that is likely to be reused with different inputs, is more than a one-liner, can take its inputs as parameters, and contains no credentials, tokens, customer data or personal details, save it as `scripts/agent/<verb-noun>.ps1` with a header comment (purpose, parameters, whether it modifies anything), add one line to `docs/SCRIPTS.md` (table: script | purpose | how to call | read-only or modifies | date; create the file with the table header if it is missing), and say so in your reply. Before saving, check whether an existing script in `scripts/agent/` can take the new case as a parameter. If one can, extend that script with a backward-compatible parameter (its default keeps the current behaviour, and existing parameter names, order and output format stay unchanged), update its header comment, index line and date, and say so, rather than saving a near-copy. If the task's file rules forbid changing the existing script, do not save a near-copy; say in your reply which parameter the existing script needs. Every part that varies between uses (paths, patterns, comment markers, row counts, exclusions) must be a parameter. For C# projects, skip `obj` and `bin` folders by default, with a switch to include them. Before finishing a task, check once whether you wrote any script worth saving. If a script fails or is out of date, fix it in place and update its date, or do the step normally; never loop on a broken script. When a task runs a listed script, set its date in `docs/SCRIPTS.md` to today if the task's file rules allow editing that file; otherwise leave the date unchanged and say so in your reply. The user curates these files: never delete scripts or index lines on your own.

## Project Guidelines
- Manual commit review before any commit.
- Build/test verification after every change.
- Do not commit or push automatically — wait for explicit user confirmation first.
- If the `vs-chat-history` skill is installed, use it to search earlier Visual Studio Copilot Chat conversations on this machine (for example "what did we decide about X", "which chat worked on Y"). It reads `session-store.db` only, and chat text is confidential: show short excerpts only. Ignore this note if the skill is not installed.

## Response Guidelines
Keep replies concise and minimal by default: no filler, no restating the question, and no unnecessary preamble. Give full detail for design rationale, build/error diagnosis, before destructive actions, when multiple approaches exist, and when generating `docs/*.md` content.

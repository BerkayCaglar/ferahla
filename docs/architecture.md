# Architecture

One NativeAOT executable, `ferahla.exe`, with three faces:

| Mode | What |
|---|---|
| `ferahla` / `ferahla ui` | Local web server on `127.0.0.1` + the browser window |
| `ferahla <command> [--json]` | CLI: `scan`, `scans`, `findings`, `diff`, `inspect`, `verify`, `report`, `quarantine`, `restore`, `note` |
| `ferahla mcp` | stdio MCP server, started by Claude Code during an analysis |

```
src/Ferahla.Core      model, rules engine, findings, diff, quarantine plan/manifest + MoveGuard, notes,
                      HTML report, stream-json parser. Platform-neutral, unit-tested.
src/Ferahla.Windows   scanner (FileSystemEnumerable), known folders, registry apps, coded detectors,
                      quarantine engine (MoveFileExW, Restart Manager, private ACL), Verifier,
                      SystemActions, AppsService, JobObject
src/Ferahla.Server    Kestrel (slim builder), Security gate, embedded web/, API, jobs + SSE, Claude analysis runner
src/Ferahla.Mcp       MCP server (official C# SDK): read-only tools + add_note/add_notes
src/Ferahla           the exe: System.CommandLine commands
web/                  the UI: plain ES modules + one stylesheet, embedded into the exe
rules/                declarative rules (JSON), embedded; see rules.md
prompts/              Claude's instructions (en, tr), embedded
```

## Data flow

1. **Scan** walks one drive or folder in a single pass (`NtQueryDirectoryFile` via `FileSystemEnumerable`, parallel near
   the root), keeps folders of 1 MB or more and notable files, and saves a compressed snapshot.
2. **Findings** = rules + detectors over the snapshot (detectors may read the disk: zip listings, project files,
   registry), then the guard, dedupe and nesting. Finding ids are stable across scans.
3. **Quarantine** takes a plan (a selection), recomputes the findings of that scan, accepts only items still suggested,
   checks each against `MoveGuard`, and renames them into the same drive's quarantine with a write-ahead manifest.
4. **The UI** talks to the server over JSON (token header) and listens to one SSE stream for job progress, analysis
   events and "data changed" signals.
5. **Claude analysis** starts `claude -p` isolated in `claude-work`, with `ferahla mcp` as its only tool server; notes
   land in `notes.json` and show up everywhere findings are shown.

See [SECURITY.md](../SECURITY.md) for the threat model and [spikes.md](spikes.md) for what was measured before
building each part.

# Ferahla: notes for Claude sessions

Ferahla is an open-source Windows disk scanner and safe-cleanup tool: one NativeAOT exe (.NET 10) that serves a local web UI, a CLI and an MCP server. Done: M0 spikes (results in `docs/spikes.md`), M1 (scan, rules, findings, report), M2a (quarantine CLI), M2b (local server + UI), M3 (Claude analysis over MCP), M4 (verify, system actions, apps & games, history compare). Next: a UI polish pass, then M5 release.
System actions are a fixed allowlist in `Windows/SystemActions.cs` (elevated per step; never `/ResetBase`); apps are removed only through their own uninstaller or the launcher (`AppsService`).

## Privacy: nothing about the owner goes into this repo

This repo is public. The tool is developed against the owner's real machine and real scan data, so leaks are easy. Rules:

- **Never commit personal or machine data.** This covers OS username, real paths (`C:\Users\<name>\…`), machine name, email addresses (the GitHub noreply author address is the only exception), account, org or subscription details, Claude usage and rate-limit numbers, login expiry, installed software or plugin lists, and real file, folder, project, game or media names and sizes from the owner's disk.
- **Real scan reports never enter the repo** (`disk-raporu-*.json`, `scans/`, quarantine folders; see `.gitignore`). The owner's plan and reports live outside this repo.
- **Mock data must be invented**, not copied from a real scan. Use neutral values like "Sample build cache", 4.2 GB on a 500 GB disk. Don't round or rename real values.
- **Captured tool output** (e.g. `claude -p` stream-json) goes into `tests/fixtures/` only after `node tools/sanitize-fixture.mjs`. It replaces paths, request, message and tool IDs, session UUIDs, timestamps, rate-limit info, credential expiry and skill/plugin lists with fixed fakes. Generate fixtures against the spike MCP server's invented data, not real data. Prefer the cheapest model (`--model haiku`) for captures.
- **Before every commit**, run `powershell -NoProfile -File tools/check-private.ps1` (scans staged files; `-All` scans everything). It must report clean. Also eyeball the diff: the script can't recognise every personal project or file name.
- If something personal was already committed and not yet pushed, rewrite the history and `git gc --prune=now`. If it was pushed, tell the owner immediately; don't try to hide it.

## Git

- Commits and PRs are authored by the owner only. **Do not add `Co-Authored-By: Claude …` lines, "Generated with Claude Code" footers, or any other AI attribution** to commit messages, PR descriptions, tags or release notes.
- Commit or push only when the owner asks.
- Everything in the repo is in English: commit messages, code, comments, docs, test data, tool scripts. Turkish appears only as product localization (`tr` texts in `rules/*.json`, the second argument of `Lang.T(en, tr)`, `README.tr.md`, the report's `tr` strings).

## Build

- SDK is pinned in `global.json` (.NET 10.0.403, `latestFeature`).
- NativeAOT on Windows needs VS 2022's "Desktop development with C++" and **`vswhere.exe` on PATH**: add `%ProgramFiles(x86)%\Microsoft Visual Studio\Installer` to PATH, or the link step fails with `'vswhere.exe' is not recognized`.
- `Directory.Build.props` sets `PathMap`, `Deterministic` and `/PDBALTPATH:%_PDB%`, so binaries don't embed the developer's local paths (username, folder layout). Don't remove it. Release binaries are built in CI, never shipped from a local machine.
- AOT rules: no reflection-based JSON (use `JsonSerializerContext` source generation), no native dependencies, and every new NuGet package must publish with zero AOT warnings.
- Windows PowerShell 5.1 reads BOM-less `.ps1` files as ANSI, so keep `.ps1` scripts ASCII-only. When piping text to native exes it also mangles non-ASCII, so write UTF-8 bytes to stdin directly.

## Layout

- `src/Ferahla.Core/`: snapshot model, rules engine (`Findings/`), diff, store, HTML report, JSON context. Platform-neutral and testable.
- `src/Ferahla.Windows/`: scanner (`FileSystemEnumerable`, one pass), registry/known folders, coded detectors (`Detectors/`).
- `src/Ferahla/`: the `ferahla` exe (System.CommandLine). Commands: `scan`, `scans`, `findings`, `diff`, `inspect`, `report`, `quarantine`, `restore`, and `ui` (the default with no command).
- Quarantine: `Core/Quarantine/` holds the plan/manifest model, `MoveGuard` (paths that never move, whatever the plan
  says) and `PlanValidator` (a plan item moves only if a fresh findings run of its scan still suggests that exact item).
  `Windows/Quarantine/` holds the engine (`MoveFileExW` rename without copy fallback, write-ahead manifest per batch and
  volume, private ACL for `X:\.ferahla`) and Restart Manager lock lookup, which runs only after a move failed. Keep it
  that way: never add a CLI or MCP command that deletes for good; that is a UI button only.
- `src/Ferahla.Server/`: the local UI server (Kestrel slim builder, NativeAOT). `Security.cs` is the gate every request
  passes (Host allowlist, Origin and Sec-Fetch-Site checks, per-launch token header on `/api`, POST = JSON only, strict
  CSP); `ServerTests.cs` pins each rule, keep them green. Handlers are plain `RequestDelegate`s with the `ApiJson`
  source-generated context (no minimal-API reflection). One job at a time (`Jobs`), progress over SSE (`EventHub`).
  `FindingsService`, `ScanService` and `PlanRunner` in Ferahla.Windows are shared by the CLI and the server.
- AI analysis (Claude Code or Codex): `src/Ferahla.Mcp/` is `ferahla mcp` (official C# MCP SDK, stdio; read-only tools + `add_note`/`add_notes`, never
  a tool that moves or deletes). `Server/Analysis.cs` runs `claude -p` isolated (`--tools "" --strict-mcp-config
  --setting-sources "" --permission-prompts none`, neutral `claude-work` folder, job object, `ANTHROPIC_API_KEY`
  removed) and stops if the init event lists any non-Ferahla tool; that isolation lives in `Server/AgentCli.cs` (`ClaudeCli`, `CodexCli`: `codex exec --json
  --ignore-user-config --ignore-rules`, read-only sandbox, approvals never, skills/web/shell/apps/plugins off, keep
  `code_mode_host` on or no MCP tool works; Codex lists no tools, so `CodexParser.Check` stops at the first foreign item),
  shared with `Server/Chat.cs` (per-suggestion drawer, `web/js/chat.js`: one session per finding, follow-ups
  `--resume`, one run at a time and never next to an analysis, kept in memory only). `Core/Claude/StreamParser.cs` turns stream-json into
  UI events (tested against `tests/fixtures/claude/`). Notes live in `<home>\notes\notes.json` (`Core/Notes`), keyed by
  the stable finding id. `prompts/analyze.{en,tr}.md` are embedded with `WithCulture="false"` (otherwise MSBuild puts
  the `.tr.md` into a satellite assembly). Don't run real analyses casually: they spend the owner's Claude plan; use
  the fixtures and `--model haiku` when a real run is unavoidable.
- Updates: `Server/Updates.cs` is Ferahla's only network access (GitHub's latest release of this repo, only while a UI
  tab is open, switch in `<home>/update.json`; tests never set `ServerOptions.CheckUpdates`). Self-update downloads
  `ferahla.exe` + `.sha256` from that release only, runs `--version`, renames the running exe to `*.old.exe`, and
  `UiCommand` restarts it with the same port and token (`FERAHLA_RESTART_*`, cleared on read) so the tab reloads.
- `web/`: the UI, embedded into the exe (no build step): `index.html`, `css/app.css` (design tokens, light/dark),
  `js/` ES modules (`app.js` shell + router, `store.js`, `api.js`, `ui.js`, `i18n.js` with en+tr, `views/*`). CSP has
  no `unsafe-inline`: never use inline `style`/`on*` attributes or innerHTML; set styles through the CSSOM.
- `rules/*.json`: declarative rules (path / marker / dirName / detector), embedded into the exe; `rules/schema.json` for editors.
- `tests/Ferahla.Core.Tests/`: xUnit v3 on real temp trees (`TempTree`). Test data is invented; never real scans.
- Scans live in `%LOCALAPPDATA%\Ferahla\scans` (override with `FERAHLA_HOME`, as tests and CI do). UI language follows Windows; override with `FERAHLA_LANG=en|tr`.
- Detector lessons, kept here so they aren't relearned: registry DisplayVersion is often stale (apps that update in place);
  `win64_3.1.0` and `x64_2_4_8` contain "64" that is not a version; a stray `package.json` in the profile must not make the
  profile a "project"; same-length audio stems can share head and tail, so duplicate probes sample the middle too;
  Win32 structs with FILETIME use two `uint`s, never `long`.
- `spikes/`: M0 experiments. Reference only; don't build product code on top of them.
- `docs/spikes.md`: what was verified, measured and decided, including the `claude -p` stream-json event schema.
- `tests/fixtures/claude/`: sanitized stream-json captures.
- `tools/`: `sanitize-fixture.mjs`, `check-private.ps1`.

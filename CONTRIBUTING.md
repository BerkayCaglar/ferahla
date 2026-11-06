# Contributing to Ferahla

Thanks for helping. Ferahla moves people's files around, so the bar is: every suggestion must be explainable, and
nothing may ever be lost. Small, well-tested changes are the easiest to review.

## Build and test

Windows 10 1809+ or 11 (x64), the .NET 10 SDK (pinned in `global.json`), Node.js (for the web check) and Visual
Studio 2022's *Desktop development with C++* workload for the NativeAOT link step. `vswhere.exe` must be on `PATH`:

```powershell
$env:Path += ";${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
dotnet test --solution Ferahla.slnx          # unit, scanner, quarantine, server security, MCP and parser tests
node tools/check-web.mjs                     # browser code: syntax, no innerHTML or inline script
dotnet publish src/Ferahla -c Release -o publish
```

Run the UI from source with `dotnet run --project src/Ferahla` (it opens your browser). Set `FERAHLA_HOME` to a
throwaway folder to keep test scans away from your real ones, and `FERAHLA_LANG=en|tr` to pick the language.

## Adding a rule

Most suggestions are declarative rules in [`rules/`](rules), validated by [`rules/schema.json`](rules/schema.json)
(your editor picks it up through `$schema`). See [docs/rules.md](docs/rules.md) for the format. A good rule:

- names exactly one kind of thing, with a `title`, a one-line `reason`, `whatBreaks` and `howToRestore`, in English
  **and** Turkish (ask in the PR if you can't write the Turkish; someone will);
- uses the most careful `risk` that is true (`safe` only if nothing at all breaks);
- starts with `"maturity": "pending"` if you aren't sure; pending rules only run with `ferahla findings --include-pending`;
- comes with a test in `tests/Ferahla.Core.Tests` built on an invented `TempTree`.

Rules never point inside `C:\Windows` or `Program Files`; the engine blocks it anyway. Programs and games are removed
through their own uninstaller or launcher (`action: uninstall` / `launcher`), never by deleting their folder.

## Ground rules for code

- NativeAOT: no reflection-based JSON (add types to a `JsonSerializerContext`), no native dependencies, and a new NuGet
  package must publish with zero AOT warnings. Warnings are errors.
- Never add a CLI command or MCP tool that deletes for good. Permanent delete is a button in the UI, behind a
  confirmation, and only for items already in quarantine.
- Every request to the local server passes `Security.cs`; keep `ServerTests` green and add a test for any new rule.
- The UI has a strict CSP: no inline scripts, styles or handlers, no `innerHTML`. Build DOM with `h()` from `ui.js`.
- English everywhere in the code base; Turkish only as product text (`Lang.T(en, tr)`, `tr` fields, `i18n.js`).

## Privacy

Tests and examples use invented data only: never paths, names or sizes from a real disk. Before committing, run
`powershell -NoProfile -File tools/check-private.ps1` (it scans what you're about to commit). Captured Claude output
goes through `node tools/sanitize-fixture.mjs` before it can become a fixture.

## Pull requests

One topic per PR, with tests. Describe what you changed and how you checked it (which tests, which screens). CI runs
the privacy check, the web check, all tests, a NativeAOT publish and a smoke test.

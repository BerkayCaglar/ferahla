# Ferahla

**Find out what fills your Windows disk, and clean it up without breaking anything.**

[Türkçe](README.tr.md)

> **Status: early development.** Ferahla can scan a drive and explain what it finds. It cannot move or delete
> anything yet; quarantine and the browser UI are next. There are no releases yet: build from source.

Most disk cleaners show you big folders, and the rest is up to you. Ferahla tells you for each suggestion:

- **what it is**: a cache, a build output, an installer you already ran, an archive you already extracted…
- **how risky it is**: *safe*, *regenerable* (comes back at a cost), *review* (look first) or *personal* (info only)
- **what breaks** if it goes and **how to get it back**
- **the evidence**: "zip contents present in the folder: 33/33", "none of your Unity projects uses this editor",
  "same remote as `my-game`", "content matches at 18 places"

It never suggests Windows itself, the page file, files of a virtual machine that is running, or anything inside
Program Files for removal by hand.

## What it finds

| | Examples |
|---|---|
| Caches | Temp, pip, uv, npm, NuGet, Cargo, Gradle, conda, Unity, GPU shader caches, browser caches |
| Build outputs | `target/`, `node_modules/`, `bin/obj`, Unity `Library/`, CMake, Gradle, Python `.venv`… found by project markers |
| Installers | setup files in Downloads/Desktop for apps that are already installed (the newest one stays) |
| Archives | `x.zip` next to its extracted `x\` (verified entry by entry), old multi-part archives never extracted |
| Duplicates | large loose files with identical content (hardlinks excluded); the copy in a project folder stays |
| Old versions | Unity editors no project uses, old CLI versions, old Playwright browsers, older Windows SDKs |
| Repository copies | second working copies of the same git remote, ParrelSync clones |
| Games | Steam, EA, Riot and Epic games, to remove from their launcher |
| System | hibernation file (`powercfg -h off`), WinSxS (DISM, never by hand), WSL disk, space the scan can't see |

Rules live in [`rules/`](rules) as JSON, with English and Turkish texts.

## Usage

```
ferahla scan                 scan the system drive (or: ferahla scan D:)
ferahla findings             classified suggestions for the latest scan
ferahla findings --risk safe
ferahla diff                 what grew or shrank since the previous scan
ferahla inspect <path>       size, subfolders and findings for one folder
ferahla report --open        a single-file, read-only HTML report in your browser
ferahla scans                saved scans
```

Every command takes `--json` (versioned schema, meaningful exit codes), so scripts and AI agents can use Ferahla too.

A full scan of a drive with a few million files takes about a minute the first time and well under a minute after
that. Scans are saved under `%LOCALAPPDATA%\Ferahla\scans`.

## Privacy

- No telemetry, no network access. The HTML report loads nothing from the internet.
- Scans and reports stay on your machine. **The report contains your file paths**: look at it before you share it.

## Building

Needs Windows 10 1809+ or 11 (x64), the .NET 10 SDK, and Visual Studio 2022's *Desktop development with C++*
workload for the NativeAOT link step. `vswhere.exe` must be on `PATH`:

```powershell
$env:Path += ";${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
dotnet test --solution Ferahla.slnx
dotnet publish src/Ferahla -c Release -o publish   # publish\ferahla.exe, a single ~5 MB file
```

## Roadmap

1. Quarantine: move selected items aside instantly, undo, then delete for good (only from the UI)
2. A local browser UI with the same report, live
3. Optional analysis with your own Claude Code installation, writing notes into the report
4. Deeper checks, system actions, apps and games pages, history

## License

[MIT](LICENSE). Third-party notices: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Ferahla finds out what fills your Windows disk and cleans it up without breaking anything.

**Download** `ferahla.exe` below (one file, no installer) and run it: it opens in your browser. Check the file against
`ferahla.exe.sha256` if you like: `Get-FileHash ferahla.exe -Algorithm SHA256`.

The file is not code-signed yet, so Windows SmartScreen may warn on first run: choose **More info → Run anyway**.

## What's in it

- Fast full-drive scan, saved so the next one can tell what grew
- Suggestions with risk, evidence, what breaks and how to get it back (caches, build outputs, installers you already
  ran, extracted archives, duplicates, old SDKs and editors, repository copies, games, system items)
- Undoable quarantine: items move aside instantly on the same drive; put them back, or delete for good from the window
- Explorer with a treemap, large files, apps and games, history and scan comparison
- `ferahla verify`: duplicates compared byte by byte, zip archives against their folder by CRC-32
- An overview bar that shows where all the used space goes: safe to clean, needs a look, apps, Windows and your own files
- Optional second opinion from your own Claude Code installation: notes for every suggestion (or only the quarantined
  ones before you delete them), and a side panel per suggestion for a quick or deep analysis and your own questions
- Progress for quarantine, restore and delete; open any suggestion in Windows Explorer
- Updates itself: a bar shows when a new version is out, and **Update now** swaps it in and reloads the window
- Everything also on the command line with `--json`; English and Turkish; light and dark

## Requirements

Windows 10 1809 or later, or Windows 11 (x64). No .NET installation needed.

See [SECURITY.md](https://github.com/BerkayCaglar/ferahla/blob/main/SECURITY.md) for how your files and the local
window are protected, and [docs/privacy.md](https://github.com/BerkayCaglar/ferahla/blob/main/docs/privacy.md):
no telemetry; the only network request is the update check, which you can turn off.

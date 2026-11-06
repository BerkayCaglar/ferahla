# Privacy

Ferahla runs entirely on your PC.

- **No telemetry, no accounts, no network.** Ferahla makes no internet connections. Its window is a page served by
  `ferahla.exe` itself on `127.0.0.1`, and loads nothing from the internet.
- **What it stores**, all under `%LOCALAPPDATA%\Ferahla` (or `FERAHLA_HOME`):
  - `scans\`: one compressed snapshot per scan (folder names, sizes, dates, notable file names) and a small summary;
  - `quarantine\` (and a hidden `X:\.ferahla\quarantine` on other drives): moved items and their manifests;
  - `notes\notes.json`: notes on findings, yours and Claude's;
  - `claude-work\`, `claude-usage.json`: only if you use the Claude analysis (see below);
  - `ui.json`: the address and key of the running window, while it runs.
  Delete the folder to remove everything. Uninstalling is deleting `ferahla.exe`.
- **The HTML report** contains your file paths. It goes where you save it and nowhere else; look before you share it.

## Claude analysis (optional)

Only if you start it. Ferahla then runs **your own** Claude Code installation, logged in to **your own** Claude
account, and the analysis is subject to Anthropic's terms and privacy policy for that account.

- Claude sees what Ferahla's tools return about the scan you chose: folder and file names, paths, sizes and dates,
  and Ferahla's findings. Nothing else on your PC; it has no file, shell or web tools.
- Ferahla never reads, stores or sends your Claude credentials. From `claude auth status` it reads only whether you
  are logged in, the login method and the plan type; your e-mail and organization are ignored.
- Your plan usage (5-hour and weekly percentages) is shown from the events Claude Code reports, and the last value is
  kept in `claude-usage.json` so it can be shown before the next run.

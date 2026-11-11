# Privacy

Ferahla runs entirely on your PC.

- **No telemetry, no accounts.** Ferahla's window is a page served by `ferahla.exe` itself on `127.0.0.1`, and loads
  nothing from the internet.
- **One network request: the update check.** While the window is open, Ferahla asks GitHub's public API for this
  project's latest release (`api.github.com/repos/BerkayCaglar/ferahla/releases/latest`): when it starts, every 15
  minutes, and when the window gets focus (at most every 2 minutes). Nothing is sent except that request (GitHub sees
  your IP address and a `Ferahla/<version>` user agent, like any web request). Turn it off with the switch behind the
  version number in the sidebar. **Update now** downloads `ferahla.exe` from that release only, checks its SHA-256
  and version, and replaces the running exe; your scans, notes and quarantine stay. The command line and the MCP
  server never go online.
- **What it stores**, all under `%LOCALAPPDATA%\Ferahla` (or `FERAHLA_HOME`):
  - `scans\`: one compressed snapshot per scan (folder names, sizes, dates, notable file names) and a small summary;
  - `quarantine\` (and a hidden `X:\.ferahla\quarantine` on other drives): moved items and their manifests;
  - `notes\notes.json`: notes on findings, yours and Claude's;
  - `claude-work\`, `claude-usage.json`: only if you use the Claude analysis (see below);
  - `ui.json`: the address and key of the running window, while it runs;
  - `update.json`: whether to check for updates.
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

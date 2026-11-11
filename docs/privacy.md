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
  - `claude-work\`, `codex-work\`, `claude-usage.json`: only if you use the AI analysis (see below);
  - `ui.json`: the address and key of the running window, while it runs;
  - `update.json`: whether to check for updates.
  Delete the folder to remove everything. Uninstalling is deleting `ferahla.exe`.
- **The HTML report** contains your file paths. It goes where you save it and nowhere else; look before you share it.

## AI analysis: Claude Code or Codex (optional)

Only if you start it. Ferahla then runs **your own** Claude Code or Codex installation, logged in to **your own**
account, and the analysis is subject to that provider's terms and privacy policy for that account (Anthropic for
Claude, OpenAI for Codex).

- The AI sees what Ferahla's tools return about the scan you chose: folder and file names, paths, sizes and dates,
  and Ferahla's findings. Nothing else on your PC; it has no file, shell or web tools.
- Ferahla never reads, stores or sends your credentials. From `claude auth status` it reads only whether you are logged
  in, the login method and the plan type; from `codex login status` only whether you are logged in and whether that is
  with ChatGPT or an API key. Your e-mail and organization are ignored.
- API keys in the environment (`ANTHROPIC_API_KEY`, `CODEX_API_KEY`, `OPENAI_API_KEY`) are removed for Ferahla's runs,
  so your plan is used, not a key.
- Claude Code and Codex keep their own session history (under `%USERPROFILE%\.claude` and `%USERPROFILE%\.codex`);
  it contains the scan text the AI read. Ferahla's own copy of a chat lives in memory only.
- Claude: your plan usage (5-hour and weekly percentages) is shown from the events Claude Code reports, and the last
  value is kept in `claude-usage.json` so it can be shown before the next run. Codex reports no plan usage; Ferahla
  shows the tokens of each run.
- Ferahla's work folders: `claude-work\` and `codex-work\`, with the prompt and MCP settings of the last run.

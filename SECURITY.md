# Security policy

## Reporting a vulnerability

Please report privately through GitHub: **Security › Report a vulnerability** on this repository. Don't open a public
issue for anything that could let someone else read, move or delete a user's files, or reach Ferahla's local server.
You'll get an answer within a week; fixes are released as soon as they are ready, and you are credited unless you
prefer not to be.

Only the latest release is supported.

## What Ferahla protects, and how

**Your files.** Ferahla never deletes what it suggests. Selected items are renamed into a quarantine folder on the same
drive and can be put back. Before anything moves:

- a plan only *selects*: the findings of that scan are computed again and an item moves only if Ferahla still suggests
  exactly that item (`PlanValidator`);
- a guard independent of the rules refuses drive roots, Windows, Program Files, the profile and system folders
  themselves, Ferahla's own data, network and device paths, and non-canonical paths (`MoveGuard`);
- moves use `MoveFileExW` without copy fallback and are written to a manifest first, so a crash leaves nothing lost;
- quarantine on other drives lives in a hidden `X:\.ferahla` folder whose ACL allows only you, SYSTEM and
  Administrators.

Permanent delete exists only in the UI, behind an explicit confirmation, for items already in quarantine, and never
follows a junction or symbolic link out of the quarantine.

**The local server.** `ferahla` serves its window on `127.0.0.1` only, on a random port. A website open in your
browser can still send requests to localhost, and binding to loopback doesn't stop DNS rebinding, so every request is
checked (`src/Ferahla.Server/Security.cs`, pinned by `ServerTests`):

- the `Host` header must be `127.0.0.1:<port>` or `localhost:<port>`;
- an `Origin` header, if present, must be this server; `Sec-Fetch-Site` must be same-origin or none;
- every API call carries a random per-launch token in a header; the token reaches the browser in the URL fragment,
  which is never sent over the network;
- only GET and POST; GET never changes anything; POST bodies must be `application/json`;
- a strict Content-Security-Policy without `unsafe-inline`, `X-Frame-Options: DENY`, `nosniff`, no referrer.

**System commands** come from a fixed allowlist with fixed arguments (`SystemActions.cs`); each asks for elevation for
that one step. `DISM /ResetBase` is never run. Ferahla itself never runs elevated.

**Claude analysis** (optional) runs your own Claude Code with no built-in tools, only Ferahla's read-only MCP tools plus
a note tool, no settings, hooks or CLAUDE.md files, and every permission prompt refused. Ferahla checks the tool list
when the run starts and stops it if anything else is enabled. It never reads Claude's credentials. Scan data given to
Claude is treated as data in the prompt, not as instructions.

## Out of scope

- Someone who already runs code as your Windows user can do anything you can; Ferahla doesn't defend against that.
- Unsigned development builds. Use the release binaries from GitHub (see [docs/code-signing.md](docs/code-signing.md)).

# M0 spike results

Each spike's code is under `spikes/<no>-<name>/`. Spike code is never moved into the product; only the results and patterns are.

Environment: Windows 11, .NET SDK 10 (pinned in `global.json`), Visual Studio 2022 C++ tools, Claude Code 2.1.x.

| # | Topic | Result |
|---|---|---|
| 1 | NativeAOT + embedded static files + SSE | ✅ Works |
| 11 | C# MCP SDK + NativeAOT | ✅ Works |
| 2 | `claude -p` stream-json | ✅ Event schema documented, fixtures in `tests/fixtures/claude/` |
| 3 | Tool isolation | ✅ Only Ferahla's tools; `--safe-mode` can't be used |
| 4 | `--mcp-config` with an exe | ✅ Works |
| 12 | Child process environment and stopping | 🟡 Partly: no orphan processes; the API key conflict is to be tested on a clean VM |
| 9 | Fluent v3 offline + dark theme | ✅ Works, but not used: the UI uses its own components (see below) |
| 5 | Restart Manager and folders | 🟡 Files ✅; folder handles (cwd) are not visible |
| 8 | Writing to a drive root | ✅ Works; the ACL must be narrowed |
| 6 | Reading RAR | ⏭ Decided: not in v1 |
| 10 | Launcher uninstall methods | ✅ Decided (below) |
| 7 | Shrinking the WSL vhdx | ⏭ Decided: information only in v1 |

## Decisions taken without a spike (M4)

- **6. RAR:** no RAR reader in v1. SharpCompress (MIT) would add a large dependency whose NativeAOT behaviour isn't
  proven, for one evidence line. RAR archives next to an extracted folder are still found (by name), at risk *review*
  with "contents not verified"; zip archives are verified file by file (`ferahla verify`, CRC-32).
- **7. WSL vhdx:** information only. There is no documented way to shrink a dynamic VHDX without Hyper-V's
  `Optimize-VHD` (not on Home editions, and not effective on sparse disks). The finding explains where the space is.
- **10. Launchers:** Steam games are removed with `steam://uninstall/<appid>` (app id from `steamapps/appmanifest_*.acf`
  in every library of `libraryfolders.vdf`). Riot and EA games register a normal uninstall entry, so they go through
  the registry `UninstallString` like any app. Epic games have no per-game uninstall entry; the UI points to the Epic
  launcher and Windows Settings › Apps. Ferahla never deletes a game folder itself.
- **9 (follow-up). UI components:** the UI uses its own small component set (one stylesheet, design tokens, light and
  dark) in the visual language of the HTML report, instead of Fluent Web Components: shadow DOM made consistent
  styling hard, and the plain components keep the page small with a strict CSP.

---

## 1. NativeAOT + embedded static files + SSE — ✅

**Code:** `spikes/01-aot-static-sse/`

**Setup:**
- `Microsoft.NET.Sdk.Web`, `PublishAot=true`, `CreateSlimBuilder`
- `Microsoft.Extensions.FileProviders.Embedded` 10.0.14 + `GenerateEmbeddedFilesManifest=true`, `<EmbeddedResource Include="wwwroot\**\*" />`, `EnableDefaultContentItems=false`
- `ManifestEmbeddedFileProvider(assembly, "wwwroot")` → `UseDefaultFiles` + `UseStaticFiles`
- `ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0))`; the real port is read from `IServerAddressesFeature`

**Test:** the exe was copied alone into an empty folder and run (no other files next to it).

| Measurement | Value |
|---|---|
| AOT warnings | 0 |
| exe size | 14.1 MB (pdb separate, not shipped) |
| Startup (process → listening) | ~80 ms |
| Memory (idle) | ~23 MB |

- `/`, `.js`, `.css`, `.json`, `.svg`, subfolders included, return 200 with the right content type; a missing file is 404.
- UTF-8 content with Turkish letters arrives intact.
- `RuntimeFeature.IsDynamicCodeSupported == false` (really AOT).
- **SSE works all three ways**; events arrive one by one, 300 ms apart, without buffering:
  - `TypedResults.ServerSentEvents(IAsyncEnumerable<T>, eventType)`: built into .NET 10; JSON is serialized with the source-generated context added to `ConfigureHttpJsonOptions`
  - `TypedResults.ServerSentEvents(IAsyncEnumerable<SseItem<T>>)`: event type and `id` per event
  - Hand-written `text/event-stream` + `FlushAsync`
  - The built-in result adds `Cache-Control: no-store, no-cache`.

**Decisions:**
- SSE uses the built-in `TypedResults.ServerSentEvents` + `SseItem<T>` (event types `progress`, `finding`, `done`…). No need to write it by hand.
- Static files are served with `ManifestEmbeddedFileProvider`. `MapStaticAssets` relies on a manifest on disk, so it isn't used in a single exe.
- Static files get no `charset=utf-8` in their content type (only the JSON APIs do). It causes no problem, but product code adds `charset=utf-8` to text types with `StaticFileOptions.OnPrepareResponse`.
- A plain `JsonSerializer.Serialize` with the default encoder escapes Turkish letters as `\u011F` (valid JSON, hard to read). Product code writes all JSON through one `JsonSerializerOptions`.

**Environment finding: the AOT link step fails when `vswhere.exe` isn't on PATH.**
```
error MSB3073: 'vswhere.exe' is not recognized as an internal or external command
```
Adding `%ProgramFiles(x86)%\Microsoft Visual Studio\Installer` to PATH fixes it. The README's build section says so. On GitHub Actions `windows-latest` the CI adds it explicitly.

---

## 11. Official C# MCP SDK + NativeAOT — ✅

**Code:** `spikes/11-mcp-aot/`

- Packages: `ModelContextProtocol` 2.3.1 + `Microsoft.Extensions.Hosting` 10.0.14
- `AddMcpServer().WithStdioServerTransport().WithTools<FerahlaTools>(jsonOptions)`; tools use `[McpServerToolType]` / `[McpServerTool(Name=…, ReadOnly=true)]` + `[Description]`
- Return types are records, serialized with a `JsonSerializerOptions` built from a source-generated `JsonSerializerContext`
- **AOT warnings: 0.** exe 12.7 MB.

**Raw JSON-RPC test** (stdin/stdout): `initialize` → `tools/list` → 3× `tools/call` → an unknown tool. All answered correctly (first reply ~65 ms). The unknown tool gets `-32602 Unknown tool`. `inputSchema` is generated correctly from the parameters and `[Description]`s (nullable → `["string","null"]`, defaults included). `ReadOnly=true` → `annotations.readOnlyHint`. When stdin closes, the process exits by itself with code 0.

**Decisions:**
- Use the official SDK; no hand-written JSON-RPC.
- Logs go to **stderr**: `AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace)`. stdout belongs to the protocol.
- Tool results are serialized with `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`. With the default encoder, Turkish letters were escaped twice (`klas\\u00F6r\\u00FC`), which wastes Claude's tokens. Output goes to JSON-RPC, not HTML, so the relaxed encoder is safe.
- All tools read or add notes; every tool except the note tool gets `ReadOnly=true`.

---

## 2–4. `claude -p` stream-json, tool isolation, `--mcp-config` — ✅

**Command** (everything accepted, exit 0):
```
claude -p --output-format stream-json --verbose --include-partial-messages
  --append-system-prompt-file <analyze.en.md>
  --mcp-config <ferahla-mcp.json> --strict-mcp-config
  --tools "" --allowedTools "mcp__ferahla__*" --permission-prompts none
  --setting-sources "" --session-id <uuid> --max-turns <n>
```
The user message was written to stdin as UTF-8; Turkish letters survived. `ferahla-mcp.json`: `{"mcpServers":{"ferahla":{"type":"stdio","command":"C:\\…\\ferahla.exe","args":["mcp"]}}}`. A Windows exe path works directly (4 ✅).

**Verified:**

| Question | Result |
|---|---|
| Is `--verbose` required? | **Yes.** Without it, exit 1: `When using --print, --output-format=stream-json requires --verbose` |
| Does `--max-turns` exist? | **Yes**; not in `--help`, but it works. When exceeded: `result.subtype = "error_max_turns"`, `is_error: true`, `terminal_reason: "max_turns"`, `errors: ["Reached maximum number of turns (1)"]`, exit 1 |
| `--append-system-prompt-file` | Works |
| Prompt from stdin | Works |
| Does `--tools ""` also turn off MCP tools? | **No.** `tools` in init = only `mcp__ferahla__add_note`, `get_summary`, `list_findings` |
| Does `--setting-sources ""` keep CLAUDE.md out? | **Yes.** A canary CLAUDE.md was put in the working folder: without the flag the canary showed up in the output, with it it didn't. (A user-level `~/.claude/CLAUDE.md` is to be tested separately on a clean VM.) |
| `--safe-mode` | **Can't be used:** it also turns off the server given with `--mcp-config` (`tools=[]`, `mcp_servers` empty in init) |
| `--resume <id>` | Continues with the same `session_id` and history. **MCP is not restored** (`tools=[]`); `--mcp-config` must be given again |
| When `claude.exe` is killed | The MCP child process (exe) exits within 2 s because its stdin closes; no orphan |

**Still listed (inert):** init lists skills, agents, plugins, slash commands and `memory_paths.auto`. But with no `Skill`, `Agent` or file tools, Claude can't use them. The user's hooks aren't loaded with `--setting-sources ""` (hooks live in settings files). Low risk; in M3 the init event is checked: if `tools` contains anything besides `mcp__ferahla__*`, the analysis doesn't start.

**Event schema** (one JSON object per line; `type` is the discriminator):

| `type` / subtype | When | Fields Ferahla uses |
|---|---|---|
| `system` / `init` | First event (~0.5–0.8 s) | `session_id`, `tools[]`, `mcp_servers[].status` (`connected`), `model`, `apiKeySource` (`none` = subscription), `claude_code_version` |
| `system` / `status` | Before each API request | `status: "requesting"` → "thinking" in the UI |
| `stream_event` | With `--include-partial-messages`; raw Anthropic stream events | `event.type`: `message_start`, `content_block_start` (`content_block.type`: `text` / `tool_use` / `thinking`, `name`), `content_block_delta` (`delta.type`: `text_delta` → streaming text, `input_json_delta`), `content_block_stop`, `message_delta`, `message_stop`. `parent_tool_use_id` |
| `assistant` | The full message, once each content block ends | `message.content[]`: `tool_use` (`id`, `name`, `input`), `text` |
| `user` | Tool result | `message.content[].tool_result` (`tool_use_id`, `content[]`, `is_error`), `tool_result_meta[].permission_decision` |
| `rate_limit_event` | Now and then | `rate_limit_info.status` (`allowed` / `allowed_warning` / …), `unifiedWindows.five_hour` / `seven_day` → `utilization` (0–1), `resetsAt` (unix seconds) |
| `result` | Last event | `subtype` (`success` / `error_max_turns` / …), `is_error`, `result` (final text), `num_turns`, `duration_ms`, `total_cost_usd`, `usage`, `permission_denials[]`, `terminal_reason`, `errors[]` |

Measured (Opus, 3 tool calls + a summary): 10.1 s, 135 events, first event at 0.85 s. `total_cost_usd` is filled in on a subscription too; it is shown for information only.

**Fixtures** (`tests/fixtures/claude/`): made against the spike MCP server's invented data with the `haiku` model, in English, and cleaned with `tools/sanitize-fixture.mjs`. Paths became `C:\Users\user\…`. Request, message, tool and session ids were replaced with consistent fakes (`req_fixture0001`, `00000000-0000-4000-8000-…`). Timestamps became `2030-01-01`. `rate_limit_info` was replaced with a fixed sample; thinking signatures with a placeholder; skill, plugin and agent lists with neutral values. New fixtures go through the same script.
- `stream-success.jsonl`: 3 tool calls + a note + a summary, with `--include-partial-messages`
- `stream-error-max-turns.jsonl`: `--max-turns 1` exceeded
- `stream-resume-no-mcp.jsonl`: resume without `--mcp-config`

**`claude auth status`:** exit 0 and JSON when logged in. Fields to use: `loggedIn`, `authMethod` (`claude.ai` / …), `subscriptionType`. The output **also contains the email, org name and org id**; Ferahla never reads, logs or stores them (the parser takes only the three fields). Exit code and output when logged out: to be checked on a clean VM.

**Product decisions:**
- `--safe-mode` is dropped from the command; isolation comes from `--tools "" --strict-mcp-config --setting-sources ""`.
- The parser looks at `type`, then the subtype; **unknown events are ignored** (new fields arrive with Claude Code versions).
- Progress: count `tool_use` blocks in `assistant` events named `mcp__ferahla__add_note`.
- `rate_limit_event` is shown in the UI: on `allowed_warning`, "you're at X% of your weekly/5-hour limit". The last known value is also shown before an analysis starts.
- Turn limit via `--max-turns`; `error_max_turns` is shown as "analysis stopped early, X findings got notes".

---

## 12. Child process environment and stopping — 🟡

- With no `ANTHROPIC_API_KEY` in the environment, `apiKeySource: "none"` comes back. That removing a defined key from the child's environment makes it use the subscription is **to be verified on a clean VM**.
- Killing `claude.exe` leaves no orphan MCP process (above). Still, if Ferahla itself crashes, `claude.exe` could stay open. So the child goes into a **Job Object** (`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`): when Ferahla exits, the whole tree exits. "Stop" = `TerminateJobObject`.
- Windows PowerShell 5.1 note: piping non-ASCII text into a native exe is mangled by `$OutputEncoding`. Ferahla writes UTF-8 bytes to stdin directly; this only affects test scripts.

---

## 9. Fluent UI Web Components v3 offline + dark theme — ✅

**Code:** `spikes/09-fluent-offline/`

- `@fluentui/web-components` **3.1.4** (latest, MIT). `dist/web-components.min.js` (269 KB) is a **self-contained classic script**: no imports, `@microsoft/fast-element` bundled in; on load it defines every `fluent-*` component and exposes `globalThis.Fluent.setTheme`. Works with `<script src>`, no build step.
- The bundle makes no network requests: no `fetch`, `XMLHttpRequest` or dynamic `import(`; the only URL is the SVG namespace.
- **Theme:** `webLightTheme` / `webDarkTheme` aren't in the bundle; they're in `@fluentui/tokens` (still `1.0.0-alpha.24`). `setTheme` only writes `--token` CSS variables, so `gen-theme.mjs` turns both themes into **static CSS once** (459 tokens):
  - `:root` → light
  - `@media (prefers-color-scheme: dark) { :root:not([data-theme="light"]) }` → dark when the system is dark
  - `:root[data-theme="dark"]` → manual choice
  - Result: the alpha tokens package is **never used at run time**; only during development, to generate the CSS.
- **Test:** headless Edge, `file://`, light / dark / system-dark (`--force-dark-mode`). 11 of 11 components defined, with shadow DOM (button, badge, checkbox, switch, spinner, progress-bar, text-input, tablist/tab, message-bar, text). Background `rgb(250,250,250)` in light, `rgb(31,31,31)` in dark. Turkish letters render correctly.
- Fluent's `warning` color is orange-ish (peach in light, brown-orange in dark) and can read as red. Our own risk chip colors may be clearer.

**Decisions:**
- `web/vendor/fluent/web-components.min.js` is committed as is (pinned version, with `NOTICE.md`).
- `web/vendor/fluent/fluent-theme.css` is generated with `tools/gen-theme.mjs` and committed; regenerated when Fluent is updated.
- There is no table component; file lists use our own table (with the tokens).

---

## 8. Writing to a drive root (quarantine location) — ✅

As a non-admin user (`IsInRole(Administrator) = false`), creating a folder at the root of fixed NTFS drives (system drive included) and moving (renaming) inside it **works**. The default root ACL lets `Authenticated Users` create folders.

**Privacy problem:** a folder created at the root inherits `Authenticated Users: Modify`, so **other users on the machine can read and change the quarantine**.

**Fix (no admin needed, tested):** right after creating the folder
- break inheritance (`SetAccessRuleProtection(true, false)`)
- grant FullControl (container + object inherit) only to the current user, `SYSTEM` and `Administrators`
- make the folder `Hidden`

Writing and moving still work after the fix.

**Notes:**
- A move within one NTFS volume keeps the file's own ACL. A file other users could already reach (e.g. `D:\Shared\x`) stays reachable in quarantine. That's not a new exposure; the quarantine folder's ACL protects listing and new files.
- FAT32/exFAT (USB) and network drives have no ACLs, or moves aren't instant. In v1, quarantine is only available on **fixed NTFS/ReFS** volumes; elsewhere it says "no quarantine on this drive, information only".
- If creating the root folder fails (corporate ACLs), quarantine is turned off for that drive and the reason is shown.

---

## 5. Restart Manager and folders — 🟡

**Code:** `spikes/05-restart-manager/` (`LibraryImport`, NativeAOT, no warnings)

Setup: a folder with 5001 files. One PowerShell process holds a file inside it with `FileShare.None`; another PowerShell process has its working directory (cwd) inside the folder.

| Test | Result |
|---|---|
| A) The locked file itself | ✅ 122 ms, right PID + "Windows PowerShell", type 5 (console) |
| B) The folder path as the resource | ❌ `RmRegisterResources` → error 5 (access denied). **RM doesn't accept folders** |
| C) Every file in the folder | ✅ 5001 files in 907 ms, the locking process found |
| The process holding the cwd | ❌ **Not visible in any test.** RM only sees file handles |
| D) Move the folder (both obstacles) | `0x80070020` sharing violation |
| E) Move the locked file | `0x80070020` |
| F) Move the folder after the cwd holder exits, file still locked | `0x80070005` access denied |

**Decisions:**
- Flow: **try the move first**, run RM only when it fails. Successful moves cost nothing.
- When a folder move fails, the folder's files are given to RM (~1 s per 5000 files; very large folders are limited to the first N thousand files, and that is said).
- When RM finds no process: "the folder is open in another program; probably a terminal or an Explorer window". Detecting cwd needs a handle scan via `NtQuerySystemInformation`; not in v1.
- Error codes: `0x20` → "in use", `0x05` → "a file inside is in use, or access denied". Either way the item is skipped and reported, and the operation goes on.
- **P/Invoke trap:** `RM_UNIQUE_PROCESS.ProcessStartTime` is a `FILETIME` (two `DWORD`s, 4-byte aligned). Writing it as a C# `long` shifted the struct by 4 bytes; app names came back missing their first two characters. Two `uint`s fixed it. `Ferahla.Windows` gets a unit test for struct sizes (`sizeof(RM_PROCESS_INFO) == 668`).

## 13. Codex CLI (`codex exec --json`) over the same MCP server — ✅

Verified with codex-cli 0.160 (ChatGPT login), against invented sample data, cheapest listed model, a handful of runs.

**Command** (message on stdin):
```
codex exec [resume <thread-id>] --json --ignore-user-config --ignore-rules --skip-git-repo-check
  --sandbox read-only            (exec only; resume takes -c sandbox_mode='read-only')
  -c approval_policy='never' -c web_search='disabled' -c project_doc_max_bytes=0
  -c skills.include_instructions=false -c skills.bundled.enabled=false -c features.memories=false
  --disable shell_tool --disable unified_exec --disable apps --disable plugins --disable browser_use … (only names
            that `codex features list` reports; an unknown name is not passed)
  -c mcp_servers.ferahla.command='<ferahla.exe>' -c mcp_servers.ferahla.args=['mcp','--scan','<id>','--home','<home>','--agent','codex']
  -c mcp_servers.ferahla.required=true -c mcp_servers.ferahla.default_tools_approval_mode='approve'
  -c developer_instructions="<prompt>" [-m <model>] -
```
- `codex debug prompt-input` (no tokens spent) shows what the model gets: with the flags above, no skills block, no
  AGENTS.md, no recommended plugins; the developer instructions are ours. Asked to run `dir C:\`, the model said it
  only has Ferahla tools.
- MCP calls run without an approval prompt with `default_tools_approval_mode='approve'` and `approval_policy='never'`.
- **`code_mode_host` must stay on.** Disabling it removes every MCP tool ("Code Mode is unavailable … fails closed").
- Events (JSONL): `thread.started {thread_id}`, `turn.started`, `item.started` / `item.completed` with `item.type`
  `agent_message {text}`, `reasoning`, `mcp_tool_call {server, tool, arguments, result{content[]}, error, status}`,
  `error` (non-fatal warnings, e.g. unknown model metadata), `turn.completed {usage{input_tokens, output_tokens, …}}`,
  `turn.failed {error{message}}` and top-level `error {message}`; API errors carry JSON inside `message`.
- No tool list up front (unlike Claude's init event): `CodexParser.Check` stops the run at the first
  `command_execution`, `file_change`, `web_search` or non-Ferahla tool call. No `--max-turns`: the runner counts tool calls.
- `codex exec resume <thread-id>` keeps the conversation (a follow-up recalled the earlier verdict); `-c` overrides,
  including the MCP server, are given again on every run.
- `codex login status` prints to **stderr** ("Logged in using ChatGPT" / "… an API key" / "Not logged in").
  `codex debug models` lists the picker's models (`visibility: "list"`).
- npm installs `codex.cmd` (a node shim); the native `codex.exe` sits under
  `node_modules\@openai\codex\node_modules\@openai\codex-win32-x64\vendor\x86_64-pc-windows-msvc\bin\`. Ferahla runs
  that exe directly, so arguments never pass through cmd.exe.
- No plan usage is reported to other programs; only tokens per turn.
- The interactive `codex` has no `--ignore-user-config`, so there is no "Continue in terminal" for Codex.

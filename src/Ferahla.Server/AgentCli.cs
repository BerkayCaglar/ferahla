using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Ferahla.Core.Claude;
using Ferahla.Core.Text;
using Ferahla.Windows;

namespace Ferahla.Server;

/// <summary>What the UI knows about one agent (Claude Code or Codex) on this PC.</summary>
public sealed class AgentInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Installed { get; set; }
    public string? Version { get; set; }
    public bool LoggedIn { get; set; }
    /// <summary>How it is logged in (Claude: method and plan type; Codex: ChatGPT or API key). Never e-mail or org.</summary>
    public string? AuthMethod { get; set; }
    public string? SubscriptionType { get; set; }
    /// <summary>An API key in the environment would bill the API instead of the plan; Ferahla removes it for its runs.</summary>
    public bool ApiKeyInEnvironment { get; set; }
    /// <summary>Logged in with an API key (Codex): runs are billed to that key, not to a ChatGPT plan.</summary>
    public bool UsesApiKey { get; set; }
    public List<ModelOption> Models { get; set; } = [];
    /// <summary>Claude only: plan usage windows arrive in the stream.</summary>
    public bool ReportsPlanUsage { get; set; }
    /// <summary>Claude only: "Continue in terminal" keeps the isolation (Codex' interactive mode can't ignore user config).</summary>
    public bool CanContinueInTerminal { get; set; }
    public string? Error { get; set; }
    /// <summary>Codex feature flags this version knows (only those are passed to --disable).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public HashSet<string> Features { get; set; } = [];
}

public sealed class ModelOption
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>"lightest" etc.; the UI translates it.</summary>
    public string? Hint { get; set; }
}

/// <summary>One run of an agent: what to ask, about which scan, in which session.</summary>
internal sealed class AgentRun
{
    public required string WorkDir { get; init; }
    /// <summary>The embedded prompt (analyze or chat).</summary>
    public required string PromptName { get; init; }
    public required string Home { get; init; }
    public required string ScanId { get; init; }
    /// <summary>Claude: the id to create or resume. Codex: the thread to resume, or null for a new one.</summary>
    public string? SessionId { get; init; }
    public bool Resume { get; init; }
    public int MaxTurns { get; init; }
    public string? Model { get; init; }
    public required string Message { get; init; }
}

/// <summary>
/// An agent CLI run isolated the same way for Claude Code and Codex: only Ferahla's read-only MCP tools (plus notes), no
/// built-in tools, no user settings, rules, skills or hooks, nothing that can run a command, write a file or go online;
/// the process tree in a job object; API keys removed from the environment so the user's plan is used; and a stop as
/// soon as the output shows anything else (<see cref="Check"/>).
/// </summary>
[SupportedOSPlatform("windows")]
internal abstract class AgentCli
{
    public static readonly AgentCli Claude = new ClaudeCli();
    public static readonly AgentCli Codex = new CodexCli();

    public static AgentCli Get(string? id) => Agents.Normalize(id) == Agents.Codex ? Codex : Claude;

    public abstract string Id { get; }
    public string Name => Agents.Name(Id);
    public abstract string? Find();
    public abstract Task<AgentInfo> ProbeAsync();
    /// <summary>Folder the agent runs in (neutral, no project files).</summary>
    public string WorkDir(string home) => Path.Combine(home, Id + "-work");
    /// <summary>Claude takes a session id up front; Codex names its thread in the first event.</summary>
    public abstract bool PresetSessionId { get; }
    /// <summary>The agent doesn't stop at a turn limit by itself, so the runner counts tool calls.</summary>
    protected virtual bool CountTurns => false;
    protected abstract string[] ApiKeyVariables { get; }

    protected abstract (List<string> Args, string Stdin, IAgentParser Parser) Prepare(AgentRun run, string exe);

    /// <summary>Null when the event is fine; otherwise why the run must stop.</summary>
    protected abstract string? Check(AnalysisEvent ev);

    public static bool ValidModel(string? model) =>
        model is { Length: > 0 and <= 64 } && model.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '[' or ']');

    /// <summary>Runs the agent to the end, handing every parsed event to <paramref name="onEvent"/>.</summary>
    /// <param name="track">Gets the job object while the run lives (to stop it), then null.</param>
    public async Task Run(AgentRun run, Action<AnalysisEvent> onEvent, Action<JobObject?> track, CancellationToken ct)
    {
        var exe = Find() ?? throw new InvalidOperationException(Lang.T($"{Name} is not installed", $"{Name} kurulu değil"));
        Directory.CreateDirectory(run.WorkDir);
        var (args, stdinText, parser) = Prepare(run, exe);
        var psi = Start(exe, args, run.WorkDir);
        foreach (var k in ApiKeyVariables) psi.Environment.Remove(k);

        using var job = new JobObject();
        using var p = Process.Start(psi) ?? throw new InvalidOperationException(Id + " did not start");
        job.Assign(p);
        track(job);
        try
        {
            var stdin = p.StandardInput.BaseStream;
            await stdin.WriteAsync(Encoding.UTF8.GetBytes(stdinText), ct);
            stdin.Close();
            var stderr = p.StandardError.ReadToEndAsync(ct);
            using var reg = ct.Register(job.Terminate);

            string? line;
            var any = false;
            var tools = 0;
            var stopped = false;
            while (!stopped && (line = await p.StandardOutput.ReadLineAsync(ct)) is not null)
            {
                foreach (var ev in parser.Feed(line))
                {
                    any = true;
                    if (Check(ev) is { } problem)
                    {
                        job.Terminate();
                        onEvent(new AnalysisEvent { Kind = AnalysisEventKind.Error, Text = problem });
                        throw new InvalidOperationException(Lang.T("Analysis stopped for safety: ", "Analiz güvenlik için durduruldu: ") + problem);
                    }
                    onEvent(ev);
                    if (CountTurns && ev.Kind == AnalysisEventKind.Tool && ++tools >= Math.Clamp(run.MaxTurns, 3, 150))
                    {
                        // Notes written so far are saved by the MCP server already; stop like Claude does at --max-turns.
                        job.Terminate();
                        onEvent(new AnalysisEvent { Kind = AnalysisEventKind.Result, Subtype = "error_max_turns", IsError = true, Turns = tools });
                        stopped = true;
                        break;
                    }
                }
            }
            if (stopped) return;
            await p.WaitForExitAsync(ct);
            if (!any)
            {
                var err = (await stderr).Trim();
                throw new InvalidOperationException(err.Length > 0 ? err[..Math.Min(err.Length, 400)] : $"{Id} exited with code {p.ExitCode}");
            }
        }
        finally { track(null); }
    }

    // ---- shared helpers ----

    internal static string Prompt(string name, string lang)
    {
        var res = $"prompts/{name}.{(lang == "tr" ? "tr" : "en")}.md";
        using var s = typeof(AgentCli).Assembly.GetManifestResourceStream(res) ?? throw new InvalidOperationException("prompt missing: " + res);
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    /// <summary><c>ferahla mcp</c> on one scan, writing notes as this agent.</summary>
    protected List<string> McpArgs(AgentRun run) => ["mcp", "--scan", run.ScanId, "--home", run.Home, "--agent", Id];

    protected static string? FindOnPath(params string[] names)
    {
        foreach (var name in names)
            foreach (var d in SearchDirs())
            {
                try
                {
                    var p = Path.Combine(d, name);
                    if (File.Exists(p)) return p;
                }
                catch (ArgumentException) { }
            }
        return null;
    }

    protected static IEnumerable<string> SearchDirs()
    {
        foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            yield return d.Trim().Trim('"');
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(profile, ".local", "bin");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");
    }

    /// <summary>Runs a short command (version, login status) and returns its exit code with stdout and stderr together.</summary>
    protected static async Task<(int Code, string Output)> RunShort(string exe, string[] args)
    {
        try
        {
            using var p = Process.Start(Start(exe, args, Path.GetTempPath()))!;
            p.StandardInput.Close();
            var read = p.StandardOutput.ReadToEndAsync();
            var err = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await p.WaitForExitAsync(cts.Token);
            return (p.ExitCode, (await read) + "\n" + (await err));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException or InvalidOperationException) { return (-1, ""); }
    }

    /// <summary>ProcessStartInfo for an exe, or a .cmd shim through cmd.exe (npm installs).</summary>
    internal static ProcessStartInfo Start(string exe, IEnumerable<string> args, string workDir)
    {
        ProcessStartInfo psi;
        if (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"));
            psi.Arguments = "/d /s /c \"" + Quote(exe) + " " + string.Join(' ', args.Select(Quote)) + "\"";
        }
        else
        {
            psi = new ProcessStartInfo(exe);
            foreach (var a in args) psi.ArgumentList.Add(a);
        }
        psi.WorkingDirectory = workDir;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = new UTF8Encoding(false);
        psi.StandardErrorEncoding = new UTF8Encoding(false);
        return psi;
    }

    internal static string Quote(string a) => a.Length == 0 ? "\"\"" : a.IndexOfAny([' ', '"', '\t', '&', '|', '<', '>', '^']) < 0 ? a : "\"" + a.Replace("\"", "\\\"") + "\"";
}

/// <summary>
/// <c>claude -p</c> with <c>--tools ""</c>, only <c>mcp__ferahla__*</c> allowed, <c>--strict-mcp-config</c>, no setting
/// sources, permission prompts refused; stops if the init event lists any other tool (docs/spikes.md 2-4).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ClaudeCli : AgentCli
{
    public override string Id => Agents.Claude;
    public override bool PresetSessionId => true;
    protected override string[] ApiKeyVariables => ["ANTHROPIC_API_KEY"];

    public static readonly ModelOption[] KnownModels =
    [
        new() { Id = "haiku", Label = "Haiku", Hint = "lightest" },
        new() { Id = "sonnet", Label = "Sonnet" },
        new() { Id = "opus", Label = "Opus" },
    ];

    public override string? Find() => FindOnPath("claude.exe", "claude.cmd");

    public override async Task<AgentInfo> ProbeAsync()
    {
        var info = new AgentInfo
        {
            Id = Id, Name = "Claude Code", Models = [.. KnownModels], ReportsPlanUsage = true, CanContinueInTerminal = true,
            ApiKeyInEnvironment = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")),
        };
        var claude = Find();
        if (claude is null) return info;
        info.Installed = true;
        var (code, output) = await RunShort(claude, ["--version"]);
        if (code == 0) info.Version = output.Trim().Split(' ')[0];
        (code, output) = await RunShort(claude, ["auth", "status"]);
        try
        {
            var json = output.Trim();
            var end = json.LastIndexOf('}');
            using var doc = JsonDocument.Parse(end >= 0 ? json[..(end + 1)] : json);
            var r = doc.RootElement;
            info.LoggedIn = code == 0 && r.TryGetProperty("loggedIn", out var li) && li.ValueKind == JsonValueKind.True;
            if (r.TryGetProperty("authMethod", out var am) && am.ValueKind == JsonValueKind.String) info.AuthMethod = am.GetString();
            if (r.TryGetProperty("subscriptionType", out var st) && st.ValueKind == JsonValueKind.String) info.SubscriptionType = st.GetString();
        }
        catch (JsonException) { info.LoggedIn = false; }
        return info;
    }

    /// <param name="resume">New session: <c>--session-id</c>; existing one: <c>--resume</c>.</param>
    public static List<string> Args(string promptFile, string mcpConfig, string sessionId, bool resume, int maxTurns, string? model)
    {
        var args = new List<string>
        {
            "-p", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
            // The appended system prompt is not part of the saved session, so it is given on every run, like
            // --mcp-config (which --resume doesn't restore either; docs/spikes.md 2-4).
            "--append-system-prompt-file", promptFile,
            "--mcp-config", mcpConfig, "--strict-mcp-config",
            "--tools", "", "--allowedTools", "mcp__ferahla__*", "--permission-prompts", "none",
            "--setting-sources", "", resume ? "--resume" : "--session-id", sessionId,
            "--max-turns", Math.Clamp(maxTurns, 3, 150).ToString(CultureInfo.InvariantCulture),
        };
        if (ValidModel(model)) { args.Add("--model"); args.Add(model!); }
        return args;
    }

    protected override (List<string> Args, string Stdin, IAgentParser Parser) Prepare(AgentRun run, string exe)
    {
        var promptFile = Path.Combine(run.WorkDir, run.PromptName + ".md");
        File.WriteAllText(promptFile, Prompt(run.PromptName, Lang.Current), new UTF8Encoding(false));
        var mcp = WriteMcpConfig(run.WorkDir, run.PromptName == "chat" ? "ferahla-mcp-chat.json" : "ferahla-mcp.json", McpArgs(run));
        return (Args(promptFile, mcp, run.SessionId ?? Guid.NewGuid().ToString(), run.Resume, run.MaxTurns, run.Model), run.Message, new StreamParser());
    }

    protected override string? Check(AnalysisEvent ev) => ev.Kind == AnalysisEventKind.Init ? StreamParser.CheckInit(ev) : null;

    /// <summary>Writes the MCP config that starts this exe as <c>ferahla mcp</c>.</summary>
    public static string WriteMcpConfig(string workDir, string fileName, IEnumerable<string> mcpArgs)
    {
        var exe = Environment.ProcessPath ?? "ferahla.exe";
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject(); w.WriteStartObject("mcpServers"); w.WriteStartObject("ferahla");
            w.WriteString("type", "stdio"); w.WriteString("command", exe);
            w.WriteStartArray("args");
            foreach (var a in mcpArgs) w.WriteStringValue(a);
            w.WriteEndArray();
            // Finding titles in the UI's language, whatever Windows' language is.
            w.WriteStartObject("env"); w.WriteString("FERAHLA_LANG", Lang.Current); w.WriteEndObject();
            w.WriteEndObject(); w.WriteEndObject(); w.WriteEndObject();
        }
        var path = Path.Combine(workDir, fileName);
        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }
}

/// <summary>
/// <c>codex exec --json</c> with the user's config, rules, AGENTS.md and skills ignored, a read-only sandbox, approvals
/// never asked, web search off, every feature that adds a tool disabled (shell, apps, plugins, browser, computer use,
/// images, sub-agents, …), and Ferahla's MCP server as the only tool source. Codex lists no tools up front, so every
/// event is checked instead and anything but a Ferahla tool call stops the run (verified, docs/spikes.md).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class CodexCli : AgentCli
{
    public override string Id => Agents.Codex;
    public override bool PresetSessionId => false;
    protected override bool CountTurns => true;
    protected override string[] ApiKeyVariables => ["CODEX_API_KEY", "OPENAI_API_KEY"];

    /// <summary>
    /// Features that would add a tool or outside context; only those this Codex version knows are passed.
    /// Not <c>code_mode_host</c>: Codex calls MCP tools through it, and without it no tool works at all ("fails closed",
    /// verified with codex-cli 0.160). Every call still shows up as an event and is checked.
    /// </summary>
    internal static readonly string[] DisabledFeatures =
    [
        "shell_tool", "unified_exec", "apps", "plugins", "remote_plugin", "browser_use", "browser_use_external", "in_app_browser",
        "computer_use", "image_generation", "view_image", "multi_agent", "multi_agent_v2", "hooks", "plugin_hooks", "skill_search",
        "skill_mcp_dependency_install", "tool_suggest", "recommended_plugins", "goals", "sleep_tool", "memories",
        "workspace_dependencies", "standalone_web_search", "code_mode", "request_permissions_tool",
    ];

    private HashSet<string> _features = [];

    /// <summary>codex.exe on PATH, or the native exe behind npm's codex.cmd shim (so arguments never pass through cmd.exe).</summary>
    public override string? Find()
    {
        if (FindOnPath("codex.exe") is { } exe) return exe;
        var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? ("arm64", "aarch64-pc-windows-msvc") : ("x64", "x86_64-pc-windows-msvc");
        foreach (var d in SearchDirs())
        {
            try
            {
                if (!File.Exists(Path.Combine(d, "codex.cmd"))) continue;
                var root = Path.Combine(d, "node_modules", "@openai");
                foreach (var candidate in new[]
                {
                    Path.Combine(root, "codex", "node_modules", "@openai", "codex-win32-" + arch.Item1, "vendor", arch.Item2, "bin", "codex.exe"),
                    Path.Combine(root, "codex-win32-" + arch.Item1, "vendor", arch.Item2, "bin", "codex.exe"),
                    Path.Combine(root, "codex", "vendor", arch.Item2, "bin", "codex.exe"),
                })
                    if (File.Exists(candidate)) return candidate;
                return Path.Combine(d, "codex.cmd");
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    public override async Task<AgentInfo> ProbeAsync()
    {
        var info = new AgentInfo
        {
            Id = Id, Name = "Codex",
            ApiKeyInEnvironment = ApiKeyVariables.Any(k => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(k))),
        };
        var codex = Find();
        if (codex is null) return info;
        info.Installed = true;
        var (code, output) = await RunShort(codex, ["--version"]);
        if (code == 0) info.Version = output.Trim().Split((char[])[' ', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault(x => char.IsAsciiDigit(x[0]));
        (code, output) = await RunShort(codex, ["login", "status"]);
        // "Logged in using ChatGPT" / "Logged in using an API key" / "Not logged in" (on stderr).
        var status = output.Trim();
        info.LoggedIn = code == 0 && status.Contains("Logged in", StringComparison.OrdinalIgnoreCase);
        if (info.LoggedIn)
        {
            info.UsesApiKey = status.Contains("API key", StringComparison.OrdinalIgnoreCase);
            info.AuthMethod = info.UsesApiKey ? "API key" : "ChatGPT";
        }
        (code, output) = await RunShort(codex, ["features", "list"]);
        if (code == 0)
            info.Features = output.Split('\n').Select(l => l.Trim().Split(' ', 2)[0]).Where(n => n.Length > 0 && n.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.')).ToHashSet();
        _features = info.Features;
        (code, output) = await RunShort(codex, ["debug", "models"]);
        if (code == 0) info.Models = ParseModels(output);
        return info;
    }

    /// <summary>The models Codex offers in its own picker (<c>codex debug models</c>, visibility "list").</summary>
    internal static List<ModelOption> ParseModels(string json)
    {
        var list = new List<ModelOption>();
        try
        {
            var start = json.IndexOf('{');
            var arrStart = json.IndexOf('[');
            if (start < 0 && arrStart < 0) return list;
            using var doc = JsonDocument.Parse(json[Math.Min(start < 0 ? int.MaxValue : start, arrStart < 0 ? int.MaxValue : arrStart)..]);
            var root = doc.RootElement;
            var arr = root.ValueKind == JsonValueKind.Array ? root : root.TryGetProperty("models", out var m) ? m : default;
            if (arr.ValueKind != JsonValueKind.Array) return list;
            foreach (var x in arr.EnumerateArray())
            {
                var slug = x.TryGetProperty("slug", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                var vis = x.TryGetProperty("visibility", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var name = x.TryGetProperty("display_name", out var dn) && dn.ValueKind == JsonValueKind.String ? dn.GetString() : null;
                if (slug is null || vis != "list" || !ValidModel(slug)) continue;
                list.Add(new ModelOption { Id = slug, Label = name ?? slug });
                if (list.Count >= 12) break;
            }
        }
        catch (JsonException) { }
        return list;
    }

    /// <summary>The <c>codex exec</c> arguments (tested in ClaudeTests). <paramref name="instructions"/> null: they go into the message.</summary>
    public static List<string> Args(string mcpExe, IEnumerable<string> mcpArgs, string lang, string? threadId, bool resume, string? model, string? instructions, IReadOnlySet<string>? known)
    {
        var args = new List<string> { "exec" };
        if (resume && threadId is not null) { args.Add("resume"); args.Add(threadId); }
        args.AddRange(["--json", "--ignore-user-config", "--ignore-rules", "--skip-git-repo-check"]);
        if (!resume) args.AddRange(["--sandbox", "read-only"]);
        void C(string kv) { args.Add("-c"); args.Add(kv); }
        C("sandbox_mode='read-only'");
        C("approval_policy='never'");
        C("web_search='disabled'");
        C("skills.include_instructions=false");
        C("skills.bundled.enabled=false");
        C("project_doc_max_bytes=0");
        C("features.memories=false");
        foreach (var f in DisabledFeatures)
            if (known is null || known.Count == 0 || known.Contains(f)) { args.Add("--disable"); args.Add(f); }
        C("mcp_servers.ferahla.command=" + Toml(mcpExe));
        C("mcp_servers.ferahla.args=[" + string.Join(",", mcpArgs.Select(Toml)) + "]");
        C("mcp_servers.ferahla.env={FERAHLA_LANG=" + Toml(lang) + "}");
        C("mcp_servers.ferahla.required=true");
        C("mcp_servers.ferahla.startup_timeout_sec=30");
        C("mcp_servers.ferahla.tool_timeout_sec=180");
        C("mcp_servers.ferahla.default_tools_approval_mode='approve'");
        if (instructions is not null) C("developer_instructions=" + Toml(instructions));
        if (ValidModel(model)) { args.Add("-m"); args.Add(model!); }
        args.Add("-"); // the message comes on stdin
        return args;
    }

    protected override (List<string> Args, string Stdin, IAgentParser Parser) Prepare(AgentRun run, string exe)
    {
        var prompt = Prompt(run.PromptName, Lang.Current).Replace("`mcp__ferahla__*`", "`ferahla`", StringComparison.Ordinal);
        // Through the npm .cmd shim (no native exe found), long multi-line arguments don't survive cmd.exe:
        // the instructions then lead the first message instead (later turns resume a session that has them).
        var viaCmd = exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
        var message = viaCmd && !run.Resume ? prompt + "\n\n---\n\n" + run.Message : run.Message;
        var known = _features.Count > 0 ? _features : null;
        var args = Args(Environment.ProcessPath ?? "ferahla.exe", McpArgs(run), Lang.Current, run.SessionId, run.Resume && run.SessionId is not null,
            run.Model, viaCmd ? null : prompt, known);
        return (args, message, new CodexParser { Model = ValidModel(run.Model) ? run.Model : null });
    }

    protected override string? Check(AnalysisEvent ev) => CodexParser.Check(ev);

    /// <summary>A TOML string for a <c>-c key=value</c> override: a literal '…' string when possible (no double quotes
    /// that a command line could mangle), else an escaped basic string.</summary>
    internal static string Toml(string s)
    {
        if (!s.Contains('\'') && s.All(c => c >= 0x20 && c != 0x7f)) return "'" + s + "'";
        var b = new StringBuilder("\"");
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': b.Append("\\\""); break;
                case '\\': b.Append("\\\\"); break;
                case '\n': b.Append("\\n"); break;
                case '\r': break;
                case '\t': b.Append("\\t"); break;
                default:
                    if (c < 0x20 || c == 0x7f) b.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    else b.Append(c);
                    break;
            }
        }
        return b.Append('"').ToString();
    }
}

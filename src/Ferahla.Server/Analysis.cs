using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Ferahla.Core.Claude;
using Ferahla.Core.Text;
using Ferahla.Windows;

namespace Ferahla.Server;

public sealed class AnalysisDto
{
    /// <summary>Every supported agent and whether it is installed and logged in here.</summary>
    public List<AgentInfo> Agents { get; set; } = [];
    /// <summary>The agent of the last (or running) analysis.</summary>
    public string? Agent { get; set; }
    public bool Running { get; set; }
    public string? ScanId { get; set; }
    public string? Scope { get; set; }
    public string? SessionId { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? Outcome { get; set; }
    public int Notes { get; set; }
    public int Target { get; set; }
    /// <summary>Claude plan usage (Codex reports tokens per run instead, on its result event).</summary>
    public UsageInfo? Usage { get; set; }
    public List<AnalysisEvent> Events { get; set; } = [];
}

public sealed class AnalysisRequest
{
    public string? Scan { get; set; }
    /// <summary><see cref="Core.Claude.Agents"/>: claude (default) or codex.</summary>
    public string? Agent { get; set; }
    public string? Model { get; set; }
    /// <summary>0: sized to the number of notes to write (<see cref="AnalysisScope.Turns"/>).</summary>
    public int MaxTurns { get; set; }
    /// <summary><see cref="AnalysisScope"/>: all, unnoted or quarantine.</summary>
    public string? Scope { get; set; }
}

/// <summary>
/// Runs the chosen agent (Claude Code or Codex, see <see cref="AgentCli"/>) against Ferahla's MCP server in a neutral
/// folder, isolated: only Ferahla's read-only tools and notes, no settings, rules or hooks from anywhere. The process
/// tree lives in a job object. Ferahla never touches the agent's credentials.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class Analysis(string home, EventHub hub, Jobs jobs)
{
    private readonly Lock _lock = new();
    private readonly List<AnalysisEvent> _events = [];
    private AnalysisDto _last = new();
    private JobObject? _job;
    private readonly Dictionary<string, (AgentInfo Info, DateTimeOffset At)> _info = [];

    private string UsageFile => Path.Combine(home, "claude-usage.json");

    // ---- detection ----

    public async Task<AgentInfo> InfoAsync(string? agent, bool refresh = false)
    {
        var cli = AgentCli.Get(agent);
        lock (_lock)
            if (!refresh && _info.TryGetValue(cli.Id, out var c) && DateTimeOffset.UtcNow - c.At < TimeSpan.FromMinutes(2)) return c.Info;
        var info = await cli.ProbeAsync();
        lock (_lock) _info[cli.Id] = (info, DateTimeOffset.UtcNow);
        return info;
    }

    public async Task<List<AgentInfo>> AllInfoAsync(bool refresh = false)
    {
        var all = await Task.WhenAll(Core.Claude.Agents.All.Select(a => InfoAsync(a, refresh)));
        return [.. all];
    }

    // ---- state ----

    public AnalysisDto State(List<AgentInfo> agents)
    {
        lock (_lock)
        {
            return new AnalysisDto
            {
                Agents = agents, Agent = _last.Agent, Running = jobs.Running && jobs.Current?.Kind == JobKind.Analysis,
                ScanId = _last.ScanId, Scope = _last.Scope, SessionId = _last.SessionId, StartedAt = _last.StartedAt, FinishedAt = _last.FinishedAt,
                Outcome = _last.Outcome, Notes = _last.Notes, Target = _last.Target, Usage = _last.Usage ?? LoadUsage(),
                Events = [.. _events],
            };
        }
    }

    private UsageInfo? LoadUsage()
    {
        try { return JsonSerializer.Deserialize(File.ReadAllBytes(UsageFile), AnalysisJson.Default.UsageInfo); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    // ---- run ----

    public Func<CancellationToken, Task> Prepare(string scanId, AnalysisRequest req, int target)
    {
        var agent = Core.Claude.Agents.Normalize(req.Agent);
        lock (_lock)
        {
            _events.Clear();
            _last = new AnalysisDto { Agent = agent, ScanId = scanId, Scope = AnalysisScope.Normalize(req.Scope), StartedAt = DateTimeOffset.UtcNow, Target = target, Usage = _last.Usage ?? LoadUsage() };
        }
        return ct => Run(agent, scanId, req, target, ct);
    }

    private async Task Run(string agent, string scanId, AnalysisRequest req, int target, CancellationToken ct)
    {
        var cli = AgentCli.Get(agent);
        var sessionId = cli.PresetSessionId ? Guid.NewGuid().ToString() : null;
        lock (_lock) _last.SessionId = sessionId;
        try
        {
            await cli.Run(new AgentRun
            {
                WorkDir = cli.WorkDir(home), PromptName = "analyze", Home = home, ScanId = scanId, SessionId = sessionId,
                MaxTurns = req.MaxTurns > 0 ? req.MaxTurns : AnalysisScope.Turns(target), Model = req.Model,
                Message = UserMessage(scanId, AnalysisScope.Normalize(req.Scope), target),
            }, ev =>
            {
                RecordUsage(ev);
                if (ev.Kind == AnalysisEventKind.Init && ev.SessionId is { } sid) lock (_lock) _last.SessionId = sid;
                if (ev.Kind == AnalysisEventKind.Note) lock (_lock) _last.Notes++;
                if (ev.Kind == AnalysisEventKind.Result) lock (_lock) _last.Outcome = ev.Subtype;
                Publish(ev);
            }, j => { lock (_lock) _job = j; }, ct);
        }
        finally
        {
            lock (_lock) _last.FinishedAt = DateTimeOffset.UtcNow;
            hub.Changed("notes");
        }
    }

    /// <summary>Plan usage from any run (analysis or chat), kept for the Analysis page.</summary>
    public void RecordUsage(AnalysisEvent ev)
    {
        if (ev.Kind != AnalysisEventKind.RateLimit || ev.Usage is not { } u) return;
        lock (_lock) _last.Usage = u;
        try { File.WriteAllBytes(UsageFile, JsonSerializer.SerializeToUtf8Bytes(u, AnalysisJson.Default.UsageInfo)); } catch (IOException) { }
    }

    private void Publish(AnalysisEvent ev)
    {
        lock (_lock)
        {
            if (_events.Count < 5000) _events.Add(ev);
        }
        hub.Publish("analysis", ev, AnalysisJson.Web.AnalysisEvent);
    }

    internal static string UserMessage(string scanId, string scope, int target) => (scope, Lang.IsTurkish) switch
    {
        (AnalysisScope.Quarantine, true) =>
            $"Ferahla'nın karantinasını gözden geçir ({target} öğe; tarama {scanId}). Kullanıcı bunları kalıcı olarak silmeyi düşünüyor. " +
            "list_quarantine ile hepsini al ve her öğenin finding id'sine bir not yaz. Notları ve özeti Türkçe yaz.",
        (AnalysisScope.Quarantine, false) =>
            $"Review Ferahla's quarantine ({target} items; scan {scanId}). The user is thinking of deleting them for good. " +
            "Get them all with list_quarantine and write a note on each entry's finding id. Write the notes and the summary in English.",
        (AnalysisScope.Unnoted, true) =>
            $"Ferahla taramasını analiz et ({scanId}). Sadece henüz notu olmayan önerilere bak (list_findings toAssess=true withoutNote=true): {target} öneri, her birine bir not. Notları ve özeti Türkçe yaz.",
        (AnalysisScope.Unnoted, false) =>
            $"Analyze the Ferahla scan ({scanId}). Only the suggestions without a note yet (list_findings toAssess=true withoutNote=true): {target} suggestions, a note on each. Write the notes and the summary in English.",
        (_, true) =>
            $"Ferahla taramasını analiz et ({scanId}). Değerlendirilecek {target} önerinin her birine bir not yaz (list_findings toAssess=true). Notları ve özeti Türkçe yaz.",
        _ =>
            $"Analyze the Ferahla scan ({scanId}). Write a note on each of the {target} suggestions to assess (list_findings toAssess=true). Write the notes and the summary in English.",
    };

    public bool Stop()
    {
        lock (_lock) _job?.Terminate();
        return jobs.Cancel();
    }

    /// <summary>
    /// Opens the session in a terminal (Windows Terminal if present) to continue by hand, with the same isolation as
    /// the analysis: no built-in tools, no settings or hooks. The session history contains scan text (file names), which
    /// is untrusted; it must not meet Bash or file tools.
    /// </summary>
    public bool OpenTerminal()
    {
        string? sid, agent; lock (_lock) { sid = _last.SessionId; agent = _last.Agent; }
        // Claude only: Codex' interactive mode can't ignore the user's config, so the isolation would not hold.
        if (agent != Core.Claude.Agents.Claude) return false;
        var claude = AgentCli.Claude.Find();
        var workDir = AgentCli.Claude.WorkDir(home);
        if (sid is null || claude is null || !Guid.TryParse(sid, out _)) return false;
        var cfg = Path.Combine(workDir, "ferahla-mcp.json");
        if (claude.Contains('"') || cfg.Contains('"') || claude.Contains('%') || cfg.Contains('%')) return false;
        // A small batch file, so the empty "" arguments survive every launcher. --mcp-config is not restored by
        // --resume, so it is given again (docs/spikes.md 2-4).
        var script = Path.Combine(workDir, "resume.cmd");
        File.WriteAllText(script,
            "@echo off\r\n" +
            $"\"{claude}\" --resume {sid} --mcp-config \"{cfg}\" --strict-mcp-config --tools \"\" --setting-sources \"\" --allowedTools mcp__ferahla__*\r\n",
            new UTF8Encoding(false));
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
        ProcessStartInfo psi;
        if (File.Exists(wt))
        {
            psi = new ProcessStartInfo(wt) { UseShellExecute = false };
            foreach (var a in new[] { "-w", "new", "new-tab", "-d", workDir, cmd, "/k", script }) psi.ArgumentList.Add(a);
        }
        else
        {
            psi = new ProcessStartInfo(cmd) { UseShellExecute = true, WorkingDirectory = workDir, Arguments = "/k \"" + script + "\"" };
        }
        Process.Start(psi)?.Dispose();
        return true;
    }
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
[System.Text.Json.Serialization.JsonSerializable(typeof(AnalysisEvent))]
[System.Text.Json.Serialization.JsonSerializable(typeof(UsageInfo))]
[System.Text.Json.Serialization.JsonSerializable(typeof(AnalysisDto))]
[System.Text.Json.Serialization.JsonSerializable(typeof(AnalysisRequest))]
[System.Text.Json.Serialization.JsonSerializable(typeof(AgentInfo))]
[System.Text.Json.Serialization.JsonSerializable(typeof(List<AgentInfo>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ChatRequest))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ChatDto))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ChatEventDto))]
internal sealed partial class AnalysisJson : System.Text.Json.Serialization.JsonSerializerContext
{
    private static AnalysisJson? _web;

    /// <summary>Readable non-ASCII text in events; the UI never inserts it as HTML.</summary>
    public static AnalysisJson Web => _web ??= new AnalysisJson(new JsonSerializerOptions(Default.Options) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}

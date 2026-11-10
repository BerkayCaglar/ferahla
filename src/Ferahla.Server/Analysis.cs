using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Ferahla.Core.Claude;
using Ferahla.Core.Text;
using Ferahla.Windows;

namespace Ferahla.Server;

public sealed class ClaudeInfo
{
    public bool Installed { get; set; }
    public string? Version { get; set; }
    public bool LoggedIn { get; set; }
    /// <summary>Only these two come from <c>claude auth status</c>; e-mail and organization are never read.</summary>
    public string? AuthMethod { get; set; }
    public string? SubscriptionType { get; set; }
    public bool ApiKeyInEnvironment { get; set; }
    public string? Error { get; set; }
}

public sealed class AnalysisDto
{
    public ClaudeInfo Claude { get; set; } = new();
    public bool Running { get; set; }
    public string? ScanId { get; set; }
    public string? Scope { get; set; }
    public string? SessionId { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? Outcome { get; set; }
    public int Notes { get; set; }
    public int Target { get; set; }
    public UsageInfo? Usage { get; set; }
    public List<AnalysisEvent> Events { get; set; } = [];
}

public sealed class AnalysisRequest
{
    public string? Scan { get; set; }
    public string? Model { get; set; }
    /// <summary>0: sized to the number of notes to write (<see cref="AnalysisScope.Turns"/>).</summary>
    public int MaxTurns { get; set; }
    /// <summary><see cref="AnalysisScope"/>: all, unnoted or quarantine.</summary>
    public string? Scope { get; set; }
}

/// <summary>
/// Runs <c>claude -p</c> against Ferahla's MCP server in a neutral folder, isolated (see docs/spikes.md 2–4 and
/// plan 4.7): no built-in tools, only <c>mcp__ferahla__*</c>, no settings, CLAUDE.md or hooks from anywhere, every
/// permission prompt refused. The process tree lives in a job object. Ferahla never touches Claude's credentials.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class Analysis(string home, EventHub hub, Jobs jobs)
{
    private readonly Lock _lock = new();
    private readonly List<AnalysisEvent> _events = [];
    private AnalysisDto _last = new();
    private JobObject? _job;
    private ClaudeInfo? _info;
    private DateTimeOffset _infoAt;

    public string WorkDir => Path.Combine(home, "claude-work");
    private string UsageFile => Path.Combine(home, "claude-usage.json");

    // ---- detection ----

    public static string? FindClaude()
    {
        var names = new[] { "claude.exe", "claude.cmd" };
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"));
        foreach (var name in names)
            foreach (var d in dirs)
            {
                try
                {
                    var p = Path.Combine(d.Trim().Trim('"'), name);
                    if (File.Exists(p)) return p;
                }
                catch (ArgumentException) { }
            }
        return null;
    }

    public async Task<ClaudeInfo> InfoAsync(bool refresh = false)
    {
        lock (_lock)
            if (!refresh && _info is not null && DateTimeOffset.UtcNow - _infoAt < TimeSpan.FromMinutes(2)) return _info;
        var info = new ClaudeInfo { ApiKeyInEnvironment = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")) };
        var claude = FindClaude();
        if (claude is not null)
        {
            info.Installed = true;
            var (code, output) = await RunShort(claude, ["--version"]);
            if (code == 0) info.Version = output.Trim().Split(' ')[0];
            (code, output) = await RunShort(claude, ["auth", "status"]);
            try
            {
                using var doc = JsonDocument.Parse(output);
                var r = doc.RootElement;
                info.LoggedIn = code == 0 && r.TryGetProperty("loggedIn", out var li) && li.ValueKind == JsonValueKind.True;
                if (r.TryGetProperty("authMethod", out var am) && am.ValueKind == JsonValueKind.String) info.AuthMethod = am.GetString();
                if (r.TryGetProperty("subscriptionType", out var st) && st.ValueKind == JsonValueKind.String) info.SubscriptionType = st.GetString();
            }
            catch (JsonException) { info.LoggedIn = false; }
        }
        lock (_lock) { _info = info; _infoAt = DateTimeOffset.UtcNow; }
        return info;
    }

    private static async Task<(int Code, string Output)> RunShort(string exe, string[] args)
    {
        try
        {
            using var p = Process.Start(Start(exe, args, Path.GetTempPath()))!;
            p.StandardInput.Close();
            var read = p.StandardOutput.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await p.WaitForExitAsync(cts.Token);
            return (p.ExitCode, await read);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException or InvalidOperationException) { return (-1, ""); }
    }

    // ---- state ----

    public AnalysisDto State(ClaudeInfo info)
    {
        lock (_lock)
        {
            var dto = new AnalysisDto
            {
                Claude = info, Running = jobs.Running && jobs.Current?.Kind == JobKind.Analysis,
                ScanId = _last.ScanId, Scope = _last.Scope, SessionId = _last.SessionId, StartedAt = _last.StartedAt, FinishedAt = _last.FinishedAt,
                Outcome = _last.Outcome, Notes = _last.Notes, Target = _last.Target, Usage = _last.Usage ?? LoadUsage(),
                Events = [.. _events],
            };
            return dto;
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
        lock (_lock)
        {
            _events.Clear();
            _last = new AnalysisDto { ScanId = scanId, Scope = AnalysisScope.Normalize(req.Scope), StartedAt = DateTimeOffset.UtcNow, Target = target, Usage = _last.Usage ?? LoadUsage() };
        }
        return ct => Run(scanId, req, target, ct);
    }

    private async Task Run(string scanId, AnalysisRequest req, int target, CancellationToken ct)
    {
        Directory.CreateDirectory(WorkDir);
        var prompt = ClaudeProcess.WritePrompt(WorkDir, "analyze");
        var mcpConfig = ClaudeProcess.WriteMcpConfig(WorkDir, "ferahla-mcp.json", home, scanId);
        var sessionId = Guid.NewGuid().ToString();
        var args = ClaudeProcess.Args(prompt, mcpConfig, sessionId, resume: false, req.MaxTurns > 0 ? req.MaxTurns : AnalysisScope.Turns(target), req.Model);
        lock (_lock) _last.SessionId = sessionId;
        try
        {
            await ClaudeProcess.Run(WorkDir, args, UserMessage(scanId, AnalysisScope.Normalize(req.Scope), target), ev =>
            {
                RecordUsage(ev);
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
        string? sid; lock (_lock) sid = _last.SessionId;
        var claude = FindClaude();
        if (sid is null || claude is null || !Guid.TryParse(sid, out _)) return false;
        var cfg = Path.Combine(WorkDir, "ferahla-mcp.json");
        if (claude.Contains('"') || cfg.Contains('"') || claude.Contains('%') || cfg.Contains('%')) return false;
        // A small batch file, so the empty "" arguments survive every launcher. --mcp-config is not restored by
        // --resume, so it is given again (docs/spikes.md 2-4).
        var script = Path.Combine(WorkDir, "resume.cmd");
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
            foreach (var a in new[] { "-w", "new", "new-tab", "-d", WorkDir, cmd, "/k", script }) psi.ArgumentList.Add(a);
        }
        else
        {
            psi = new ProcessStartInfo(cmd) { UseShellExecute = true, WorkingDirectory = WorkDir, Arguments = "/k \"" + script + "\"" };
        }
        Process.Start(psi)?.Dispose();
        return true;
    }
    /// <summary>ProcessStartInfo for claude.exe, or claude.cmd through cmd.exe (npm installs).</summary>
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

    private static string Quote(string a) => a.Length == 0 ? "\"\"" : a.IndexOfAny([' ', '"', '\t', '&', '|', '<', '>', '^']) < 0 ? a : "\"" + a.Replace("\"", "\\\"") + "\"";
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
[System.Text.Json.Serialization.JsonSerializable(typeof(AnalysisEvent))]
[System.Text.Json.Serialization.JsonSerializable(typeof(UsageInfo))]
[System.Text.Json.Serialization.JsonSerializable(typeof(AnalysisDto))]
[System.Text.Json.Serialization.JsonSerializable(typeof(AnalysisRequest))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ClaudeInfo))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ChatRequest))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ChatDto))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ChatEventDto))]
internal sealed partial class AnalysisJson : System.Text.Json.Serialization.JsonSerializerContext
{
    private static AnalysisJson? _web;

    /// <summary>Readable non-ASCII text in events; the UI never inserts it as HTML.</summary>
    public static AnalysisJson Web => _web ??= new AnalysisJson(new JsonSerializerOptions(Default.Options) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}

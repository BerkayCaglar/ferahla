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
    public int MaxTurns { get; set; } = 40;
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
                ScanId = _last.ScanId, SessionId = _last.SessionId, StartedAt = _last.StartedAt, FinishedAt = _last.FinishedAt,
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

    public Func<CancellationToken, Task> Prepare(string scanId, AnalysisRequest req, int target, ClaudeInfo info)
    {
        lock (_lock)
        {
            _events.Clear();
            _last = new AnalysisDto { ScanId = scanId, StartedAt = DateTimeOffset.UtcNow, Target = target, Usage = _last.Usage ?? LoadUsage() };
        }
        return ct => Run(scanId, req, info, ct);
    }

    private async Task Run(string scanId, AnalysisRequest req, ClaudeInfo info, CancellationToken ct)
    {
        var claude = FindClaude() ?? throw new InvalidOperationException(Lang.T("Claude Code is not installed", "Claude Code kurulu değil"));
        Directory.CreateDirectory(WorkDir);
        var prompt = Path.Combine(WorkDir, "analyze.md");
        File.WriteAllText(prompt, Prompt(Lang.Current), new UTF8Encoding(false));
        var mcpConfig = Path.Combine(WorkDir, "ferahla-mcp.json");
        File.WriteAllText(mcpConfig, McpConfig(scanId), new UTF8Encoding(false));
        var sessionId = Guid.NewGuid().ToString();

        var args = new List<string>
        {
            "-p", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
            "--append-system-prompt-file", prompt,
            "--mcp-config", mcpConfig, "--strict-mcp-config",
            "--tools", "", "--allowedTools", "mcp__ferahla__*", "--permission-prompts", "none",
            "--setting-sources", "", "--session-id", sessionId, "--max-turns", Math.Clamp(req.MaxTurns, 5, 120).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (req.Model is { Length: > 0 } model && model.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '[' or ']')) { args.Add("--model"); args.Add(model); }

        var psi = Start(claude, args, WorkDir);
        // In -p mode an API key in the environment takes precedence over the user's subscription.
        psi.Environment.Remove("ANTHROPIC_API_KEY");
        lock (_lock) _last.SessionId = sessionId;

        using var job = new JobObject();
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("claude did not start");
        job.Assign(p);
        lock (_lock) _job = job;
        try
        {
            var stdin = p.StandardInput.BaseStream;
            await stdin.WriteAsync(Encoding.UTF8.GetBytes(UserMessage(scanId)), ct);
            stdin.Close();
            var stderr = p.StandardError.ReadToEndAsync(ct);
            using var reg = ct.Register(job.Terminate);

            var parser = new StreamParser();
            string? line;
            var initChecked = false;
            while ((line = await p.StandardOutput.ReadLineAsync(ct)) is not null)
            {
                foreach (var ev in parser.Feed(line))
                {
                    if (ev.Kind == AnalysisEventKind.Init && !initChecked)
                    {
                        initChecked = true;
                        if (StreamParser.CheckInit(ev) is { } problem)
                        {
                            job.Terminate();
                            Publish(new AnalysisEvent { Kind = AnalysisEventKind.Error, Text = problem });
                            throw new InvalidOperationException(Lang.T("Analysis stopped for safety: ", "Analiz güvenlik için durduruldu: ") + problem);
                        }
                    }
                    if (ev.Kind == AnalysisEventKind.RateLimit && ev.Usage is { } u)
                    {
                        lock (_lock) _last.Usage = u;
                        try { File.WriteAllBytes(UsageFile, JsonSerializer.SerializeToUtf8Bytes(u, AnalysisJson.Default.UsageInfo)); } catch (IOException) { }
                    }
                    if (ev.Kind == AnalysisEventKind.Note) lock (_lock) _last.Notes++;
                    if (ev.Kind == AnalysisEventKind.Result) lock (_lock) _last.Outcome = ev.Subtype;
                    Publish(ev);
                }
            }
            await p.WaitForExitAsync(ct);
            if (!initChecked)
            {
                var err = (await stderr).Trim();
                throw new InvalidOperationException(err.Length > 0 ? err[..Math.Min(err.Length, 400)] : $"claude exited with code {p.ExitCode}");
            }
        }
        finally
        {
            lock (_lock) { _job = null; _last.FinishedAt = DateTimeOffset.UtcNow; }
            hub.Changed("notes");
        }
    }

    private void Publish(AnalysisEvent ev)
    {
        lock (_lock)
        {
            if (_events.Count < 5000) _events.Add(ev);
        }
        hub.Publish("analysis", ev, AnalysisJson.Web.AnalysisEvent);
    }

    private string McpConfig(string scanId)
    {
        var exe = Environment.ProcessPath ?? "ferahla.exe";
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject(); w.WriteStartObject("mcpServers"); w.WriteStartObject("ferahla");
            w.WriteString("type", "stdio"); w.WriteString("command", exe);
            w.WriteStartArray("args");
            foreach (var a in new[] { "mcp", "--scan", scanId, "--home", home }) w.WriteStringValue(a);
            w.WriteEndArray();
            // Finding titles in the UI's language, whatever Windows' language is.
            w.WriteStartObject("env"); w.WriteString("FERAHLA_LANG", Lang.Current); w.WriteEndObject();
            w.WriteEndObject(); w.WriteEndObject(); w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string UserMessage(string scanId) => Lang.IsTurkish
        ? $"Ferahla taramasını analiz et ({scanId}). Notları ve özeti Türkçe yaz."
        : $"Analyze the Ferahla scan ({scanId}). Write the notes and the summary in English.";

    internal static string Prompt(string lang)
    {
        var name = lang == "tr" ? "prompts/analyze.tr.md" : "prompts/analyze.en.md";
        using var s = typeof(Analysis).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException("prompt missing: " + name);
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    public bool Stop()
    {
        lock (_lock) _job?.Terminate();
        return jobs.Cancel();
    }

    /// <summary>Opens the session in a terminal (Windows Terminal if present) to continue by hand.</summary>
    public bool OpenTerminal()
    {
        string? sid; lock (_lock) sid = _last.SessionId;
        var claude = FindClaude();
        if (sid is null || claude is null) return false;
        var cfg = Path.Combine(WorkDir, "ferahla-mcp.json");
        // --mcp-config is not restored by --resume, so it is given again (docs/spikes.md 2–4).
        var claudeArgs = new[] { "--resume", sid, "--mcp-config", cfg, "--strict-mcp-config", "--allowedTools", "mcp__ferahla__*" };
        var wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
        ProcessStartInfo psi;
        if (File.Exists(wt))
        {
            psi = new ProcessStartInfo(wt) { UseShellExecute = false };
            foreach (var a in new[] { "-w", "new", "new-tab", "-d", WorkDir, claude }.Concat(claudeArgs)) psi.ArgumentList.Add(a);
        }
        else
        {
            psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = true, WorkingDirectory = WorkDir };
            var quoted = string.Join(' ', claudeArgs.Select(a => "'" + a.Replace("'", "''") + "'"));
            psi.Arguments = $"-NoExit -Command \"& '{claude.Replace("'", "''")}' {quoted}\"";
        }
        Process.Start(psi);
        return true;
    }

    /// <summary>ProcessStartInfo for claude.exe, or claude.cmd through cmd.exe (npm installs).</summary>
    private static ProcessStartInfo Start(string exe, IEnumerable<string> args, string workDir)
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
internal sealed partial class AnalysisJson : System.Text.Json.Serialization.JsonSerializerContext
{
    private static AnalysisJson? _web;

    /// <summary>Readable non-ASCII text in events; the UI never inserts it as HTML.</summary>
    public static AnalysisJson Web => _web ??= new AnalysisJson(new JsonSerializerOptions(Default.Options) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}

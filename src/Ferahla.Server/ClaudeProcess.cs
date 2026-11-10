using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Ferahla.Core.Claude;
using Ferahla.Core.Text;
using Ferahla.Windows;

namespace Ferahla.Server;

/// <summary>
/// One isolated <c>claude -p</c> run, shared by the scan analysis and the per-suggestion chat: no built-in tools,
/// only <c>mcp__ferahla__*</c>, no settings, CLAUDE.md or hooks from anywhere, every permission prompt refused, the
/// process tree in a job object, and a stop as soon as the init event shows anything but Ferahla's tools.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ClaudeProcess
{
    /// <param name="session">New session: <c>--session-id</c>; existing one: <c>--resume</c>.</param>
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
        if (model is { Length: > 0 } && model.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '[' or ']')) { args.Add("--model"); args.Add(model); }
        return args;
    }

    /// <summary>Writes the MCP config that starts this exe as <c>ferahla mcp</c> on one scan.</summary>
    public static string WriteMcpConfig(string workDir, string fileName, string home, string scanId)
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
        var path = Path.Combine(workDir, fileName);
        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    public static string WritePrompt(string workDir, string name)
    {
        var path = Path.Combine(workDir, name + ".md");
        File.WriteAllText(path, Prompt(name, Lang.Current), new UTF8Encoding(false));
        return path;
    }

    internal static string Prompt(string name, string lang)
    {
        var res = $"prompts/{name}.{(lang == "tr" ? "tr" : "en")}.md";
        using var s = typeof(ClaudeProcess).Assembly.GetManifestResourceStream(res) ?? throw new InvalidOperationException("prompt missing: " + res);
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    /// <summary>Runs claude to the end, handing every parsed event to <paramref name="onEvent"/>.</summary>
    /// <param name="track">Gets the job object while the run lives (to stop it), then null.</param>
    public static async Task Run(string workDir, List<string> args, string message, Action<AnalysisEvent> onEvent, Action<JobObject?> track, CancellationToken ct)
    {
        var claude = Analysis.FindClaude() ?? throw new InvalidOperationException(Lang.T("Claude Code is not installed", "Claude Code kurulu değil"));
        var psi = Analysis.Start(claude, args, workDir);
        // In -p mode an API key in the environment takes precedence over the user's subscription.
        psi.Environment.Remove("ANTHROPIC_API_KEY");

        using var job = new JobObject();
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("claude did not start");
        job.Assign(p);
        track(job);
        try
        {
            var stdin = p.StandardInput.BaseStream;
            await stdin.WriteAsync(Encoding.UTF8.GetBytes(message), ct);
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
                            onEvent(new AnalysisEvent { Kind = AnalysisEventKind.Error, Text = problem });
                            throw new InvalidOperationException(Lang.T("Analysis stopped for safety: ", "Analiz güvenlik için durduruldu: ") + problem);
                        }
                    }
                    onEvent(ev);
                }
            }
            await p.WaitForExitAsync(ct);
            if (!initChecked)
            {
                var err = (await stderr).Trim();
                throw new InvalidOperationException(err.Length > 0 ? err[..Math.Min(err.Length, 400)] : $"claude exited with code {p.ExitCode}");
            }
        }
        finally { track(null); }
    }
}

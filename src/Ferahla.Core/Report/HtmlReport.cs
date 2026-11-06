using System.Text.Json;
using Ferahla.Core.Diff;
using Ferahla.Core.Findings;
using Ferahla.Core.Json;
using Ferahla.Core.Model;
using Ferahla.Core.Text;

namespace Ferahla.Core.Report;

public sealed class ReportData
{
    public string Lang { get; set; } = "en";
    public DateTimeOffset GeneratedAt { get; set; }
    public string ToolVersion { get; set; } = "";
    public long Files { get; set; }
    public double ElapsedSeconds { get; set; }
    public FindingsResult Findings { get; set; } = new();
    public DiffResult? Diff { get; set; }
}

/// <summary>
/// One self-contained, read-only HTML file: no network (CSP default-src 'none'), data embedded as a JSON island.
/// The default encoder escapes &lt; &gt; &amp; and quotes, so scan data can't close the script element.
/// </summary>
public static class HtmlReport
{
    private const string Placeholder = "/*__DATA__*/";

    public static string Render(Snapshot snapshot, FindingsResult findings, DiffResult? diff, DateTimeOffset now)
    {
        var data = new ReportData
        {
            Lang = Text.Lang.Current,
            GeneratedAt = now,
            ToolVersion = snapshot.ToolVersion,
            Files = snapshot.Stats.Files,
            ElapsedSeconds = snapshot.ElapsedSeconds,
            Findings = findings,
            Diff = diff,
        };
        var json = JsonSerializer.Serialize(data, FerahlaJson.Default.ReportData);
        var template = Template();
        var i = template.IndexOf(Placeholder, StringComparison.Ordinal);
        if (i < 0) throw new InvalidOperationException("report template has no data placeholder");
        var title = $"Ferahla · {snapshot.Root} · {snapshot.ScannedAt.LocalDateTime:yyyy-MM-dd}";
        return template[..i].Replace("<title>Ferahla</title>", "<title>" + System.Net.WebUtility.HtmlEncode(title) + "</title>")
               + json + template[(i + Placeholder.Length)..];
    }

    private static string Template()
    {
        using var s = typeof(HtmlReport).Assembly.GetManifestResourceStream("report/report.html")
                      ?? throw new InvalidOperationException("report template missing");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}

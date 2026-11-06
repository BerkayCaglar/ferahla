using Ferahla.Core.Findings;
using Ferahla.Core.Report;
using Ferahla.Core.Rules;

namespace Ferahla.Core.Tests;

public class ReportTests
{
    [Fact]
    public void Report_is_self_contained_and_data_cannot_break_out_of_the_script()
    {
        using var t = new TempTree();
        t.File(@"home\AppData\Local\pip\cache\x", 20 << 20);
        var snap = FindingsEngineTests.Scan(t);
        var findings = new FindingsEngine(RuleLoader.LoadBuiltIn()).Run(snap, live: false);
        findings.Findings[0].Title = "</script><script>alert(1)</script><!--";

        var html = HtmlReport.Render(snap, findings, null, DateTimeOffset.Now);

        // The data island must end at its own </script> and still parse, with the hostile title intact inside it.
        const string open = "<script type=\"application/json\" id=\"data\">";
        var start = html.IndexOf(open, StringComparison.Ordinal) + open.Length;
        var island = html[start..html.IndexOf("</script>", start, StringComparison.Ordinal)];
        using var doc = System.Text.Json.JsonDocument.Parse(island);
        Assert.Equal("</script><script>alert(1)</script><!--", doc.RootElement.GetProperty("findings").GetProperty("findings")[0].GetProperty("title").GetString());
        Assert.DoesNotContain("<script>alert(1)", html);
        Assert.Contains("default-src 'none'", html);
        Assert.DoesNotContain("http://", html.Replace("http://www.w3.org", ""));
        Assert.DoesNotContain("https://", html);
        Assert.DoesNotContain("/*__DATA__*/", html);
    }
}

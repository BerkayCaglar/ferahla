using Ferahla.Core.Claude;
using Ferahla.Server;

namespace Ferahla.Core.Tests;

/// <summary>Codex: the JSONL parser (invented fixtures in tests/fixtures/codex, same schema as codex-cli 0.160) and the isolation arguments.</summary>
public class CodexTests
{
    private static List<AnalysisEvent> Parse(string name)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "tests", "fixtures", "codex", name);
            if (File.Exists(p))
            {
                var parser = new CodexParser { Model = "sample-model" };
                return File.ReadLines(p).SelectMany(parser.Feed).ToList();
            }
        }
        throw new FileNotFoundException(name);
    }

    [Fact]
    public void Successful_run_yields_init_tools_notes_text_and_tokens()
    {
        var ev = Parse("exec-success.jsonl");
        var init = ev.First();
        Assert.Equal(AnalysisEventKind.Init, init.Kind);
        Assert.Equal("00000000-0000-4000-8000-000000000001", init.SessionId);
        Assert.Equal("sample-model", init.Model);
        Assert.All(ev, e => Assert.Null(CodexParser.Check(e)));

        Assert.Contains(ev, e => e.Kind == AnalysisEventKind.Tool && e.Tool == "get_summary" && e.ToolId == "item_1");
        Assert.Contains(ev, e => e.Kind == AnalysisEventKind.ToolResult && e.ToolId == "item_1" && e.Text!.Contains("freeGb"));
        var notes = ev.Where(e => e.Kind == AnalysisEventKind.Note).ToList();
        Assert.Equal(2, notes.Count);
        Assert.Equal(("f-1111111111", "remove"), (notes[0].FindingId, notes[0].Verdict));
        Assert.Equal(("f-2222222222", "keep"), (notes[1].FindingId, notes[1].Verdict));
        // The batch tool is announced once, not once per started/completed event.
        Assert.Single(ev, e => e.Kind == AnalysisEventKind.Tool && e.Tool == "add_notes");

        var failed = Assert.Single(ev, e => e.Kind == AnalysisEventKind.ToolResult && e.ToolId == "item_4");
        Assert.True(failed.IsError);
        Assert.Equal("tool call timed out", failed.Text);

        Assert.Contains(ev, e => e.Kind == AnalysisEventKind.Text && e.Text!.Contains("build cache"));
        Assert.DoesNotContain(ev, e => e.Kind == AnalysisEventKind.Error); // the metadata warning is not an error
        var result = ev.Last();
        Assert.Equal(AnalysisEventKind.Result, result.Kind);
        Assert.Equal("success", result.Subtype);
        Assert.Equal(21250, result.Tokens);
        Assert.Equal(4, result.Turns); // three tool calls + the answer
    }

    [Fact]
    public void Failed_turn_gives_the_inner_message_once()
    {
        var ev = Parse("exec-failed.jsonl");
        var err = Assert.Single(ev, e => e.Kind == AnalysisEventKind.Error);
        Assert.Equal("The sample-model model is not supported.", err.Text);
        var result = ev.Last();
        Assert.Equal("error", result.Subtype);
        Assert.Equal("The sample-model model is not supported.", result.Text);
    }

    [Theory]
    [InlineData("""{"type":"item.started","item":{"id":"i1","type":"command_execution","command":"dir C:\\","status":"in_progress"}}""", "command_execution")]
    [InlineData("""{"type":"item.started","item":{"id":"i2","type":"web_search","query":"x"}}""", "web_search")]
    [InlineData("""{"type":"item.completed","item":{"id":"i3","type":"file_change","changes":[]}}""", "file_change")]
    [InlineData("""{"type":"item.started","item":{"id":"i4","type":"mcp_tool_call","server":"other","tool":"read_file","arguments":{}}}""", "other/read_file")]
    public void Anything_outside_Ferahla_stops_the_run(string line, string tool)
    {
        var ev = new CodexParser().Feed(line);
        var t = Assert.Single(ev);
        Assert.Equal(tool, t.Tool);
        Assert.NotNull(CodexParser.Check(t));
    }

    [Fact]
    public void Garbage_is_ignored()
    {
        var p = new CodexParser();
        Assert.Empty(p.Feed(""));
        Assert.Empty(p.Feed("not json"));
        Assert.Empty(p.Feed("[1,2]"));
        Assert.Empty(p.Feed("""{"type":"something.new","x":1}"""));
    }

    [Fact]
    public void Exec_arguments_keep_codex_isolated()
    {
        var known = new HashSet<string>(["shell_tool", "unified_exec", "apps", "plugins", "browser_use", "computer_use", "hooks"]);
        var first = CodexCli.Args(@"C:\App\ferahla.exe", ["mcp", "--scan", "s1", "--home", @"C:\Home", "--agent", "codex"], "tr", null, resume: false, "gpt-sample", "Line one\nIt's \"quoted\"", known);
        var next = CodexCli.Args(@"C:\App\ferahla.exe", ["mcp"], "en", "thread-1", resume: true, "bad model; rm", null, known);
        foreach (var a in new[] { first, next })
        {
            Assert.Equal("exec", a[0]);
            Assert.Contains("--json", a);
            Assert.Contains("--ignore-user-config", a);
            Assert.Contains("--ignore-rules", a);
            Assert.Contains("sandbox_mode='read-only'", a);
            Assert.Contains("approval_policy='never'", a);
            Assert.Contains("web_search='disabled'", a);
            Assert.Contains("skills.include_instructions=false", a);
            Assert.Contains("project_doc_max_bytes=0", a);
            foreach (var f in new[] { "shell_tool", "unified_exec", "apps", "plugins", "browser_use", "computer_use", "hooks" })
                Assert.Contains(f, a.Where((x, i) => i > 0 && a[i - 1] == "--disable"));
            // Only features this Codex knows are disabled (an unknown name would stop older versions).
            Assert.DoesNotContain("image_generation", a);
            Assert.Contains(@"mcp_servers.ferahla.command='C:\App\ferahla.exe'", a);
            Assert.Contains("mcp_servers.ferahla.required=true", a);
            Assert.Equal("-", a[^1]);
        }
        Assert.Contains("mcp_servers.ferahla.args=['mcp','--scan','s1','--home','C:\\Home','--agent','codex']", first);
        Assert.Contains("mcp_servers.ferahla.env={FERAHLA_LANG='tr'}", first);
        Assert.Contains("developer_instructions=\"Line one\\nIt's \\\"quoted\\\"\"", first);
        Assert.Equal("gpt-sample", first[first.IndexOf("-m") + 1]);
        Assert.DoesNotContain("resume", first);
        Assert.Equal(["exec", "resume", "thread-1"], next.Take(3));
        Assert.DoesNotContain("--sandbox", next); // resume doesn't take it; the -c override covers it
        Assert.DoesNotContain("-m", next);        // not a model name: dropped
        Assert.DoesNotContain(next, x => x.StartsWith("developer_instructions", StringComparison.Ordinal));
    }

    [Fact]
    public void Without_a_feature_list_every_feature_is_disabled()
    {
        var a = CodexCli.Args("ferahla.exe", ["mcp"], "en", null, false, null, null, null);
        Assert.Equal(CodexCli.DisabledFeatures.Length, a.Count(x => x == "--disable"));
    }

    [Theory]
    [InlineData("plain", "'plain'")]
    [InlineData(@"C:\Path with space\x", @"'C:\Path with space\x'")]
    [InlineData("it's", "\"it's\"")]
    [InlineData("a\"b\\c\nd", "\"a\\\"b\\\\c\\nd\"")]
    public void Toml_strings(string input, string expected) => Assert.Equal(expected, CodexCli.Toml(input));

    [Fact]
    public void Models_come_from_the_codex_catalog()
    {
        const string json = """
            {"models":[{"slug":"sample-large","display_name":"Sample Large","visibility":"list"},
                       {"slug":"sample-hidden","display_name":"Hidden","visibility":"hide"},
                       {"slug":"bad slug; rm","display_name":"Bad","visibility":"list"},
                       {"slug":"sample-small","display_name":"Sample Small","visibility":"list"}]}
            """;
        var m = CodexCli.ParseModels("warning line\n" + json);
        Assert.Equal(["sample-large", "sample-small"], m.Select(x => x.Id));
        Assert.Equal("Sample Large", m[0].Label);
        Assert.Empty(CodexCli.ParseModels("not json"));
    }

    [Theory]
    [InlineData(null, "claude")]
    [InlineData("", "claude")]
    [InlineData("CODEX", "codex")]
    [InlineData("gpt", "claude")]
    public void Agent_ids_normalize(string? input, string expected) => Assert.Equal(expected, Agents.Normalize(input));
}

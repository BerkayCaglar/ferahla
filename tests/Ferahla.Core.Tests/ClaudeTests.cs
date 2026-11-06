using Ferahla.Core.Claude;
using Ferahla.Core.Notes;
using Ferahla.Mcp;

namespace Ferahla.Core.Tests;

public class StreamParserTests
{
    private static string Fixture(string name)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "tests", "fixtures", "claude", name);
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException(name);
    }

    private static List<AnalysisEvent> Parse(string fixture)
    {
        var parser = new StreamParser();
        return File.ReadLines(Fixture(fixture)).SelectMany(parser.Feed).ToList();
    }

    [Fact]
    public void Successful_run_yields_init_tools_note_text_and_result()
    {
        var ev = Parse("stream-success.jsonl");
        var init = ev.First();
        Assert.Equal(AnalysisEventKind.Init, init.Kind);
        Assert.True(init.McpConnected);
        Assert.Null(StreamParser.CheckInit(init));
        Assert.Contains(ev, e => e.Kind == AnalysisEventKind.Tool && e.Tool == "get_summary");
        Assert.Contains(ev, e => e.Kind == AnalysisEventKind.ToolResult && e.Text!.Contains("freeGb"));
        var note = Assert.Single(ev, e => e.Kind == AnalysisEventKind.Note);
        Assert.Equal("f2", note.FindingId);
        Assert.False(string.IsNullOrWhiteSpace(note.Text));
        Assert.Contains(ev, e => e.Kind == AnalysisEventKind.Text);
        Assert.Contains(ev, e => e.Kind == AnalysisEventKind.RateLimit && e.Usage!.FiveHour is > 0);
        var result = ev.Last();
        Assert.Equal(AnalysisEventKind.Result, result.Kind);
        Assert.Equal("success", result.Subtype);
        Assert.Equal(Enumerable.Range(1, ev.Count), ev.Select(e => e.Seq));
    }

    [Fact]
    public void Max_turns_is_reported_as_an_error_result()
    {
        var r = Parse("stream-error-max-turns.jsonl").Last();
        Assert.Equal(AnalysisEventKind.Result, r.Kind);
        Assert.Equal("error_max_turns", r.Subtype);
        Assert.True(r.IsError);
    }

    [Fact]
    public void Resume_without_mcp_fails_the_init_check()
    {
        var init = Parse("stream-resume-no-mcp.jsonl").First(e => e.Kind == AnalysisEventKind.Init);
        Assert.NotNull(StreamParser.CheckInit(init));
    }

    [Fact]
    public void Init_with_foreign_tools_is_refused()
    {
        var init = new AnalysisEvent { Kind = AnalysisEventKind.Init, McpConnected = true, Tools = ["mcp__ferahla__get_summary", "Bash"] };
        Assert.Contains("Bash", StreamParser.CheckInit(init));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("tr")]
    public void Prompts_are_embedded_in_the_main_assembly(string lang) =>
        // "analyze.tr.md" once landed in a "tr" satellite assembly (culture detection); the run then failed.
        Assert.Contains("mcp__ferahla__", Ferahla.Server.Analysis.Prompt(lang));

    [Fact]
    public void Garbage_and_unknown_events_are_ignored()
    {
        var p = new StreamParser();
        Assert.Empty(p.Feed("not json"));
        Assert.Empty(p.Feed("{\"type\":\"something_new\",\"x\":1}"));
        Assert.Empty(p.Feed("[1,2]"));
    }
}

public sealed class NoteStoreTests : IDisposable
{
    private readonly TempTree _t = new();
    public void Dispose() => _t.Dispose();

    [Fact]
    public void Notes_are_kept_per_finding_and_applied()
    {
        var store = new NoteStore(_t.Root);
        store.Set("f-0123456789", "Old build output; safe.", NoteVerdict.Remove, "claude", "scan-1", DateTimeOffset.UtcNow);
        store.Set("f-0123456789", "Replaced note.", NoteVerdict.Check, "user", null, DateTimeOffset.UtcNow);
        var r = new Findings.FindingsResult { Findings = [new() { Id = "f-0123456789" }, new() { Id = "f-aaaaaaaaaa" }] };
        store.Apply(r);
        Assert.Equal("Replaced note.", r.Findings[0].Note);
        Assert.Equal(NoteVerdict.Check, r.Findings[0].NoteVerdict);
        Assert.Null(r.Findings[1].Note);
        store.Set("f-0123456789", "", NoteVerdict.None, "user", null, DateTimeOffset.UtcNow);
        Assert.Empty(store.Load().Notes);
    }

    [Theory]
    [InlineData("f-012345678")]
    [InlineData("f-0123456789a")]
    [InlineData("f-ABCDEF0123")]
    [InlineData("..\\x")]
    public void Only_finding_ids_are_accepted(string id) =>
        Assert.Throws<ArgumentException>(() => new NoteStore(_t.Root).Set(id, "x", NoteVerdict.None, "user", null, DateTimeOffset.UtcNow));

    [Fact]
    public void Long_text_is_trimmed() =>
        Assert.Equal(NoteStore.MaxText + 1, new NoteStore(_t.Root).Set("f-0123456789", new string('x', 5000), NoteVerdict.None, "user", null, DateTimeOffset.UtcNow).Text.Length);
}

public sealed class McpToolTests : IDisposable
{
    private readonly TempTree _t = new(outsideTemp: true);
    public void Dispose() => _t.Dispose();

    private string Setup()
    {
        var home = _t.Dir("home");
        _t.File("work\\app\\package.json", 2);
        _t.File("work\\app\\node_modules\\pkg\\big.bin", 60L << 20);
        var root = _t.P("work");
        var store = new Storage.SnapshotStore(home);
        Windows.Scanning.ScanService.Run(Core.PathUtil.Normalize(root), store, Windows.SystemInfo.KnownFolders());
        Session.Init(home, null);
        return home;
    }

    [Fact]
    public void Tools_read_the_scan_and_notes_go_to_the_store()
    {
        var home = Setup();
        var s = Tools.Summary();
        Assert.True(s.Findings >= 1);
        var page = Tools.Findings(null, "build", 0, 10, 0);
        var f = Assert.Single(page.Findings);
        Assert.Equal("regenerable", f.Risk);
        Assert.True(f.Mb >= 59);

        var inspect = Tools.Inspect(_t.P("work\\app"));
        Assert.Contains(f.Id, inspect.FindingIds);
        Assert.Contains("not in this scan", Tools.Inspect(@"Z:\elsewhere").Error, StringComparison.OrdinalIgnoreCase);

        var v = Tools.Verify(f.Id);
        Assert.Equal("verified", v.Verdict);
        Assert.Equal("unknown finding id", Tools.Verify("f-0000000000").Verdict);

        Assert.True(Tools.Note(f.Id, "Dependencies; npm install brings them back.", "remove").Saved);
        Assert.False(Tools.Note("f-0000000000", "x", "remove").Saved);
        var note = new NoteStore(home).Load().Notes[f.Id];
        Assert.Equal("claude", note.Source);
        Assert.Equal(NoteVerdict.Remove, note.Verdict);
        Assert.Equal("Dependencies; npm install brings them back.", Tools.Findings(null, "build", 0, 10, 0).Findings[0].ExistingNote);
    }
}

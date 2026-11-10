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
    [InlineData("analyze", "en")]
    [InlineData("analyze", "tr")]
    [InlineData("chat", "en")]
    [InlineData("chat", "tr")]
    public void Prompts_are_embedded_in_the_main_assembly(string name, string lang) =>
        // "analyze.tr.md" once landed in a "tr" satellite assembly (culture detection); the run then failed.
        Assert.Contains("mcp__ferahla__", Ferahla.Server.ClaudeProcess.Prompt(name, lang));

    [Fact]
    public void Runs_stay_isolated_and_follow_ups_resume_the_session()
    {
        var first = Ferahla.Server.ClaudeProcess.Args("p.md", "m.json", "sid", resume: false, 15, "haiku");
        var next = Ferahla.Server.ClaudeProcess.Args("p.md", "m.json", "sid", resume: true, 500, "bad model; rm");
        foreach (var a in new[] { first, next })
        {
            Assert.Equal("", a[a.IndexOf("--tools") + 1]);
            Assert.Equal("", a[a.IndexOf("--setting-sources") + 1]);
            Assert.Contains("--strict-mcp-config", a);
            Assert.Equal("m.json", a[a.IndexOf("--mcp-config") + 1]);
            Assert.Equal("p.md", a[a.IndexOf("--append-system-prompt-file") + 1]);
        }
        Assert.Equal("sid", first[first.IndexOf("--session-id") + 1]);
        Assert.DoesNotContain("--resume", first);
        Assert.Equal("sid", next[next.IndexOf("--resume") + 1]);
        Assert.Equal("150", next[next.IndexOf("--max-turns") + 1]);
        Assert.DoesNotContain("--model", next); // not a model name: dropped
        Assert.Equal("haiku", first[first.IndexOf("--model") + 1]);
    }

    [Fact]
    public void Garbage_and_unknown_events_are_ignored()
    {
        var p = new StreamParser();
        Assert.Empty(p.Feed("not json"));
        Assert.Empty(p.Feed("{\"type\":\"something_new\",\"x\":1}"));
        Assert.Empty(p.Feed("[1,2]"));
    }

    [Fact]
    public void Batch_notes_yield_one_note_event_each()
    {
        const string line = """
            {"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_fake01","name":"mcp__ferahla__add_notes",
            "input":{"notes":[{"findingId":"f-0000000001","text":"Build output.","verdict":"remove"},
            {"findingId":"f-0000000002","text":"Look first."},"junk"]}}]}}
            """;
        var ev = new StreamParser().Feed(line.ReplaceLineEndings(""));
        Assert.Equal("add_notes", Assert.Single(ev, e => e.Kind == AnalysisEventKind.Tool).Tool);
        var notes = ev.Where(e => e.Kind == AnalysisEventKind.Note).ToList();
        Assert.Equal(["f-0000000001", "f-0000000002"], notes.Select(n => n.FindingId));
        Assert.Equal(["remove", "check"], notes.Select(n => n.Verdict));
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
        Assert.Empty(Tools.Findings(null, "build", 0, 10, 0, withoutNote: true).Findings);

        var batch = Tools.Notes([
            new McpNoteInput { FindingId = f.Id, Text = "Rebuilt by npm install.", Verdict = "remove" },
            new McpNoteInput { FindingId = "f-0000000000", Text = "x" },
            new McpNoteInput { FindingId = f.Id, Text = " " },
        ]);
        Assert.Equal(1, batch.Saved);
        Assert.Equal(2, batch.Failed.Count);
        Assert.Equal("Rebuilt by npm install.", new NoteStore(home).Load().Notes[f.Id].Text);
        Assert.Equal(0, Tools.Quarantine(25, 0).Total);

        var detail = Tools.Finding(f.Id);
        Assert.Null(detail.Error);
        Assert.Equal("Rebuilt by npm install.", detail.Note);
        Assert.Equal("remove", detail.NoteVerdict);
        Assert.EndsWith("node_modules", Assert.Single(detail.Items).Path);
        Assert.NotNull(Tools.Finding("f-0000000000").Error);
    }

    [Fact]
    public void Quarantined_findings_are_listed_and_can_get_notes()
    {
        var home = Setup();
        var f = Assert.Single(Tools.Findings(null, "build", 0, 10, 0).Findings);
        var store = new Storage.SnapshotStore(home);
        var snap = store.Load(store.Latest()!);
        var plan = new Core.Quarantine.CleanupPlan
        {
            Schema = Core.Quarantine.CleanupPlan.CurrentSchema, Kind = Core.Quarantine.CleanupPlan.KindName, ScanId = snap.Id,
            Findings = [new() { Id = f.Id, RuleId = f.Rule, Action = "quarantine", Items = f.Items.Select(i => new Core.Quarantine.PlanItem { Path = i.Path, Bytes = (long)(i.Mb * (1 << 20)) }).ToList() }],
        };
        var moved = Windows.Quarantine.PlanRunner.Run(plan, snap, store, dryRun: false);
        Assert.Equal(1, moved.Done);

        var q = Tools.Quarantine(25, 0);
        var e = Assert.Single(q.Entries);
        Assert.Equal(f.Id, e.FindingId);
        Assert.EndsWith("node_modules", e.OriginalPath);
        Assert.True(Tools.Notes([new McpNoteInput { FindingId = f.Id, Text = "Safe to delete; npm install restores it.", Verdict = "remove" }]).Saved == 1);
        Assert.Equal("Safe to delete; npm install restores it.", Tools.Quarantine(25, 0).Entries[0].ExistingNote);
        Assert.Single(Tools.Finding(f.Id).InQuarantine);
    }
}

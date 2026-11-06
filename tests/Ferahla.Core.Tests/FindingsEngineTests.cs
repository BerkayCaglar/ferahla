using Ferahla.Core.Findings;
using Ferahla.Core.Model;
using Ferahla.Core.Rules;
using Ferahla.Windows.Scanning;

namespace Ferahla.Core.Tests;

public class FindingsEngineTests
{
    private const long MB = 1 << 20;

    internal static Dictionary<string, string> Known(TempTree t) => new()
    {
        ["UserProfile"] = t.P("home"),
        ["LocalAppData"] = t.P(@"home\AppData\Local"),
        ["AppData"] = t.P(@"home\AppData\Roaming"),
        ["Temp"] = t.P(@"home\AppData\Local\Temp"),
        ["Downloads"] = t.P(@"home\Downloads"),
        ["Desktop"] = t.P(@"home\Desktop"),
        ["ProgramFiles"] = t.P("Program Files"),
        ["ProgramData"] = t.P("ProgramData"),
        ["Windows"] = t.P("Windows"),
    };

    internal static Snapshot Scan(TempTree t) =>
        new Scanner(new ScanOptions { Root = t.Root, KnownFolders = Known(t) }).Run();

    private static FindingsResult Run(Snapshot s, Snapshot? prev = null, RuleSet? rules = null) =>
        new FindingsEngine(rules ?? RuleLoader.LoadBuiltIn()).Run(s, prev, live: false);

    [Fact]
    public void Built_in_rules_load_without_errors()
    {
        var set = RuleLoader.LoadBuiltIn(includePending: true);
        Assert.Empty(set.Errors);
        Assert.True(set.Rules.Count > 20);
    }

    [Fact]
    public void Known_cache_path_is_found()
    {
        using var t = new TempTree();
        t.File(@"home\AppData\Local\pip\cache\http\a.bin", 20 * MB);

        var r = Run(Scan(t));

        var f = Assert.Single(r.Findings, f => f.RuleId == "cache.pip");
        Assert.Equal(Risk.Safe, f.Risk);
        Assert.Equal(20 * MB, f.Bytes);
        Assert.Equal(t.P(@"home\AppData\Local\pip\cache"), f.Items.Single().Path);
    }

    [Fact]
    public void Wildcard_paths_match_each_profile()
    {
        using var t = new TempTree();
        t.File(@"home\AppData\Local\Google\Chrome\User Data\Default\Cache\x", 15 * MB);
        t.File(@"home\AppData\Local\Google\Chrome\User Data\Profile 1\Cache\x", 15 * MB);

        var r = Run(Scan(t));

        Assert.Equal(2, r.Findings.Count(f => f.RuleId == "cache.browsers"));
    }

    [Fact]
    public void Rust_target_is_found_with_project_evidence()
    {
        using var t = new TempTree();
        t.File(@"home\code\app\Cargo.toml", 100);
        t.File(@"home\code\app\src\main.rs", 2 * MB);
        t.File(@"home\code\app\target\debug\big.rlib", 40 * MB);

        var r = Run(Scan(t));

        var f = Assert.Single(r.Findings, f => f.RuleId == "build.rust");
        Assert.Equal(40 * MB, f.Bytes);
        Assert.Equal(t.P(@"home\code\app\target"), f.Items.Single().Path);
        Assert.Contains(f.Evidence, e => e.Contains("CargoToml"));
    }

    [Fact]
    public void Packages_inside_node_modules_are_not_separate_findings()
    {
        using var t = new TempTree();
        t.File(@"home\web\package.json", 100);
        t.File(@"home\web\node_modules\left-pad\package.json", 100);
        t.File(@"home\web\node_modules\left-pad\node_modules\x\big.js", 15 * MB);
        t.File(@"home\web\node_modules\other\big.js", 15 * MB);

        var r = Run(Scan(t));

        var f = Assert.Single(r.Findings, f => f.RuleId == "build.node");
        Assert.Equal(t.P(@"home\web\node_modules"), f.Items.Single().Path);
        Assert.Equal(30 * MB + 100, f.Bytes);
    }

    [Fact]
    public void Projects_in_app_data_and_dot_folders_are_left_alone()
    {
        using var t = new TempTree();
        t.File(@"home\AppData\Roaming\npm\package.json", 100);
        t.File(@"home\AppData\Roaming\npm\node_modules\tool\x.js", 20 * MB);
        t.File(@"home\.vscode\extensions\ext\package.json", 100);
        t.File(@"home\.vscode\extensions\ext\node_modules\x.js", 20 * MB);

        var r = Run(Scan(t));

        Assert.DoesNotContain(r.Findings, f => f.RuleId == "build.node");
    }

    [Fact]
    public void Unity_project_generated_folders_and_builds_are_separate()
    {
        using var t = new TempTree();
        t.Dir(@"home\game\Assets");
        t.Dir(@"home\game\ProjectSettings");
        t.File(@"home\game\Library\ArtifactDB", 30 * MB);
        t.File(@"home\game\Temp\x", 12 * MB);
        t.File(@"home\game\Builds\game.exe", 20 * MB);

        var r = Run(Scan(t));

        var gen = Assert.Single(r.Findings, f => f.RuleId == "build.unity");
        Assert.Equal(2, gen.Items.Count);
        Assert.Equal(Risk.Regenerable, gen.Risk);
        var builds = Assert.Single(r.Findings, f => f.RuleId == "build.unity-builds");
        Assert.Equal(Risk.Review, builds.Risk);
    }

    [Fact]
    public void Agent_scratch_folder_is_review()
    {
        using var t = new TempTree();
        t.File(@"home\code\proj\.agent-tmp\copy\tool.exe", 60 * MB);

        var r = Run(Scan(t));

        var f = Assert.Single(r.Findings, f => f.RuleId == "agent.tmp");
        Assert.Equal(Risk.Review, f.Risk);
        Assert.Contains("proj", f.Title);
    }

    [Fact]
    public void Quarantine_is_never_suggested_in_program_files_or_windows()
    {
        using var t = new TempTree();
        t.File(@"Program Files\App\cache\x", 30 * MB);
        t.File(@"Windows\Temp\x", 30 * MB);
        var rule = new Rule
        {
            Id = "test.bad", Category = "cache", Risk = Risk.Safe,
            Title = new LocalizedText { En = "x", Tr = "x" },
            Match = new RuleMatch { Path = [@"%ProgramFiles%\App\cache", @"%Windows%\Temp"] },
        };

        var r = Run(Scan(t), rules: new RuleSet([rule], []));

        Assert.Empty(r.Findings);
        Assert.Contains(r.NotSuggested, n => n.Path.EndsWith(@"App\cache"));
        Assert.Contains(r.NotSuggested, n => n.Path.EndsWith(@"Windows\Temp"));
    }

    [Fact]
    public void Findings_entirely_inside_another_are_dropped_and_partial_ones_not_counted_twice()
    {
        using var t = new TempTree();
        t.File(@"home\.cache\other\x", 300 * MB);
        t.File(@"home\.cache\inner\x", 20 * MB);
        t.File(@"home\outside\x", 20 * MB);
        Rule R(string id, params string[] paths) => new()
        {
            Id = id, Category = "cache", Risk = Risk.Safe,
            Title = new LocalizedText { En = "i", Tr = "i" },
            Match = new RuleMatch { Path = [.. paths] },
        };
        var all = RuleLoader.LoadBuiltIn();
        var rules = new RuleSet([.. all.Rules, R("test.inner", @"%UserProfile%\.cache\inner")], []);
        var mixedRules = new RuleSet([.. all.Rules, new Rule
        {
            Id = "test.two", Category = "x", Title = new() { En = "m", Tr = "m" },
            Match = new RuleMatch { Detector = "two" },
        }], []);

        var r = Run(Scan(t), rules: rules);

        var outer = Assert.Single(r.Findings, f => f.RuleId == "cache.dotcache");
        Assert.DoesNotContain(r.Findings, f => f.RuleId == "test.inner");
        Assert.Equal(320 * MB, outer.Bytes);

        // A finding with one item inside another finding and one outside keeps only the outside bytes.
        var mixed = new FindingsEngine(mixedRules, [new TwoItems(t)]).Run(Scan(t), live: false);
        var m = Assert.Single(mixed.Findings, f => f.RuleId == "test.mixed");
        Assert.Equal(20 * MB, m.Bytes);
        Assert.NotNull(m.Items.Single(i => i.Path.Contains(".cache")).NestedIn);
    }

    private sealed class TwoItems(TempTree t) : IDetector
    {
        public string Name => "two";
        public IEnumerable<Finding> Detect(DetectorContext ctx, Rule rule)
        {
            var f = FindingsEngine.NewFinding(new Rule { Id = "test.mixed", Category = "x", Title = new() { En = "m", Tr = "m" } }, t.P(@"home\outside"));
            f.Items.Add(FindingsEngine.DirItem(ctx.Index, ctx.Index.Find(t.P(@"home\.cache\inner"))));
            f.Items.Add(FindingsEngine.DirItem(ctx.Index, ctx.Index.Find(t.P(@"home\outside"))));
            yield return f;
        }
    }

    [Fact]
    public void Growth_and_new_are_reported_against_previous_scan()
    {
        using var t = new TempTree();
        t.File(@"home\AppData\Local\pip\cache\a", 20 * MB);
        var before = Scan(t);
        t.File(@"home\AppData\Local\pip\cache\b", 30 * MB);
        t.File(@"home\AppData\Local\uv\cache\a", 25 * MB);
        var after = Scan(t);

        var r = Run(after, before);

        var pip = Assert.Single(r.Findings, f => f.RuleId == "cache.pip");
        Assert.Equal(30 * MB, pip.GrowthBytes);
        Assert.False(pip.IsNew);
        Assert.True(Assert.Single(r.Findings, f => f.RuleId == "cache.uv").IsNew);
    }

    [Fact]
    public void Finding_ids_are_stable()
    {
        Assert.Equal(FindingsEngine.StableId("cache.pip", @"C:\Users\user\AppData\Local\pip\cache"),
                     FindingsEngine.StableId("cache.pip", @"c:\users\user\AppData\Local\pip\cache\"));
    }
}

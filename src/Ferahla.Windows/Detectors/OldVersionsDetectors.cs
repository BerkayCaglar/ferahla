using System.Text.Json;
using System.Text.RegularExpressions;
using Ferahla.Core;
using Ferahla.Core.Findings;
using Ferahla.Core.Model;
using Ferahla.Core.Rules;
using Ferahla.Core.Text;

namespace Ferahla.Windows.Detectors;

/// <summary>Unity editors no Unity project on this drive uses (from each project's ProjectSettings/ProjectVersion.txt).</summary>
public sealed partial class UnityEditorsDetector : IDetector
{
    public string Name => "unity-editors";

    [GeneratedRegex(@"m_EditorVersion:\s*(\S+)")]
    private static partial Regex EditorVersionRx();

    public IEnumerable<Finding> Detect(DetectorContext ctx, Rule rule)
    {
        if (!ctx.Live) yield break;
        var ix = ctx.Index;
        var roots = new List<string>();
        if (ctx.Var("ProgramFiles") is { } pf) roots.Add(PathUtil.Join(pf, @"Unity\Hub\Editor"));
        if (ctx.Var("AppData") is { } ad && DetectorUtil.ReadSmall(PathUtil.Join(ad, @"UnityHub\secondaryInstallPath.json")) is { } sec)
        {
            try { if (JsonDocument.Parse(sec).RootElement.GetString() is { Length: > 0 } p) roots.Add(PathUtil.Normalize(p)); }
            catch (JsonException) { }
            catch (InvalidOperationException) { }
        }

        var editors = roots.Select(ix.Find).Where(i => i >= 0).SelectMany(ix.Children).ToList();
        if (editors.Count == 0) yield break;

        var used = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        const DirMarkers unity = DirMarkers.UnityAssets | DirMarkers.UnityProjectSettings;
        for (var d = 0; d < ix.Count; d++)
        {
            if ((ix.Dir(d).Markers & unity) != unity) continue;
            var proj = ix.PathOf(d);
            if (DetectorUtil.ReadSmall(PathUtil.Join(proj, @"ProjectSettings\ProjectVersion.txt")) is { } txt
                && EditorVersionRx().Match(txt) is { Success: true } m)
                (used.TryGetValue(m.Groups[1].Value, out var l) ? l : used[m.Groups[1].Value] = []).Add(PathUtil.Name(proj));
        }

        foreach (var e in editors)
        {
            var version = ix.Dir(e).Name;
            if (used.ContainsKey(version)) continue;
            var branch = string.Join('.', version.Split('.').Take(2));
            var sameBranch = editors.Count(x => ix.Dir(x).Name.StartsWith(branch + ".", StringComparison.Ordinal));
            var f = FindingsEngine.NewFinding(rule, ix.PathOf(e), new() { ["version"] = version });
            f.Items.Add(FindingsEngine.DirItem(ix, e));
            f.Evidence.Add(used.Count == 0
                ? Lang.T("No Unity project with ProjectVersion.txt was found on this drive", "Bu sürücüde ProjectVersion.txt'si olan Unity projesi bulunamadı")
                : Lang.T($"None of your {used.Values.Sum(v => v.Count)} Unity projects uses it (they use: {string.Join(", ", used.Keys.Order())})",
                         $"{used.Values.Sum(v => v.Count)} Unity projenin hiçbiri kullanmıyor (kullanılanlar: {string.Join(", ", used.Keys.Order())})"));
            if (sameBranch > 1) f.Evidence.Add(Lang.T($"{sameBranch} patch versions of {branch} are installed", $"{branch} dalında {sameBranch} yama sürümü kurulu"));
            yield return f;
        }
    }
}

/// <summary>
/// Version folders/files under known tool locations where only the newest is needed
/// (e.g. %UserProfile%\.local\share\claude\versions). The locations are listed here, not in the rule file,
/// because each needs version-aware handling.
/// </summary>
public sealed class CliVersionsDetector : IDetector
{
    public string Name => "cli-versions";

    private static readonly string[] Locations = [@"%UserProfile%\.local\share\claude\versions"];

    public IEnumerable<Finding> Detect(DetectorContext ctx, Rule rule)
    {
        var ix = ctx.Index;
        foreach (var loc in Locations)
        {
            if (PathUtil.Expand(loc, ctx.Vars) is not { } path) continue;
            var d = ix.Find(path);
            if (d < 0) continue;
            var entries = new List<(string Path, string Name, long Bytes, DateTime? When, bool IsDir)>();
            foreach (var c in ix.Children(d)) entries.Add((ix.PathOf(c), ix.Dir(c).Name, ix.Dir(c).Size, ix.Dir(c).LastWriteUtc, true));
            foreach (var fi in ix.FilesIn(d))
            {
                var file = ix.Snapshot.Files[fi];
                if (!file.Name.Contains('\\')) entries.Add((ix.PathOf(file), file.Name, file.Size, file.LastWriteUtc, false));
            }
            var versioned = entries.Select(e => (e, V: DetectorUtil.VersionIn(e.Name))).Where(x => x.V is not null).OrderBy(x => x.V).ToList();
            if (versioned.Count < 2) continue;
            var newest = versioned[^1];
            var old = versioned.Take(versioned.Count - 1).ToList();
            var f = FindingsEngine.NewFinding(rule, path, new() { ["name"] = PathUtil.Name(PathUtil.Parent(path) ?? path), ["count"] = old.Count.ToString(Lang.Culture) });
            foreach (var (e, _) in old) f.Items.Add(new FindingItem { Path = e.Path, IsDir = e.IsDir, Bytes = e.Bytes, LastWriteUtc = e.When });
            f.Evidence.Add(Lang.T($"Newest stays: {newest.e.Name}", $"En yenisi kalıyor: {newest.e.Name}"));
            if (f.Items.Sum(i => i.Bytes) >= rule.MinBytes) yield return f;
        }
    }
}

/// <summary>Old Playwright browser builds: per browser, only the highest build number is kept.</summary>
public sealed class PlaywrightDetector : IDetector
{
    public string Name => "playwright";

    public IEnumerable<Finding> Detect(DetectorContext ctx, Rule rule)
    {
        var ix = ctx.Index;
        if (ctx.Var("LocalAppData") is not { } lad) yield break;
        var root = ix.Find(PathUtil.Join(lad, "ms-playwright"));
        if (root < 0) yield break;
        var builds = ix.Children(root)
            .Select(c => (Dir: c, Name: ix.Dir(c).Name, Dash: ix.Dir(c).Name.LastIndexOf('-')))
            .Where(x => x.Dash > 0 && int.TryParse(x.Name[(x.Dash + 1)..], out _))
            .GroupBy(x => x.Name[..x.Dash]);
        var f = FindingsEngine.NewFinding(rule, ix.PathOf(root));
        foreach (var g in builds)
        {
            var ordered = g.OrderBy(x => int.Parse(x.Name[(x.Dash + 1)..], Lang.Culture)).ToList();
            foreach (var x in ordered.Take(ordered.Count - 1)) f.Items.Add(FindingsEngine.DirItem(ix, x.Dir));
            if (ordered.Count > 1) f.Evidence.Add(Lang.T($"{g.Key}: keeping {ordered[^1].Name}", $"{g.Key}: {ordered[^1].Name} kalıyor"));
        }
        if (f.Items.Count > 0 && f.Items.Sum(i => i.Bytes) >= rule.MinBytes) yield return f;
    }
}

/// <summary>Several Windows SDKs installed: older ones, to uninstall (from Apps; no folder can be split per version).</summary>
public sealed class WindowsSdkDetector : IDetector
{
    public string Name => "windows-sdk";

    public IEnumerable<Finding> Detect(DetectorContext ctx, Rule rule)
    {
        var sdks = ctx.Snapshot.Apps
            .Where(a => a.Name.StartsWith("Windows Software Development Kit", StringComparison.OrdinalIgnoreCase))
            .Select(a => (App: a, V: DetectorUtil.VersionIn(a.Version ?? a.Name)))
            .Where(x => x.V is not null)
            .OrderBy(x => x.V)
            .ToList();
        if (sdks.Count < 2) yield break;
        var newest = sdks[^1];
        foreach (var (app, v) in sdks.Take(sdks.Count - 1))
        {
            var f = FindingsEngine.NewFinding(rule, "app:" + app.Name + "|" + app.Version, new() { ["version"] = v!.ToString() });
            f.Bytes = app.EstimatedBytes;
            f.Command = app.UninstallString;
            f.Evidence.Add(Lang.T($"Newer SDK installed: {newest.V}", $"Yeni SDK kurulu: {newest.V}"));
            f.Evidence.Add(Lang.T("Size is the installer's estimate", "Boyut, kurulumun tahmini"));
            yield return f;
        }
    }
}

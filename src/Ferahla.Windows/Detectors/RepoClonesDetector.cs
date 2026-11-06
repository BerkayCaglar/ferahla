using System.Text.RegularExpressions;
using Ferahla.Core;
using Ferahla.Core.Findings;
using Ferahla.Core.Model;
using Ferahla.Core.Rules;
using Ferahla.Core.Text;

namespace Ferahla.Windows.Detectors;

/// <summary>
/// Git working copies that point at the same remote. The copy with a plain name (no "_clone_0", " - Copy", "(2)")
/// is treated as the main one; if all names are plain, the most recently changed one is. The rest are review.
/// </summary>
public sealed partial class RepoClonesDetector : IDetector
{
    public string Name => "repo-clones";

    [GeneratedRegex(@"(_clone_\d+|[ _-]+copy( \d+)?|[ _-]*\(\d+\)|[ _-]+kopya( \d+)?)$", RegexOptions.IgnoreCase)]
    private static partial Regex CloneSuffixRx();

    [GeneratedRegex(@"\[remote ""origin""\][^\[]*?url\s*=\s*(\S+)", RegexOptions.Singleline)]
    private static partial Regex OriginRx();

    public IEnumerable<Finding> Detect(DetectorContext ctx, Rule rule)
    {
        if (!ctx.Live) yield break;
        var ix = ctx.Index;
        var repos = new List<(int Dir, string Path, string Remote)>();
        for (var d = 0; d < ix.Count; d++)
        {
            if ((ix.Dir(d).Markers & DirMarkers.Git) == 0) continue;
            var path = ix.PathOf(d);
            if (!ctx.IsUserArea(path)) continue;
            if (DetectorUtil.ReadSmall(PathUtil.Join(path, @".git\config")) is not { } cfg) continue; // worktrees/submodules: .git is a file
            if (OriginRx().Match(cfg) is not { Success: true } m) continue;
            repos.Add((d, path, NormalizeRemote(m.Groups[1].Value)));
        }

        var reported = new HashSet<int>();
        foreach (var f in ParrelSyncClones(ctx, rule, reported)) yield return f;

        foreach (var g in repos.Where(r => !reported.Contains(r.Dir)).GroupBy(r => r.Remote, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            var list = g.ToList();
            var main = list.Where(r => !CloneSuffixRx().IsMatch(PathUtil.Name(r.Path)))
                           .OrderByDescending(r => ix.Dir(r.Dir).LastWriteTicks).FirstOrDefault();
            if (main.Path is null) main = list.OrderByDescending(r => ix.Dir(r.Dir).LastWriteTicks).First();
            foreach (var r in list.Where(r => r.Dir != main.Dir))
            {
                var f = FindingsEngine.NewFinding(rule, r.Path, new() { ["name"] = PathUtil.Name(r.Path), ["main"] = PathUtil.Name(main.Path) });
                f.Items.Add(FindingsEngine.DirItem(ix, r.Dir));
                f.Evidence.Add(Lang.T($"Same remote as {PathUtil.Name(main.Path)}: {g.Key}", $"{PathUtil.Name(main.Path)} ile aynı remote: {g.Key}"));
                if (ix.Dir(r.Dir).LastWriteUtc is { } w)
                    f.Evidence.Add(Lang.T($"Last changed {DetectorUtil.Date(w)}", $"Son değişiklik {DetectorUtil.Date(w)}"));
                f.Evidence.Add(Lang.T("Check for uncommitted or unpushed work before removing", "Kaldırmadan önce commit'lenmemiş ya da push'lanmamış iş var mı bak"));
                yield return f;
            }
        }
    }

    [GeneratedRegex(@"^(?<base>.+)_clone_\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex ParrelSyncRx();

    /// <summary>
    /// ParrelSync (Unity multiplayer testing) clones: "&lt;project&gt;_clone_N" next to the project, with Assets linked to
    /// the original. Only the clone's own Library and settings take space; ParrelSync recreates the clone on demand.
    /// </summary>
    private static IEnumerable<Finding> ParrelSyncClones(DetectorContext ctx, Rule rule, HashSet<int> reported)
    {
        var ix = ctx.Index;
        const DirMarkers unity = DirMarkers.UnityAssets | DirMarkers.UnityProjectSettings;
        for (var d = 0; d < ix.Count; d++)
        {
            var e = ix.Dir(d);
            if ((e.Markers & unity) != unity || ParrelSyncRx().Match(e.Name) is not { Success: true } m) continue;
            var path = ix.PathOf(d);
            if (!ctx.IsUserArea(path) || PathUtil.Parent(path) is not { } parent) continue;
            var original = ix.Find(PathUtil.Join(parent, m.Groups["base"].Value));
            if (original < 0 || (ix.Dir(original).Markers & unity) != unity) continue;

            reported.Add(d);
            var f = FindingsEngine.NewFinding(rule, path, new() { ["name"] = e.Name, ["main"] = ix.Dir(original).Name });
            f.Items.Add(FindingsEngine.DirItem(ix, d));
            var linked = ctx.Live && (new DirectoryInfo(PathUtil.Join(path, "Assets")).Attributes & FileAttributes.ReparsePoint) != 0;
            f.Evidence.Add(linked
                ? Lang.T($"ParrelSync clone of {ix.Dir(original).Name}: its Assets folder is a link to the original", $"{ix.Dir(original).Name} projesinin ParrelSync klonu: Assets klasörü asıl projeye bağlı")
                : Lang.T($"Named like a ParrelSync clone of {ix.Dir(original).Name}", $"{ix.Dir(original).Name} projesinin ParrelSync klonu gibi adlandırılmış"));
            f.Evidence.Add(Lang.T("Remove it from ParrelSync → Clones Manager; it can be created again from there.", "ParrelSync → Clones Manager'dan kaldır; oradan yeniden oluşturulabilir."));
            yield return f;
        }
    }

    /// <summary>https://github.com/a/b.git, git@github.com:a/b → github.com/a/b</summary>
    internal static string NormalizeRemote(string url)
    {
        var u = url.Trim();
        if (u.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) u = u[..^4];
        u = Regex.Replace(u, @"^[a-z+]+://", "", RegexOptions.IgnoreCase);
        u = Regex.Replace(u, @"^[^@/]+@", "");
        u = u.Replace(':', '/');
        return u.TrimEnd('/').ToLowerInvariant();
    }
}

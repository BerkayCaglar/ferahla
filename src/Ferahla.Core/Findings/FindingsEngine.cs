using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ferahla.Core.Model;
using Ferahla.Core.Rules;
using Ferahla.Core.Text;

namespace Ferahla.Core.Findings;

/// <summary>A coded detector for rules whose match is <c>{"detector": "name"}</c>.</summary>
public interface IDetector
{
    string Name { get; }
    IEnumerable<Finding> Detect(DetectorContext ctx, Rule rule);
}

public sealed class DetectorContext(SnapshotIndex index, IReadOnlyDictionary<string, string> vars, SnapshotIndex? previous, bool live)
{
    public SnapshotIndex Index { get; } = index;
    public Snapshot Snapshot => Index.Snapshot;
    public IReadOnlyDictionary<string, string> Vars { get; } = vars;
    public SnapshotIndex? Previous { get; } = previous;
    /// <summary>Detectors may read the disk (zip listings, project files). False in tests that only use a snapshot.</summary>
    public bool Live { get; } = live;

    public string? Var(string name) => Vars.TryGetValue(name, out var v) ? v : null;

    private bool[]? _inProject;
    private static readonly HashSet<string> BuildDirNames = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", "target", "artifacts", "build", "builds", "dist", "out", "node_modules", "Library", "Temp", ".git" };

    /// <summary>
    /// True if the directory is, or is inside, a project (any marker: git, Unity, Cargo…) or a build output folder.
    /// Files there belong to the project (assets, build outputs), not to the user's loose downloads.
    /// </summary>
    public bool InProject(int dir)
    {
        if (_inProject is null)
        {
            var ix = Index;
            // A stray package.json in the profile (npm install run in ~) must not turn the whole profile into a
            // "project": markers on known folders and the drive root don't count.
            var containers = new HashSet<int> { 0 };
            foreach (var v in Vars.Values)
                if (ix.Find(v) is var k and >= 0) containers.Add(k);
            var a = new bool[ix.Count];
            for (var i = 0; i < ix.Count; i++) // parents come before children
            {
                var d = ix.Dir(i);
                a[i] = (d.Parent >= 0 && a[d.Parent])
                       || (d.Markers != DirMarkers.None && !containers.Contains(i))
                       || (d.Parent >= 0 && BuildDirNames.Contains(d.Name));
            }
            _inProject = a;
        }
        return _inProject[dir];
    }

    /// <summary>The user's own area: not system, program or app-data folders, not dot-folders in the profile.</summary>
    public bool IsUserArea(string path)
    {
        foreach (var k in (ReadOnlySpan<string>)["Windows", "ProgramFiles", "ProgramFilesX86", "ProgramData", "LocalAppData", "AppData"])
            if (Var(k) is { } p && PathUtil.IsUnder(path, p)) return false;
        if (Var("UserProfile") is { } profile && PathUtil.IsUnder(path, profile) && path.Length > profile.Length + 1)
        {
            var rest = path.AsSpan(profile.Length + 1);
            var first = rest.IndexOf('\\') is var i and >= 0 ? rest[..i] : rest;
            if (first.Length > 1 && first[0] == '.') return false;
        }
        return true;
    }
}

public sealed class FindingsEngine
{
    private readonly RuleSet _rules;
    private readonly Dictionary<string, IDetector> _detectors;

    public FindingsEngine(RuleSet rules, IEnumerable<IDetector>? detectors = null)
    {
        _rules = rules;
        _detectors = (detectors ?? []).ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);
    }

    public FindingsResult Run(Snapshot snapshot, Snapshot? previous = null, bool live = true)
    {
        var ix = new SnapshotIndex(snapshot);
        var prevIx = previous is null ? null : new SnapshotIndex(previous);
        var vars = snapshot.KnownFolders;
        var ctx = new DetectorContext(ix, vars, prevIx, live);
        var findings = new List<Finding>();
        var errors = _rules.Errors.ToList();

        foreach (var rule in _rules.Rules)
        {
            try
            {
                var m = rule.Match;
                if (m.Path is { Count: > 0 }) findings.AddRange(PathRule(ctx, rule));
                else if (m.Marker is { Count: > 0 }) findings.AddRange(MarkerRule(ctx, rule));
                else if (m.DirName is { Count: > 0 }) findings.AddRange(DirNameRule(ctx, rule));
                else if (m.Detector is { } d)
                {
                    if (_detectors.TryGetValue(d, out var det)) findings.AddRange(det.Detect(ctx, rule));
                    else errors.Add($"{rule.Id}: unknown detector '{d}'");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                errors.Add($"{rule.Id}: {ex.Message}");
            }
        }

        // Detectors that look at machine-wide places (SDKs, CLI versions) must not reach outside a folder scan.
        var root = PathUtil.Normalize(snapshot.Root);
        findings.RemoveAll(f =>
        {
            var had = f.Items.Count > 0;
            f.Items.RemoveAll(i => !PathUtil.IsUnder(i.Path, root));
            // item-less findings are about the machine (installed apps, hibernation, unseen space), not a folder
            return f.Items.Count == 0 && (had || root.Length > 3);
        });

        var notSuggested = new List<ProtectedNote>();
        findings = Guard(findings, ctx, notSuggested);
        Dedupe(findings);
        ResolveNesting(findings);
        if (prevIx is not null) Growth(findings, prevIx);
        AddStaticNotes(ix, vars, notSuggested);

        findings.Sort((a, b) => a.Risk != b.Risk ? a.Risk.CompareTo(b.Risk) : b.Bytes.CompareTo(a.Bytes));
        var result = new FindingsResult
        {
            ScanId = snapshot.Id,
            PreviousScanId = previous?.Id,
            ScannedAt = snapshot.ScannedAt,
            Root = snapshot.Root,
            TotalBytes = snapshot.Volume.TotalBytes,
            FreeBytes = snapshot.Volume.FreeBytes,
            Findings = findings,
            NotSuggested = notSuggested,
            RuleErrors = errors,
        };
        foreach (var g in findings.GroupBy(f => f.Risk)) result.BytesByRisk[g.Key] = g.Sum(f => f.Bytes);
        return result;
    }

    // ---- rule kinds ----

    private static IEnumerable<Finding> PathRule(DetectorContext ctx, Rule rule)
    {
        foreach (var pattern in rule.Match.Path!)
        {
            if (PathUtil.Expand(pattern, ctx.Vars) is not { } expanded) continue;
            foreach (var d in PathPattern.Match(ctx.Index, expanded))
            {
                var e = ctx.Index.Dir(d);
                if (e.Size < rule.MinBytes) continue;
                if (rule.Match.ExcludeNames is { } ex && ex.Any(n => PathPattern.Glob(e.Name, n))) continue;
                var path = ctx.Index.PathOf(d);
                var f = NewFinding(rule, path, new() { ["name"] = PathUtil.Name(path) });
                f.Items.Add(DirItem(ctx.Index, d));
                yield return f;
            }
        }
    }

    private static IEnumerable<Finding> MarkerRule(DetectorContext ctx, Rule rule)
    {
        var ix = ctx.Index;
        var required = DirMarkers.None;
        foreach (var mk in rule.Match.Marker!) required |= Enum.Parse<DirMarkers>(mk);
        var targets = rule.Match.Targets!;
        var claimed = new HashSet<int>();   // target dirs already suggested: never look for markers inside them

        for (var d = 0; d < ix.Count; d++)  // pre-order: parents before children
        {
            var e = ix.Dir(d);
            if ((e.Markers & required) != required) continue;
            if (IsInsideClaimed(ix, d, claimed)) continue;
            var path = ix.PathOf(d);
            if (!ctx.IsUserArea(path)) continue;

            var items = new List<FindingItem>();
            long projectLatest = 0;
            foreach (var c in ix.Children(d))
            {
                var name = ix.Dir(c).Name;
                if (targets.Any(t => PathPattern.Glob(name, t)))
                {
                    items.Add(DirItem(ix, c));
                    claimed.Add(c);
                }
                else if (ix.Dir(c).LastWriteTicks > projectLatest) projectLatest = ix.Dir(c).LastWriteTicks;
            }
            var bytes = items.Sum(i => i.Bytes);
            if (items.Count == 0 || bytes < rule.MinBytes) continue;

            var f = NewFinding(rule, path, new() { ["name"] = PathUtil.Name(path), ["targets"] = string.Join(", ", items.Select(i => PathUtil.Name(i.Path))) });
            f.Items.AddRange(items);
            f.Evidence.Add(Lang.T($"Project marker: {string.Join(" + ", rule.Match.Marker!)}", $"Proje işareti: {string.Join(" + ", rule.Match.Marker!)}"));
            if (projectLatest > 0)
            {
                var when = new DateTime(projectLatest, DateTimeKind.Utc).ToLocalTime().ToString("d", Lang.Culture);
                f.Evidence.Add(Lang.T($"Project last changed (outside build folders): {when}", $"Projenin son değişikliği (build klasörleri hariç): {when}"));
            }
            yield return f;
        }
    }

    private static IEnumerable<Finding> DirNameRule(DetectorContext ctx, Rule rule)
    {
        var ix = ctx.Index;
        var roots = (rule.Match.Under ?? ["%UserProfile%"]).Select(u => PathUtil.Expand(u, ctx.Vars)).OfType<string>().ToList();
        var claimed = new HashSet<int>();
        for (var d = 0; d < ix.Count; d++)
        {
            var e = ix.Dir(d);
            if (!rule.Match.DirName!.Any(n => PathPattern.Glob(e.Name, n))) continue;
            if (e.Size < rule.MinBytes || IsInsideClaimed(ix, d, claimed)) continue;
            var path = ix.PathOf(d);
            if (!roots.Any(r => PathUtil.IsUnder(path, r))) continue;
            claimed.Add(d);
            var parent = PathUtil.Parent(path) ?? path;
            var f = NewFinding(rule, path, new() { ["name"] = PathUtil.Name(path), ["parent"] = PathUtil.Name(parent) });
            f.Items.Add(DirItem(ix, d));
            f.Evidence.Add(Lang.T($"Folder name: {e.Name}", $"Klasör adı: {e.Name}"));
            yield return f;
        }
    }

    private static bool IsInsideClaimed(SnapshotIndex ix, int d, HashSet<int> claimed)
    {
        if (claimed.Count == 0) return false;
        for (var p = ix.Dir(d).Parent; p >= 0; p = ix.Dir(p).Parent)
            if (claimed.Contains(p)) return true;
        return claimed.Contains(d);
    }

    // ---- shared helpers (also used by detectors) ----

    public static Finding NewFinding(Rule rule, string primaryPath, Dictionary<string, string>? args = null)
    {
        args ??= [];
        return new Finding
        {
            Id = StableId(rule.Id, primaryPath),
            RuleId = rule.Id,
            Category = rule.Category,
            Risk = rule.Risk,
            Action = rule.Action,
            Command = rule.Command,
            Title = rule.Title.Resolve(args),
            Reason = rule.Reason?.Resolve(args),
            WhatBreaks = rule.WhatBreaks?.Resolve(args),
            HowToRestore = rule.HowToRestore?.Resolve(args),
            KeepRecentHours = rule.KeepRecentHours,
        };
    }

    public static FindingItem DirItem(SnapshotIndex ix, int d) => new()
    {
        Path = ix.PathOf(d),
        IsDir = true,
        Bytes = ix.Dir(d).Size,
        LastWriteUtc = ix.Dir(d).LastWriteUtc,
    };

    public static FindingItem FileItem(SnapshotIndex ix, FileEntry f) => new()
    {
        Path = ix.PathOf(f),
        IsDir = false,
        Bytes = f.Size,
        LastWriteUtc = f.LastWriteUtc,
    };

    public static string StableId(string ruleId, string path)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(ruleId + "|" + PathUtil.Normalize(path).ToUpperInvariant()));
        return "f-" + Convert.ToHexStringLower(bytes.AsSpan(0, 5));
    }

    // ---- post-processing ----

    /// <summary>Drops items under protected locations; quarantine is never suggested inside Program Files.</summary>
    private static List<Finding> Guard(List<Finding> findings, DetectorContext ctx, List<ProtectedNote> notes)
    {
        var never = new List<string>();
        if (ctx.Var("Windows") is { } win) never.Add(win);
        if (ctx.Var("SystemDrive") is { } sd)
        {
            never.Add(PathUtil.Join(sd, "System Volume Information"));
            never.Add(PathUtil.Join(sd, "pagefile.sys"));
            never.Add(PathUtil.Join(sd, "swapfile.sys"));
        }
        var noMove = new List<string>();
        foreach (var k in (ReadOnlySpan<string>)["ProgramFiles", "ProgramFilesX86"])
            if (ctx.Var(k) is { } p) noMove.Add(p);

        var kept = new List<Finding>();
        foreach (var f in findings)
        {
            // A detector can decide something must not be suggested at all (e.g. a VM disk that is in use).
            if (f.Risk == Risk.Protected)
            {
                foreach (var i in f.Items)
                    notes.Add(new ProtectedNote { Path = i.Path, Bytes = i.Bytes, Reason = f.Evidence.FirstOrDefault() ?? f.Title });
                continue;
            }
            // Info and command findings don't touch files themselves (e.g. "run DISM" about WinSxS), so only
            // findings that move or remove files are checked.
            var touchesFiles = f.Action is ActionKind.Quarantine or ActionKind.Uninstall or ActionKind.Launcher;
            var hadItems = f.Items.Count > 0;
            if (touchesFiles)
                f.Items.RemoveAll(i =>
                {
                    var blocked = never.Any(n => PathUtil.IsUnder(i.Path, n))
                                  || (f.Action == ActionKind.Quarantine && noMove.Any(n => PathUtil.IsUnder(i.Path, n)));
                    if (blocked) notes.Add(new ProtectedNote { Path = i.Path, Bytes = i.Bytes, Reason = Lang.T($"Protected location (rule {f.RuleId})", $"Korumalı konum (kural {f.RuleId})") });
                    return blocked;
                });
            if (f.Items.Count > 0 || !hadItems) kept.Add(f);
        }
        return kept;
    }

    /// <summary>Two rules can match the same folder; keep the first (rules are ordered by file, then position).</summary>
    private static void Dedupe(List<Finding> findings)
    {
        var owner = new Dictionary<string, Finding>(StringComparer.OrdinalIgnoreCase);
        var emptied = new HashSet<Finding>();
        foreach (var f in findings)
        {
            if (f.Items.Count == 0) continue; // item-less info findings are kept as they are
            f.Items.RemoveAll(i => !owner.TryAdd(i.Path, f) && owner[i.Path] != f);
            if (f.Items.Count == 0) emptied.Add(f);
        }
        findings.RemoveAll(emptied.Contains);
    }

    private static void ResolveNesting(List<Finding> findings)
    {
        var dirOwner = new Dictionary<string, Finding>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in findings)
            foreach (var i in f.Items.Where(i => i.IsDir))
                dirOwner[PathUtil.Normalize(i.Path)] = f;

        foreach (var f in findings)
        {
            foreach (var i in f.Items)
            {
                for (var p = PathUtil.Parent(i.Path); p is not null; p = PathUtil.Parent(p))
                    if (dirOwner.TryGetValue(p, out var outer) && outer != f) { i.NestedIn = outer.Id; break; }
            }
            if (f.Items.Count == 0) continue; // info findings set their own Bytes
            f.Bytes = f.Items.Where(i => i.NestedIn is null).Sum(i => i.Bytes);
            f.LastWriteUtc = f.Items.Max(i => i.LastWriteUtc);
        }
        // A finding that lies entirely inside another one adds nothing (e.g. Gradle projects Unity generates
        // inside Library/): acting on the outer one covers it.
        findings.RemoveAll(f => f.Items.Count > 0 && f.Items.All(i => i.NestedIn is not null));
    }

    private static void Growth(List<Finding> findings, SnapshotIndex prev)
    {
        foreach (var f in findings)
        {
            long before = 0;
            var anyBefore = false;
            foreach (var i in f.Items.Where(i => i.NestedIn is null))
            {
                if (i.IsDir)
                {
                    var d = prev.Find(i.Path);
                    if (d >= 0) { before += prev.Dir(d).Size; anyBefore = true; }
                }
                else
                {
                    var d = prev.Find(PathUtil.Parent(i.Path) ?? "");
                    var name = PathUtil.Name(i.Path);
                    if (d >= 0 && prev.FilesIn(d).Select(x => prev.Snapshot.Files[x]).FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)) is { } pf)
                    { before += pf.Size; anyBefore = true; }
                }
            }
            f.GrowthBytes = f.Bytes - before;
            f.IsNew = !anyBefore;
        }
    }

    private static void AddStaticNotes(SnapshotIndex ix, IReadOnlyDictionary<string, string> vars, List<ProtectedNote> notes)
    {
        if (vars.TryGetValue("Windows", out var win) && ix.Find(win) is var w and >= 0)
            notes.Add(new ProtectedNote { Path = win, Bytes = ix.Dir(w).Size, Reason = Lang.T("Windows itself; clean it only with Windows tools (see system findings)", "Windows'un kendisi; sadece Windows araçlarıyla temizlenir (bkz. sistem bulguları)") });
        foreach (var fi in ix.FilesIn(0))
        {
            var f = ix.Snapshot.Files[fi];
            if (f.Name.Equals("pagefile.sys", StringComparison.OrdinalIgnoreCase) || f.Name.Equals("swapfile.sys", StringComparison.OrdinalIgnoreCase))
                notes.Add(new ProtectedNote { Path = ix.PathOf(f), Bytes = f.Size, Reason = Lang.T("Virtual memory; Windows manages it", "Sanal bellek; Windows yönetir") });
        }
    }
}

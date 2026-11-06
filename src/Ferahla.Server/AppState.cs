using Ferahla.Core;
using Ferahla.Core.Diff;
using Ferahla.Core.Findings;
using Ferahla.Core.Model;
using Ferahla.Core.Rules;
using Ferahla.Core.Storage;
using Ferahla.Core.Text;
using Ferahla.Windows;

namespace Ferahla.Server;

/// <summary>
/// Loaded scans and computed findings, cached: loading a full-drive snapshot takes about a second and the findings
/// (detectors read the disk) a little less. Findings are filtered by what still exists, so items that went to
/// quarantine or were deleted since the scan drop out without a rescan.
/// </summary>
internal sealed class AppState(SnapshotStore store)
{
    private readonly Lock _lock = new();
    private (Snapshot Snap, SnapshotIndex Index)? _scan;
    private readonly Dictionary<string, FindingsResult> _findings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DiffResult?> _diffs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Snapshot?> _previous = new(StringComparer.Ordinal);

    public SnapshotStore Store { get; } = store;

    /// <exception cref="FileNotFoundException">No such scan, or no scans at all.</exception>
    public (Snapshot Snap, SnapshotIndex Index) Scan(string? id)
    {
        id = string.IsNullOrEmpty(id) ? Store.Latest() : id;
        if (id is null) throw new FileNotFoundException(Lang.T("No scans yet", "Henüz tarama yok"));
        if (!id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) throw new FileNotFoundException(Lang.T($"No such scan: {id}", $"Böyle bir tarama yok: {id}"));
        lock (_lock)
            if (_scan is { } s && s.Snap.Id == id) return s;
        var snap = Store.Load(id);
        var loaded = (snap, new SnapshotIndex(snap));
        lock (_lock) _scan = loaded;
        return loaded;
    }

    private Snapshot? Previous(Snapshot snap)
    {
        lock (_lock)
            if (_previous.TryGetValue(snap.Id, out var p)) return p;
        var prev = FindingsService.Previous(Store, snap);
        lock (_lock) _previous[snap.Id] = prev;
        return prev;
    }

    public FindingsResult Findings(string? id)
    {
        var (snap, _) = Scan(id);
        var key = snap.Id + "|" + Lang.Current;
        lock (_lock)
            if (_findings.TryGetValue(key, out var cached)) return cached;
        var result = FindingsService.Compute(snap, Previous(snap));
        DropVanished(result);
        lock (_lock) _findings[key] = result;
        return result;
    }

    public DiffResult? Diff(string? id)
    {
        var (snap, _) = Scan(id);
        lock (_lock)
            if (_diffs.TryGetValue(snap.Id, out var cached)) return cached;
        var prev = Previous(snap);
        var diff = prev is null ? null : SnapshotDiff.Compare(prev, snap, threshold: 200L << 20);
        lock (_lock) _diffs[snap.Id] = diff;
        return diff;
    }

    /// <summary>After moves, deletes or a language change: findings are computed again on the next request.</summary>
    public void InvalidateFindings()
    {
        lock (_lock) _findings.Clear();
    }

    /// <summary>After a new scan (it is the new "previous" of nothing, but the latest changes).</summary>
    public void InvalidateAll()
    {
        lock (_lock) { _findings.Clear(); _diffs.Clear(); _previous.Clear(); }
    }

    private static void DropVanished(FindingsResult r)
    {
        foreach (var f in r.Findings)
        {
            if (f.Items.Count == 0 || f.Action is ActionKind.Info or ActionKind.Command) continue;
            var before = f.Items.Count;
            f.Items.RemoveAll(i => !(i.IsDir ? Directory.Exists(i.Path) : File.Exists(i.Path)));
            if (f.Items.Count == before) continue;
            f.Bytes = f.Items.Where(i => i.NestedIn is null).Sum(i => i.Bytes);
        }
        r.Findings.RemoveAll(f => f.Items.Count == 0 && f.Action is not (ActionKind.Info or ActionKind.Command) && f.Bytes == 0);
        r.BytesByRisk.Clear();
        foreach (var g in r.Findings.GroupBy(f => f.Risk)) r.BytesByRisk[g.Key] = g.Sum(f => f.Bytes);
    }

    /// <summary>Paths that are inside a finding item, for the explorer's "suggested" bars.</summary>
    public static long SuggestedUnder(FindingsResult r, string path)
    {
        long sum = 0;
        foreach (var f in r.Findings)
        {
            if (f.Action is ActionKind.Info) continue;
            foreach (var i in f.Items)
                if (i.NestedIn is null && PathUtil.IsUnder(i.Path, path)) sum += i.Bytes;
        }
        return sum;
    }
}

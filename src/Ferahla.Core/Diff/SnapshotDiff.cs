using Ferahla.Core.Cli;
using Ferahla.Core.Model;

namespace Ferahla.Core.Diff;

public sealed class DiffResult
{
    public int Schema { get; set; } = OutputSchema.Version;
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public DateTimeOffset FromAt { get; set; }
    public DateTimeOffset ToAt { get; set; }
    public string Root { get; set; } = "";
    public long FreeBytesDelta { get; set; }
    public long ScannedBytesDelta { get; set; }
    public long Threshold { get; set; }
    public List<DiffEntry> Entries { get; set; } = [];
}

public sealed class DiffEntry
{
    public string Path { get; set; } = "";
    public long Before { get; set; }
    public long After { get; set; }
    public long Delta { get; set; }
    /// <summary>Not in the older scan (or too small to be kept there).</summary>
    public bool IsNew { get; set; }
    /// <summary>Not in the newer scan (or shrank below the kept size).</summary>
    public bool IsGone { get; set; }
}

public static class SnapshotDiff
{
    /// <summary>
    /// Directory-level change between two scans of the same root. Only folders whose change isn't mostly explained by
    /// one child are listed, so growth shows up once, at the deepest folder that accounts for it.
    /// </summary>
    public static DiffResult Compare(Snapshot from, Snapshot to, long threshold = 500L << 20, double explainedShare = 0.8)
    {
        var a = Sizes(from);
        var b = Sizes(to);
        var paths = new HashSet<string>(a.Keys, StringComparer.OrdinalIgnoreCase);
        paths.UnionWith(b.Keys);

        var delta = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var children = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in paths)
        {
            delta[p] = b.GetValueOrDefault(p) - a.GetValueOrDefault(p);
            if (PathUtil.Parent(p) is { } parent && paths.Contains(parent))
                (children.TryGetValue(parent, out var l) ? l : children[parent] = []).Add(p);
        }

        var result = new DiffResult
        {
            From = from.Id, To = to.Id, FromAt = from.ScannedAt, ToAt = to.ScannedAt,
            Root = to.Root, Threshold = threshold,
            FreeBytesDelta = to.Volume.FreeBytes - from.Volume.FreeBytes,
            ScannedBytesDelta = to.Stats.ScannedBytes - from.Stats.ScannedBytes,
        };
        foreach (var p in paths)
        {
            var d = delta[p];
            if (Math.Abs(d) < threshold) continue;
            // Explained by a child moving the same way?
            if (children.TryGetValue(p, out var kids) && kids.Any(k => Math.Sign(delta[k]) == Math.Sign(d) && Math.Abs(delta[k]) >= explainedShare * Math.Abs(d)))
                continue;
            result.Entries.Add(new DiffEntry
            {
                Path = p, Before = a.GetValueOrDefault(p), After = b.GetValueOrDefault(p), Delta = d,
                IsNew = !a.ContainsKey(p), IsGone = !b.ContainsKey(p),
            });
        }
        result.Entries.Sort((x, y) => Math.Abs(y.Delta).CompareTo(Math.Abs(x.Delta)));
        return result;
    }

    private static Dictionary<string, long> Sizes(Snapshot s)
    {
        var ix = new SnapshotIndex(s);
        var d = new Dictionary<string, long>(ix.Count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < ix.Count; i++) d[ix.PathOf(i)] = ix.Dir(i).Size;
        return d;
    }
}

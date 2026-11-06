using Ferahla.Core.Model;

namespace Ferahla.Core.Findings;

/// <summary>
/// Matches an expanded pattern (<c>C:\Users\user\AppData\Local\*\cache</c>) against the directories kept in a snapshot.
/// '*' (or any segment containing '*') matches one segment by wildcard; '**' matches zero or more segments.
/// </summary>
public static class PathPattern
{
    public static IEnumerable<int> Match(SnapshotIndex ix, string expanded)
    {
        var normalized = PathUtil.Normalize(expanded);
        var firstWild = normalized.IndexOf('*');
        if (firstWild < 0)
        {
            var i = ix.Find(normalized);
            if (i >= 0) yield return i;
            yield break;
        }
        // Literal prefix up to the last separator before the first wildcard
        var cut = normalized.LastIndexOf('\\', firstWild);
        var prefix = normalized[..cut];
        if (prefix.Length == 2) prefix += "\\";
        var start = ix.Find(prefix);
        if (start < 0) yield break;
        var segments = normalized[(cut + 1)..].Split('\\');
        var seen = new HashSet<int>();
        foreach (var m in Walk(ix, start, segments, 0))
            if (seen.Add(m)) yield return m;
    }

    private static IEnumerable<int> Walk(SnapshotIndex ix, int dir, string[] segs, int s)
    {
        if (s == segs.Length) { yield return dir; yield break; }
        var seg = segs[s];
        if (seg == "**")
        {
            foreach (var m in Walk(ix, dir, segs, s + 1)) yield return m;
            foreach (var c in ix.Children(dir))
                foreach (var m in Walk(ix, c, segs, s)) yield return m;
            yield break;
        }
        foreach (var c in ix.Children(dir))
            if (Glob(LastSegment(ix.Dir(c).Name), seg))
                foreach (var m in Walk(ix, c, segs, s + 1)) yield return m;
    }

    /// <summary>A kept directory's name can be "a\b" when intermediate dirs were folded; compare its last part.</summary>
    private static string LastSegment(string name)
    {
        var i = name.LastIndexOf('\\');
        return i < 0 ? name : name[(i + 1)..];
    }

    /// <summary>Case-insensitive glob with '*' and '?'.</summary>
    public static bool Glob(string text, string pattern)
    {
        if (!pattern.Contains('*') && !pattern.Contains('?')) return string.Equals(text, pattern, StringComparison.OrdinalIgnoreCase);
        return System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, text, ignoreCase: true);
    }
}

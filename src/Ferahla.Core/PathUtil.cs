namespace Ferahla.Core;

/// <summary>Windows path helpers that work on plain strings (no file system access).</summary>
public static class PathUtil
{
    /// <summary>Backslashes, no trailing separator except for a drive root (<c>C:\</c>).</summary>
    public static string Normalize(string path)
    {
        var p = path.Replace('/', '\\');
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal) && !p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            p = p[4..];
        // "%SystemDrive%\x" expands to "C:\\x": collapse repeated separators (keeping a leading UNC "\\").
        if (p.Contains(@"\\", StringComparison.Ordinal))
        {
            var unc = p.StartsWith(@"\\", StringComparison.Ordinal);
            var body = unc ? p[2..] : p;
            while (body.Contains(@"\\", StringComparison.Ordinal)) body = body.Replace(@"\\", @"\", StringComparison.Ordinal);
            p = unc ? @"\\" + body : body;
        }
        if (p.Length == 2 && p[1] == ':') return char.ToUpperInvariant(p[0]) + @":\";
        if (p.Length >= 2 && p[1] == ':') p = char.ToUpperInvariant(p[0]) + p[1..];
        while (p.Length > 3 && p.EndsWith('\\')) p = p[..^1];
        return p;
    }

    public static string Join(string dir, string name) =>
        dir.EndsWith('\\') ? dir + name : dir + "\\" + name;

    /// <summary>Parent path, or null for a root.</summary>
    public static string? Parent(string path)
    {
        var p = Normalize(path);
        if (p.Length <= 3) return null;
        var i = p.LastIndexOf('\\');
        if (i < 0) return null;
        return i == 2 ? p[..3] : p[..i];
    }

    public static string Name(string path)
    {
        var p = Normalize(path);
        var i = p.LastIndexOf('\\');
        return i < 0 || p.Length <= 3 ? p : p[(i + 1)..];
    }

    /// <summary>True if <paramref name="path"/> equals <paramref name="ancestor"/> or is inside it.</summary>
    public static bool IsUnder(string path, string ancestor)
    {
        var p = Normalize(path);
        var a = Normalize(ancestor);
        if (p.Length < a.Length) return false;
        if (!p.StartsWith(a, StringComparison.OrdinalIgnoreCase)) return false;
        return p.Length == a.Length || a.EndsWith('\\') || p[a.Length] == '\\';
    }

    /// <summary>Expands <c>%Name%</c> tokens from <paramref name="vars"/>; returns null if any token is unknown.</summary>
    public static string? Expand(string pattern, IReadOnlyDictionary<string, string> vars)
    {
        var sb = new System.Text.StringBuilder();
        var i = 0;
        while (i < pattern.Length)
        {
            var start = pattern.IndexOf('%', i);
            if (start < 0) { sb.Append(pattern, i, pattern.Length - i); break; }
            var end = pattern.IndexOf('%', start + 1);
            if (end < 0) { sb.Append(pattern, i, pattern.Length - i); break; }
            sb.Append(pattern, i, start - i);
            var key = pattern[(start + 1)..end];
            if (!vars.TryGetValue(key, out var value)) return null;
            sb.Append(value);
            i = end + 1;
        }
        return Normalize(sb.ToString());
    }
}

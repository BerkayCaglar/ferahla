using Ferahla.Core.Text;

namespace Ferahla.Core.Quarantine;

/// <summary>
/// The last check before a path is moved, independent of rules and findings: whatever a plan says, these paths
/// never move. Pure string logic (no disk access) so it is fully testable.
/// </summary>
public sealed class MoveGuard
{
    private readonly List<string> _never = [];      // the path and everything under it
    private readonly List<string> _containers = []; // the path itself and its ancestors (things inside are fine)

    /// <param name="knownFolders">Known folders as in <c>Snapshot.KnownFolders</c> (UserProfile, Windows, Temp…).</param>
    /// <param name="ferahlaHome">Ferahla's data folder; it holds the quarantine itself.</param>
    public MoveGuard(IReadOnlyDictionary<string, string> knownFolders, string ferahlaHome)
    {
        foreach (var k in (ReadOnlySpan<string>)["Windows", "ProgramFiles", "ProgramFilesX86"])
            if (knownFolders.TryGetValue(k, out var p)) _never.Add(PathUtil.Normalize(p));
        _never.Add(PathUtil.Normalize(ferahlaHome));

        // Folders Windows and apps expect to exist. Their contents can be cleaned; they themselves never move.
        foreach (var v in knownFolders.Values) _containers.Add(PathUtil.Normalize(v));
        if (knownFolders.TryGetValue("UserProfile", out var profile) && PathUtil.Parent(profile) is { } users)
        {
            _containers.Add(PathUtil.Join(profile, @"AppData\LocalLow"));
            _containers.Add(PathUtil.Join(profile, "AppData"));
            _containers.Add(users);
        }
    }

    /// <summary>Null if the path may be moved, otherwise the reason it may not.</summary>
    public string? Check(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Lang.T("Empty path", "Boş yol");
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            return Lang.T("Network and device paths are not quarantined", "Ağ ve aygıt yolları karantinaya alınmaz");
        if (path.Length < 4 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\')
            return Lang.T("Not a full local path", "Tam bir yerel yol değil");
        if (path.AsSpan().IndexOfAny("*?\"<>|") >= 0 || path.IndexOf(':', 2) >= 0)
            return Lang.T("Invalid characters in path", "Yolda geçersiz karakter");
        foreach (var seg in path.Split('\\'))
            if (seg is "." or ".." || (seg.Length > 0 && (seg.EndsWith(' ') || seg.EndsWith('.'))))
                return Lang.T("Path is not in canonical form", "Yol standart biçimde değil");

        var n = PathUtil.Normalize(path);
        if (n.Length <= 3) return Lang.T("A drive root never moves", "Sürücü kökü taşınmaz");
        var rest = n[3..];
        var first = rest.Split('\\')[0];
        if (first.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)
            || first.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)
            || first.Equals("Recovery", StringComparison.OrdinalIgnoreCase)
            || first.Equals("Boot", StringComparison.OrdinalIgnoreCase)
            || first.Equals(".ferahla", StringComparison.OrdinalIgnoreCase)
            || (!rest.Contains('\\') && (first.Equals("pagefile.sys", StringComparison.OrdinalIgnoreCase)
                                         || first.Equals("swapfile.sys", StringComparison.OrdinalIgnoreCase)
                                         || first.Equals("hiberfil.sys", StringComparison.OrdinalIgnoreCase))))
            return Lang.T("System location", "Sistem konumu");

        foreach (var p in _never)
            if (PathUtil.IsUnder(n, p) || PathUtil.IsUnder(p, n)) return Lang.T("Protected location", "Korumalı konum");
        foreach (var c in _containers)
            if (PathUtil.IsUnder(c, n)) return Lang.T("A system or profile folder itself never moves, only its contents", "Sistem ya da profil klasörünün kendisi taşınmaz, sadece içindekiler");
        return null;
    }
}

using System.Text;
using System.Text.RegularExpressions;
using Ferahla.Core;
using Ferahla.Core.Findings;
using Ferahla.Core.Model;

namespace Ferahla.Windows.Detectors;

internal static partial class DetectorUtil
{
    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "setup", "installer", "install", "x64", "x86", "amd64", "arm64", "win64", "win32", "win", "windows", "64bit", "32bit",
        "64", "32", "bit", "full", "offline", "online", "web", "portable", "release", "stable", "latest", "final", "msi", "exe",
        "x64setup", "user", "system", "en", "us", "tr", "multi", "multilang", "signed", "version", "v", "build", "update",
    };

    // Not preceded by a letter or digit, so the "64" in "win64_3.1.0" or "x64_2_4_8" is not taken as a version.
    [GeneratedRegex(@"(?<![\dA-Za-z])v?(\d+(?:[._]\d+){1,3})(?!\d)")]
    private static partial Regex VersionRx();

    [GeneratedRegex(@"[\s_\-.()\[\]+]+")]
    private static partial Regex SplitRx();

    /// <summary>"tunebox_win64_3.1.0.exe" → "tunebox"; "Install_mixdeck_x64_2_4_8.exe" → "mixdeck".</summary>
    public static string ProductKey(string fileName) => Key(Path.GetFileNameWithoutExtension(fileName));

    /// <summary>An app's display name the same way: "Tunebox Studio 3" → "tuneboxstudio".</summary>
    public static string AppKey(string displayName) => Key(displayName);

    // Split on separators first, then drop numbers, versions (v22) and noise words, so "x64_2_4_8" leaves nothing behind.
    private static string Key(string text)
    {
        var sb = new StringBuilder();
        foreach (var tok in SplitRx().Split(text))
        {
            if (tok.Length == 0 || Noise.Contains(tok) || tok.All(char.IsAsciiDigit) || IsVersionToken(tok)) continue;
            foreach (var ch in tok) if (char.IsLetter(ch)) sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    private static bool IsVersionToken(string tok) =>
        tok.Length > 1 && (tok[0] is 'v' or 'V') && tok.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0;

    public static Version? VersionIn(string text)
    {
        var m = VersionRx().Match(text);
        if (!m.Success) return null;
        var parts = m.Groups[1].Value.Replace('_', '.').Split('.');
        if (parts.Length > 4) parts = parts[..4];
        return Version.TryParse(string.Join('.', parts.Length == 1 ? [parts[0], "0"] : parts), out var v) ? v : null;
    }

    /// <summary>True if an installer key and an app key name the same product.</summary>
    public static bool SameProduct(string installerKey, string appKey)
    {
        if (installerKey.Length < 3 || appKey.Length < 3) return false;
        if (installerKey == appKey) return true;
        var shorter = installerKey.Length <= appKey.Length ? installerKey : appKey;
        var longer = ReferenceEquals(shorter, installerKey) ? appKey : installerKey;
        return shorter.Length >= 4 && longer.StartsWith(shorter, StringComparison.Ordinal);
    }

    public static string Title(string key, string? fallbackName)
    {
        if (fallbackName is { Length: > 0 }) return fallbackName;
        return key.Length == 0 ? "?" : char.ToUpperInvariant(key[0]) + key[1..];
    }

    public static IEnumerable<FileEntry> FilesWith(Snapshot s, FileFlags flag) => s.Files.Where(f => (f.Flags & flag) != 0);

    public static string Date(DateTime utc) => utc.ToLocalTime().ToString("d", Core.Text.Lang.Culture);

    public static string? ReadSmall(string path, int maxBytes = 64 * 1024)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length > maxBytes) return null;
            return File.ReadAllText(path);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>True if another process has the file open without sharing (e.g. a running VM's disk).</summary>
    public static bool IsInUse(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public static Finding WithItem(this Finding f, FindingItem item) { f.Items.Add(item); return f; }

    public static FindingItem FileItem(string path, long bytes, DateTime? lastWriteUtc) => new() { Path = path, IsDir = false, Bytes = bytes, LastWriteUtc = lastWriteUtc };

    public static bool IsUnderAny(string path, IEnumerable<string?> roots) => roots.Any(r => r is not null && PathUtil.IsUnder(path, r));
}

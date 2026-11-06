using System.IO.Compression;
using System.Text.Json;
using Ferahla.Core.Json;
using Ferahla.Core.Model;

namespace Ferahla.Core.Storage;

/// <summary>
/// Stores snapshots as gzip-compressed JSON under <c>&lt;home&gt;\scans</c>.
/// Home is <c>%FERAHLA_HOME%</c> if set (tests), otherwise <c>%LOCALAPPDATA%\Ferahla</c>.
/// </summary>
public sealed class SnapshotStore(string home)
{
    public const string Extension = ".scan.json.gz";

    public string Home { get; } = home;
    public string ScansDir => Path.Combine(Home, "scans");

    public static string DefaultHome() =>
        Environment.GetEnvironmentVariable("FERAHLA_HOME") is { Length: > 0 } h
            ? h
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ferahla");

    public static SnapshotStore Default() => new(DefaultHome());

    public string Save(Snapshot snapshot)
    {
        Directory.CreateDirectory(ScansDir);
        var path = Path.Combine(ScansDir, snapshot.Id + Extension);
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
        using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
            JsonSerializer.Serialize(gz, snapshot, FerahlaJson.Default.Snapshot);
        File.Move(tmp, path, overwrite: true);
        WriteMeta(snapshot);
        return path;
    }

    public Snapshot Load(string idOrPath)
    {
        var path = File.Exists(idOrPath) ? idOrPath : Path.Combine(ScansDir, idOrPath + Extension);
        using var fs = File.OpenRead(path);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        var s = JsonSerializer.Deserialize(gz, FerahlaJson.Default.Snapshot)
                ?? throw new InvalidDataException($"Empty snapshot: {path}");
        if (s.Schema > Snapshot.CurrentSchema)
            throw new InvalidDataException($"Snapshot schema {s.Schema} is newer than supported ({Snapshot.CurrentSchema}). Update Ferahla.");
        return s;
    }

    /// <summary>Snapshot ids, newest first. Ids sort chronologically.</summary>
    public IReadOnlyList<string> List(string? root = null)
    {
        if (!Directory.Exists(ScansDir)) return [];
        var ids = Directory.EnumerateFiles(ScansDir, "*" + Extension)
            .Select(f => Path.GetFileName(f)[..^Extension.Length])
            .Where(id => root is null || id.EndsWith("-" + RootTag(root), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(id => id, StringComparer.Ordinal)
            .ToList();
        return ids;
    }

    public string? Latest(string? root = null) => List(root).FirstOrDefault();

    public const string MetaExtension = ".meta.json";

    public static Cli.ScanListItem Meta(Snapshot s) => new()
    {
        Id = s.Id, Root = s.Root, ScannedAt = s.ScannedAt, FreeBytes = s.Volume.FreeBytes, TotalBytes = s.Volume.TotalBytes,
        ScannedBytes = s.Stats.ScannedBytes, Files = s.Stats.Files, ElapsedSeconds = s.ElapsedSeconds,
    };

    private void WriteMeta(Snapshot s) =>
        File.WriteAllBytes(Path.Combine(ScansDir, s.Id + MetaExtension), JsonSerializer.SerializeToUtf8Bytes(Meta(s), FerahlaJson.Default.ScanListItem));

    /// <summary>
    /// Summaries of the saved scans, newest first, without loading the snapshots (a small sidecar per scan;
    /// older scans without one are loaded once and get it). Unreadable scans are left out.
    /// </summary>
    public List<Cli.ScanListItem> ListMeta(string? root = null)
    {
        var list = new List<Cli.ScanListItem>();
        foreach (var id in List(root))
        {
            var meta = Path.Combine(ScansDir, id + MetaExtension);
            try
            {
                if (File.Exists(meta) && JsonSerializer.Deserialize(File.ReadAllBytes(meta), FerahlaJson.Default.ScanListItem) is { } m)
                {
                    list.Add(m);
                    continue;
                }
                var s = Load(id);
                try { WriteMeta(s); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                list.Add(Meta(s));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException) { }
        }
        return list;
    }

    /// <summary>Removes a saved scan and its sidecar.</summary>
    public bool Delete(string id)
    {
        if (id.Length == 0 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) return false;
        var path = Path.Combine(ScansDir, id + Extension);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        File.Delete(Path.Combine(ScansDir, id + MetaExtension));
        return true;
    }

    /// <summary>Id like <c>20261106T203000Z-C</c>.</summary>
    public static string NewId(DateTimeOffset at, string root) =>
        at.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture) + "-" + RootTag(root);

    /// <summary><c>C</c> for a drive root; <c>C-1a2b3c4d</c> for a folder (hash, so the file name carries no path).</summary>
    public static string RootTag(string root)
    {
        var n = PathUtil.Normalize(root);
        if (n.Length == 3) return n[..1];
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(n.ToUpperInvariant()));
        return n[..1] + "-" + Convert.ToHexStringLower(hash.AsSpan(0, 4));
    }
}

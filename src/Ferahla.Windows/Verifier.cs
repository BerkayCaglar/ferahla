using System.Diagnostics;
using System.IO.Compression;
using System.IO.Enumeration;
using Ferahla.Core;
using Ferahla.Core.Findings;
using Ferahla.Core.Text;

namespace Ferahla.Windows;

/// <summary>
/// The deep checks behind <c>ferahla verify</c> (CLI, UI and Claude's verify_finding): items still there and their size
/// now; duplicates compared byte by byte with the kept copy; a zip and its extracted folder compared file by file with
/// CRC-32. Read-only. Big folders are measured, not hashed.
/// </summary>
public static class Verifier
{
    private const int MaxFilesCounted = 500_000;

    public static VerifyResult Verify(Finding f, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var c = Lang.Culture;
        var r = new VerifyResult { FindingId = f.Id, Title = f.Title };
        foreach (var i in f.Items.Take(50))
        {
            ct.ThrowIfCancellationRequested();
            var item = new VerifyItem { Path = i.Path, ScanBytes = i.Bytes, Exists = i.IsDir ? Directory.Exists(i.Path) : File.Exists(i.Path) };
            if (item.Exists)
            {
                try
                {
                    if (i.IsDir) (item.NowBytes, item.LastWriteUtc, item.Note) = MeasureDir(i.Path, ct);
                    else { var fi = new FileInfo(i.Path); item.NowBytes = fi.Length; item.LastWriteUtc = fi.LastWriteTimeUtc; }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { item.Note = ex.Message; }
            }
            r.Items.Add(item);
        }
        var present = r.Items.Count(x => x.Exists);
        r.Checks.Add(present == r.Items.Count
            ? Lang.T($"All {r.Items.Count} item(s) still exist", $"{r.Items.Count} öğenin hepsi hâlâ yerinde")
            : Lang.T($"{r.Items.Count - present} of {r.Items.Count} item(s) are gone", $"{r.Items.Count} öğenin {r.Items.Count - present} tanesi artık yok"));
        var nowTotal = r.Items.Sum(x => x.NowBytes ?? 0);
        if (present > 0) r.Checks.Add(Lang.T($"Size now: {Fmt.Size(nowTotal, c)} (scan: {Fmt.Size(r.Items.Sum(x => x.ScanBytes), c)})", $"Şu anki boyut: {Fmt.Size(nowTotal, c)} (taramada: {Fmt.Size(r.Items.Sum(x => x.ScanBytes), c)})"));
        var ok = present == r.Items.Count && present > 0;

        if (f.Category == "duplicate") ok &= Duplicates(f, r, ct);
        else if (f.Category == "archive") ok &= Archive(f, r, ct);
        r.Ok = ok;
        r.Seconds = Math.Round(sw.Elapsed.TotalSeconds, 1);
        return r;
    }

    private static (long, DateTime?, string?) MeasureDir(string path, CancellationToken ct)
    {
        long bytes = 0; long n = 0; DateTime newest = default;
        var e = new FileSystemEnumerable<(long Len, DateTime T)>(path, (ref FileSystemEntry x) => (x.Length, x.LastWriteTimeUtc.UtcDateTime),
            new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 })
        {
            ShouldIncludePredicate = (ref FileSystemEntry x) => !x.IsDirectory,
            ShouldRecursePredicate = (ref FileSystemEntry x) => (x.Attributes & FileAttributes.ReparsePoint) == 0,
        };
        foreach (var (len, t) in e)
        {
            bytes += len; if (t > newest) newest = t;
            if (++n % 4096 == 0) ct.ThrowIfCancellationRequested();
            if (n >= MaxFilesCounted) return (bytes, newest, Lang.T("Stopped counting at 500,000 files", "500.000 dosyada saymayı bıraktı"));
        }
        return (bytes, newest == default ? null : newest, null);
    }

    // ---- duplicates ----

    /// <summary>The kept copy is named in the first evidence line ("Kept: …").</summary>
    private static bool Duplicates(Finding f, VerifyResult r, CancellationToken ct)
    {
        var kept = f.Evidence.Select(e => { var i = e.IndexOf(':'); return i > 0 ? e[(i + 1)..].Trim() : null; })
            .FirstOrDefault(p => p is { Length: > 3 } && p[1] == ':' && File.Exists(p));
        if (kept is null)
        {
            r.Checks.Add(Lang.T("The kept copy is gone: keep these files until you look", "Tutulan kopya artık yok: bakana kadar bu dosyaları tut"));
            return false;
        }
        var all = true;
        foreach (var i in f.Items.Where(i => !i.IsDir && File.Exists(i.Path)))
        {
            var same = SameContent(kept, i.Path, ct);
            all &= same;
            r.Checks.Add(same
                ? Lang.T($"{Path.GetFileName(i.Path)}: byte-for-byte identical to {kept}", $"{Path.GetFileName(i.Path)}: {kept} ile bayt bayt aynı")
                : Lang.T($"{Path.GetFileName(i.Path)}: DIFFERENT from {kept}; not a duplicate", $"{Path.GetFileName(i.Path)}: {kept} ile FARKLI; kopya değil"));
        }
        return all;
    }

    internal static bool SameContent(string a, string b, CancellationToken ct = default)
    {
        using var fa = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        using var fb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        if (fa.Length != fb.Length) return false;
        var ba = new byte[1 << 20]; var bb = new byte[1 << 20];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var na = fa.ReadAtLeast(ba, ba.Length, throwOnEndOfStream: false);
            var nb = fb.ReadAtLeast(bb, bb.Length, throwOnEndOfStream: false);
            if (na != nb) return false;
            if (na == 0) return true;
            if (!ba.AsSpan(0, na).SequenceEqual(bb.AsSpan(0, nb))) return false;
        }
    }

    // ---- archive next to its extracted folder ----

    private static bool Archive(Finding f, VerifyResult r, CancellationToken ct)
    {
        var zip = f.Items.Select(i => i.Path).FirstOrDefault(p => p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && File.Exists(p));
        if (zip is null || f.Items.Count != 1)
        {
            r.Checks.Add(Lang.T("Only zip archives can be compared with their folder", "Sadece zip arşivleri klasörleriyle karşılaştırılabilir"));
            return true; // nothing contradicts the finding
        }
        var stem = Path.GetFileNameWithoutExtension(zip);
        var folder = Path.Combine(Path.GetDirectoryName(zip)!, stem);
        if (!Directory.Exists(folder))
        {
            r.Checks.Add(Lang.T($"No extracted folder {stem}\\ next to it", $"Yanında açılmış {stem}\\ klasörü yok"));
            return false;
        }
        try
        {
            using var archive = ZipFile.OpenRead(zip);
            var entries = archive.Entries.Where(e => !e.FullName.EndsWith('/')).ToList();
            var top = entries.Count > 0 && entries.All(e => e.FullName.Contains('/')) ? entries[0].FullName[..entries[0].FullName.IndexOf('/')] : null;
            if (top is not null && entries.Any(e => !e.FullName.StartsWith(top + "/", StringComparison.Ordinal))) top = null;
            int same = 0, missing = 0, different = 0;
            foreach (var e in entries)
            {
                ct.ThrowIfCancellationRequested();
                var rel = e.FullName.Replace('/', '\\');
                var p = Path.Combine(folder, rel);
                if (!File.Exists(p) && top is not null) p = Path.Combine(folder, rel[(top.Length + 1)..]);
                // A crafted entry name (..\, C:\) must not make us read outside the folder.
                if (!PathUtil.IsUnder(Path.GetFullPath(p), folder)) { missing++; continue; }
                if (!File.Exists(p)) { missing++; continue; }
                var fi = new FileInfo(p);
                if (fi.Length != e.Length || Crc32.Of(p, ct) != e.Crc32) different++;
                else same++;
            }
            r.Checks.Add(Lang.T($"zip vs folder: {same}/{entries.Count} files identical (CRC-32)", $"zip ve klasör: {entries.Count} dosyanın {same} tanesi aynı (CRC-32)"));
            if (missing > 0) r.Checks.Add(Lang.T($"{missing} file(s) of the zip are missing from the folder", $"zip'teki {missing} dosya klasörde yok"));
            if (different > 0) r.Checks.Add(Lang.T($"{different} file(s) differ from the zip (edited after extracting?)", $"{different} dosya zip'tekinden farklı (açtıktan sonra değiştirilmiş olabilir)"));
            return missing == 0 && different == 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            r.Checks.Add(Lang.T($"The zip could not be read: {ex.Message}", $"zip okunamadı: {ex.Message}"));
            return false;
        }
    }
}

/// <summary>CRC-32 (IEEE 802.3, as in zip), table-driven.</summary>
public static class Crc32
{
    private static readonly uint[] Table = Build();

    private static uint[] Build()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    public static uint Append(uint crc, ReadOnlySpan<byte> data)
    {
        crc = ~crc;
        foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    public static uint Of(string path, CancellationToken ct = default)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        var buf = new byte[1 << 20];
        uint crc = 0;
        int n;
        while ((n = fs.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            crc = Append(crc, buf.AsSpan(0, n));
        }
        return crc;
    }
}

using System.IO.Compression;
using System.Text.RegularExpressions;
using Ferahla.Core;
using Ferahla.Core.Findings;
using Ferahla.Core.Model;
using Ferahla.Core.Rules;
using Ferahla.Core.Text;

namespace Ferahla.Windows.Detectors;

/// <summary>
/// Archives in the user's folders. With an extracted twin folder next to it (x.zip + x\), a zip is verified entry by
/// entry (name + size) and becomes safe when everything is there; other formats can't be read yet and stay review.
/// Multi-part sets (x.part01.rar…, x.7z.001…) are one finding. Large archives with no twin, untouched for 30 days,
/// are shown at review level.
/// </summary>
public sealed partial class ArchivesDetector : IDetector
{
    private const long UnopenedMinBytes = 200L << 20;
    /// <summary>Archives with a twin folder are worth showing from this size (stem packs are often 10–50 MB).</summary>
    private const long TwinMinBytes = 5L << 20;
    private const int MaxEntries = 20_000;

    public string Name => "archives";

    [GeneratedRegex(@"^(?<base>.+?)(?:\.part\d+\.rar|\.(?:7z|zip|rar)\.\d{3}|\.r\d\d|\.z\d\d)$", RegexOptions.IgnoreCase)]
    private static partial Regex PartRx();

    public IEnumerable<Finding> Detect(DetectorContext ctx, Rule rule)
    {
        var ix = ctx.Index;
        // Archives inside projects (assets, packaged builds, sdists) belong to the project.
        var archives = DetectorUtil.FilesWith(ctx.Snapshot, FileFlags.Archive)
            .Where(a => (a.Flags & FileFlags.Installer) == 0 && !ctx.InProject(a.Dir)).ToList();
        var dlDesk = new[] { ctx.Var("Downloads"), ctx.Var("Desktop") };

        // Group multi-part sets by directory + base name; single archives are their own group.
        var sets = archives.GroupBy(a => (a.Dir, Base: BaseName(Path.GetFileName(a.Name)), Sub: Path.GetDirectoryName(a.Name) ?? ""));
        foreach (var set in sets)
        {
            var parts = set.ToList();
            var dirPath = PathUtil.Join(ix.PathOf(set.Key.Dir), set.Key.Sub).TrimEnd('\\');
            var first = ix.PathOf(parts.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).First());
            var twinPath = PathUtil.Join(dirPath, set.Key.Base);
            var twin = ctx.Live ? Directory.Exists(twinPath) : ix.Find(twinPath) >= 0;
            var bytes = parts.Sum(p => p.Size);
            var latest = parts.Max(p => p.LastWriteTicks);
            var multi = parts.Count > 1 || PartRx().IsMatch(Path.GetFileName(first));
            var args = new Dictionary<string, string> { ["name"] = Path.GetFileName(first), ["count"] = parts.Count.ToString(Lang.Culture), ["base"] = set.Key.Base };

            Finding? f = null;
            if (twin)
            {
                f = FindingsEngine.NewFinding(rule, first, args);
                f.Evidence.Add(Lang.T($"Extracted folder next to it: {set.Key.Base}\\", $"Yanında açılmış klasörü var: {set.Key.Base}\\"));
                f.Risk = Risk.Review;
                if (!multi && first.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && ctx.Live && VerifyZip(first, twinPath) is var (ok, total) && total > 0)
                {
                    f.Evidence.Add(Lang.T($"zip contents present in the folder: {ok}/{total} (name + size)", $"zip içeriği klasörde var: {ok}/{total} (ad + boyut)"));
                    if (ok == total) f.Risk = Risk.Safe;
                }
                else if (!first.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    f.Evidence.Add(Lang.T("Contents not verified (this archive format can't be read yet)", "İçerik doğrulanmadı (bu arşiv biçimi henüz okunamıyor)"));
            }
            else if (bytes >= UnopenedMinBytes && DateTime.UtcNow - new DateTime(latest, DateTimeKind.Utc) > TimeSpan.FromDays(30)
                     && DetectorUtil.IsUnderAny(first, dlDesk))
            {
                f = FindingsEngine.NewFinding(rule, first, args);
                f.Risk = Risk.Review;
                f.Evidence.Add(Lang.T($"No extracted folder found; last changed {DetectorUtil.Date(new DateTime(latest, DateTimeKind.Utc))}",
                                      $"Açılmış hâli bulunamadı; son değişiklik {DetectorUtil.Date(new DateTime(latest, DateTimeKind.Utc))}"));
            }
            if (f is null) continue;
            if (multi) f.Evidence.Add(Lang.T($"Multi-part archive: {parts.Count} parts", $"Çok parçalı arşiv: {parts.Count} parça"));
            foreach (var p in parts) f.Items.Add(FindingsEngine.FileItem(ix, p));
            if (bytes < (twin ? TwinMinBytes : rule.MinBytes)) continue;
            yield return f;
        }
    }

    private static string BaseName(string fileName)
    {
        var m = PartRx().Match(fileName);
        if (m.Success) return m.Groups["base"].Value;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        return stem.EndsWith(".tar", StringComparison.OrdinalIgnoreCase) ? stem[..^4] : stem;
    }

    /// <summary>How many file entries of the zip exist in <paramref name="folder"/> with the same size.</summary>
    internal static (int Ok, int Total)? VerifyZip(string zipPath, string folder)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var entries = zip.Entries.Where(e => !e.FullName.EndsWith('/')).Take(MaxEntries + 1).ToList();
            if (entries.Count > MaxEntries) return null;
            // Some archives wrap everything in one top folder named like the archive; accept both layouts.
            var prefix = CommonTopFolder(entries);
            var ok = 0;
            foreach (var e in entries)
            {
                var rel = e.FullName.Replace('/', '\\');
                var candidate = Path.Combine(folder, rel);
                if (!File.Exists(candidate) && prefix is not null) candidate = Path.Combine(folder, rel[(prefix.Length + 1)..]);
                if (File.Exists(candidate) && new FileInfo(candidate).Length == e.Length) ok++;
            }
            return (ok, entries.Count);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? CommonTopFolder(List<ZipArchiveEntry> entries)
    {
        string? top = null;
        foreach (var e in entries)
        {
            var i = e.FullName.IndexOf('/');
            if (i <= 0) return null;
            var t = e.FullName[..i];
            if (top is null) top = t;
            else if (!string.Equals(top, t, StringComparison.Ordinal)) return null;
        }
        return top;
    }
}

/// <summary>Unfinished downloads. Safe when the finished file sits next to it; review when old.</summary>
public sealed class PartialsDetector : IDetector
{
    public string Name => "partials";

    public IEnumerable<Finding> Detect(DetectorContext ctx, Rule rule)
    {
        var ix = ctx.Index;
        // Only where downloads land: ".part" is also used by test data and tools elsewhere.
        var dlDesk = new[] { ctx.Var("Downloads"), ctx.Var("Desktop") };
        foreach (var p in DetectorUtil.FilesWith(ctx.Snapshot, FileFlags.PartialDownload))
        {
            var path = ix.PathOf(p);
            if (!DetectorUtil.IsUnderAny(path, dlDesk) || ctx.InProject(p.Dir)) continue;
            var finished = path[..path.LastIndexOf('.')];
            var hasTwin = ctx.Live && File.Exists(finished);
            var age = DateTime.UtcNow - p.LastWriteUtc;
            if (!hasTwin && age < TimeSpan.FromDays(3)) continue; // may still be downloading
            var f = FindingsEngine.NewFinding(rule, path, new() { ["name"] = Path.GetFileName(finished) });
            f.Items.Add(FindingsEngine.FileItem(ix, p));
            if (hasTwin)
            {
                var len = new FileInfo(finished).Length;
                f.Risk = Risk.Safe;
                f.Evidence.Add(Lang.T($"Finished file exists: {Path.GetFileName(finished)} ({Fmt.Size(len, Lang.Culture)})",
                                      $"Tamamlanmış hâli var: {Path.GetFileName(finished)} ({Fmt.Size(len, Lang.Culture)})"));
            }
            else
            {
                f.Risk = Risk.Review;
                f.Evidence.Add(Lang.T($"Not touched for {(int)age.TotalDays} days", $"{(int)age.TotalDays} gündür dokunulmamış"));
            }
            yield return f;
        }
    }
}

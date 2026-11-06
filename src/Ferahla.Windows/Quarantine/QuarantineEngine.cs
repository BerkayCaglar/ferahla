using System.Globalization;
using System.IO.Enumeration;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ferahla.Core;
using Ferahla.Core.Json;
using Ferahla.Core.Quarantine;
using Ferahla.Core.Text;

namespace Ferahla.Windows.Quarantine;

/// <summary>
/// Moves approved items into a quarantine folder on their own volume (a rename: instant, no copy) and back.
/// Layout: <c>&lt;root&gt;\&lt;batch&gt;\manifest.json</c> + <c>items\&lt;n&gt;\&lt;original name&gt;</c>.
/// The root is <c>&lt;home&gt;\quarantine</c> on the volume that holds Ferahla's home, and
/// <c>X:\.ferahla\quarantine</c> (private ACL, hidden) on other fixed NTFS/ReFS volumes.
/// One item failing (in use, access denied) never stops the rest.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class QuarantineEngine(string home, MoveGuard guard, Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly Dictionary<string, string?> _volumeProblem = new(StringComparer.OrdinalIgnoreCase);

    public string Home { get; } = Path.GetFullPath(home);

    [GeneratedRegex(@"^\d{8}T\d{6}Z-[0-9a-f]{4}$")]
    private static partial Regex BatchIdRegex();

    public static bool IsBatchId(string s) => BatchIdRegex().IsMatch(s);

    // ---- locations ----

    public string RootFor(string volumeRoot) =>
        string.Equals(PathUtil.Normalize(volumeRoot), PathUtil.Normalize(Path.GetPathRoot(Home)!), StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Home, "quarantine")
            : Path.Combine(PathUtil.Normalize(volumeRoot), ".ferahla", "quarantine");

    public IEnumerable<string> ExistingRoots()
    {
        var home = Path.Combine(Home, "quarantine");
        if (Directory.Exists(home)) yield return home;
        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.DriveType != DriveType.Fixed) continue;
            var r = RootFor(d.RootDirectory.FullName);
            if (!string.Equals(r, home, StringComparison.OrdinalIgnoreCase) && Directory.Exists(r)) yield return r;
        }
    }

    /// <summary>Null if quarantine works on this volume, otherwise why not.</summary>
    private string? VolumeProblem(string volumeRoot)
    {
        if (_volumeProblem.TryGetValue(volumeRoot, out var cached)) return cached;
        string? problem;
        try
        {
            var d = new DriveInfo(volumeRoot);
            problem = d.DriveType != DriveType.Fixed || d.DriveFormat is not ("NTFS" or "ReFS")
                ? Lang.T($"No quarantine on {volumeRoot} ({d.DriveType}, {d.DriveFormat}): only fixed NTFS/ReFS drives", $"{volumeRoot} sürücüsünde karantina yok ({d.DriveType}, {d.DriveFormat}): sadece sabit NTFS/ReFS sürücüler")
                : null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            problem = Lang.T($"Drive {volumeRoot} is not available", $"{volumeRoot} sürücüsü kullanılamıyor");
        }
        return _volumeProblem[volumeRoot] = problem;
    }

    /// <summary>A drive-root quarantine would inherit "Authenticated Users: Modify": make it private and hidden.</summary>
    internal static void MakePrivate(string dotFerahla)
    {
        var di = Directory.CreateDirectory(dotFerahla);
        var sec = di.GetAccessControl();
        if (!sec.AreAccessRulesProtected)
        {
            sec = new DirectorySecurity();
            sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var user = WindowsIdentity.GetCurrent().User!;
            foreach (var sid in new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
                sec.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            di.SetAccessControl(sec);
        }
        di.Attributes |= FileAttributes.Hidden;
    }

    // ---- quarantine ----

    public QuarantineResult Quarantine(IReadOnlyList<ApprovedMove> moves, string? scanId, bool dryRun, Action<string>? progress = null)
    {
        var result = new QuarantineResult { DryRun = dryRun };
        var batchId = NewBatchId();
        var batches = new Dictionary<string, Batch>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var m in moves)
                foreach (var (path, isDir) in Units(m, result))
                {
                    progress?.Invoke(path);
                    var o = MoveOne(m, path, isDir, dryRun, scanId, batchId, batches);
                    result.Add(o);
                }
        }
        finally
        {
            foreach (var b in batches.Values)
                if (!b.Manifest.Items.Any(e => e.State != EntryState.Restored)) DeleteEmptyBatch(b.Dir);
        }
        if (!dryRun && batches.Values.Any(b => b.Manifest.Items.Count > 0)) result.Batch = batchId;
        return result;
    }

    /// <summary>
    /// What actually moves for one approved item. Usually the item itself; for a rule with keepRecentHours
    /// (Temp) the folder stays and only its entries untouched for that long move.
    /// </summary>
    private IEnumerable<(string Path, bool IsDir)> Units(ApprovedMove m, QuarantineResult result)
    {
        if (m.KeepRecentHours is not int hours || !m.IsDir) { yield return (m.Path, m.IsDir); yield break; }
        var cut = _now().UtcDateTime.AddHours(-hours);
        List<(string, bool)> old = [];
        int kept = 0;
        long keptBytes = 0;
        try
        {
            foreach (var e in new DirectoryInfo(m.Path).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true }))
            {
                var isDir = e is DirectoryInfo;
                var (bytes, _, newest) = Measure(e.FullName, isDir);
                if (newest >= cut) { kept++; keptBytes += bytes; }
                else old.Add((PathUtil.Normalize(e.FullName), isDir));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (kept > 0)
            result.Add(new ItemOutcome
            {
                Path = m.Path, FindingId = m.FindingId, Status = OutcomeStatus.Skipped, Bytes = keptBytes,
                Reason = Lang.T($"{kept} item(s) changed in the last {hours} h were left in place", $"Son {hours} saatte değişen {kept} öğe yerinde bırakıldı"),
            });
        foreach (var u in old) yield return u;
    }

    private ItemOutcome MoveOne(ApprovedMove m, string path, bool isDir, bool dryRun, string? scanId, string batchId, Dictionary<string, Batch> batches)
    {
        var o = new ItemOutcome { Path = path, FindingId = m.FindingId, Bytes = m.PlannedBytes };
        if (guard.Check(path) is { } blocked) return Skip(o, blocked);

        FileAttributes attrs;
        try { attrs = File.GetAttributes(path); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return Skip(o, Lang.T("No longer exists", "Artık yok")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Fail(o, ex.Message); }
        if (attrs.HasFlag(FileAttributes.Directory) != isDir) return Skip(o, Lang.T("Changed since the scan (file/folder)", "Taramadan sonra değişmiş (dosya/klasör)"));

        var volume = PathUtil.Normalize(Path.GetPathRoot(path)!);
        if (VolumeProblem(volume) is { } vp) return Skip(o, vp);

        var (bytes, files, _) = Measure(path, isDir);
        o.Bytes = bytes;
        if (dryRun) { o.Status = OutcomeStatus.WouldMove; return o; }

        if (!batches.TryGetValue(volume, out var b))
        {
            var root = RootFor(volume);
            try
            {
                if (!PathUtil.IsUnder(root, Home)) MakePrivate(Path.GetDirectoryName(root)!);
                b = new Batch(Path.Combine(root, batchId), new QuarantineManifest { Batch = batchId, CreatedAt = _now(), ScanId = scanId, Volume = volume });
                Directory.CreateDirectory(b.Dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _volumeProblem[volume] = Lang.T($"Could not create the quarantine folder on {volume}: {ex.Message}", $"{volume} üzerinde karantina klasörü oluşturulamadı: {ex.Message}");
                return Skip(o, _volumeProblem[volume]!);
            }
            batches[volume] = b;
        }

        var n = b.Manifest.Items.Count == 0 ? 1 : b.Manifest.Items.Max(e => e.N) + 1;
        var slot = Path.Combine(b.Dir, "items", n.ToString(CultureInfo.InvariantCulture));
        var entry = new QuarantineEntry
        {
            N = n, OriginalPath = path, StoredPath = Path.Combine("items", n.ToString(CultureInfo.InvariantCulture), PathUtil.Name(path)),
            IsDir = isDir, Bytes = bytes, Files = files, FindingId = m.FindingId, RuleId = m.RuleId, State = EntryState.Moving,
        };
        Directory.CreateDirectory(slot);
        b.Manifest.Items.Add(entry);
        b.Save(); // write-ahead: if Ferahla stops mid-move, restore still knows where the item may be

        var rc = Native.Rename(path, Path.Combine(b.Dir, entry.StoredPath));
        if (rc == 0)
        {
            entry.State = EntryState.Quarantined;
            entry.MovedAt = _now();
            b.Save();
            o.Status = OutcomeStatus.Moved;
            return o;
        }
        b.Manifest.Items.Remove(entry);
        b.Save();
        TryDeleteEmpty(slot);
        return Explain(o, rc, path, isDir);
    }

    private static ItemOutcome Explain(ItemOutcome o, int rc, string path, bool isDir)
    {
        const int ACCESS_DENIED = 5, SHARING_VIOLATION = 32, LOCK_VIOLATION = 33, NOT_SAME_DEVICE = 17;
        if (rc is ACCESS_DENIED or SHARING_VIOLATION or LOCK_VIOLATION)
        {
            var holders = LockInspector.WhoIsLocking(path, isDir);
            if (holders.Count > 0)
            {
                o.LockedBy = holders;
                return Fail(o, Lang.T("In use by: ", "Kullanan: ") + string.Join(", ", holders.Select(h => $"{h.App} ({h.Pid})")));
            }
            if (isDir)
                return Fail(o, Lang.T("In use: the folder may be open in another window (terminal, Explorer, editor)", "Kullanımda: klasör başka bir pencerede açık olabilir (terminal, Gezgin, editör)"));
            return Fail(o, rc == ACCESS_DENIED ? Lang.T("Access denied", "Erişim reddedildi") : Lang.T("In use by another program", "Başka bir program kullanıyor"));
        }
        if (rc == NOT_SAME_DEVICE) return Fail(o, Lang.T("Lies on another drive (through a junction or mount point)", "Başka bir sürücüde (bağlantı noktası üzerinden)"));
        if (rc is 2 or 3) return Skip(o, Lang.T("No longer exists", "Artık yok"));
        return Fail(o, Lang.T($"Windows error {rc}", $"Windows hatası {rc}"));
    }

    // ---- restore ----

    /// <exception cref="FileNotFoundException">No such batch.</exception>
    public QuarantineResult Restore(string batchId, bool dryRun, Action<string>? progress = null)
    {
        if (!IsBatchId(batchId)) throw new ArgumentException(Lang.T($"Not a batch id: {batchId}", $"Geçerli bir karantina kimliği değil: {batchId}"));
        var found = Load(batchId);
        if (found.Count == 0) throw new FileNotFoundException(Lang.T($"No quarantine batch {batchId}", $"{batchId} karantina kaydı yok"));

        var result = new QuarantineResult { DryRun = dryRun, Batch = batchId };
        foreach (var b in found)
        {
            foreach (var e in b.Manifest.Items.Where(e => e.State != EntryState.Restored).ToList())
            {
                progress?.Invoke(e.OriginalPath);
                var o = new ItemOutcome { Path = e.OriginalPath, FindingId = e.FindingId, Bytes = e.Bytes };
                var stored = Path.GetFullPath(Path.Combine(b.Dir, e.StoredPath));
                if (!PathUtil.IsUnder(stored, b.Dir) || guard.Check(e.OriginalPath) is not null)
                {
                    result.Add(Fail(o, Lang.T("The manifest entry is not valid", "Manifest kaydı geçersiz")));
                    continue;
                }
                if (!Exists(stored))
                {
                    if (e.State == EntryState.Moving) { b.Manifest.Items.Remove(e); if (!dryRun) b.Save(); continue; } // never moved
                    result.Add(Fail(o, Lang.T($"Missing from quarantine: {stored}", $"Karantinada yok: {stored}")));
                    continue;
                }
                if (Exists(e.OriginalPath))
                {
                    result.Add(Skip(o, Lang.T($"Something is already at the original place; the item stays in {stored}", $"Asıl yerde zaten bir şey var; öğe {stored} içinde kalıyor")));
                    continue;
                }
                if (dryRun) { o.Status = OutcomeStatus.WouldRestore; result.Add(o); continue; }

                try { Directory.CreateDirectory(PathUtil.Parent(e.OriginalPath)!); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { result.Add(Fail(o, ex.Message)); continue; }
                var rc = Native.Rename(stored, e.OriginalPath);
                if (rc != 0) { result.Add(Explain(o, rc, stored, e.IsDir)); continue; }
                e.State = EntryState.Restored;
                e.RestoredAt = _now();
                b.Save();
                TryDeleteEmpty(Path.GetDirectoryName(stored)!);
                o.Status = OutcomeStatus.Restored;
                result.Add(o);
            }
            if (!dryRun && b.Manifest.Items.All(e => e.State == EntryState.Restored)) DeleteEmptyBatch(b.Dir);
        }
        return result;
    }

    // ---- listing ----

    public List<BatchInfo> List()
    {
        var map = new Dictionary<string, BatchInfo>(StringComparer.Ordinal);
        foreach (var root in ExistingRoots())
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var id = Path.GetFileName(dir);
                if (!IsBatchId(id) || TryLoad(dir) is not { } b) continue;
                if (!map.TryGetValue(id, out var info))
                    map[id] = info = new BatchInfo { Batch = id, CreatedAt = b.Manifest.CreatedAt, ScanId = b.Manifest.ScanId };
                info.Volumes.Add(b.Manifest.Volume);
                foreach (var e in b.Manifest.Items)
                {
                    if (e.State == EntryState.Restored) { info.Restored++; continue; }
                    info.Items++;
                    info.Bytes += e.Bytes;
                }
            }
        return map.Values.OrderByDescending(b => b.Batch, StringComparer.Ordinal).ToList();
    }

    // ---- helpers ----

    private List<Batch> Load(string batchId) =>
        ExistingRoots().Select(r => TryLoad(Path.Combine(r, batchId))).OfType<Batch>().ToList();

    private static Batch? TryLoad(string dir)
    {
        var file = Path.Combine(dir, QuarantineManifest.FileName);
        if (!File.Exists(file)) return null;
        try
        {
            var m = JsonSerializer.Deserialize(File.ReadAllBytes(file), FerahlaJson.Default.QuarantineManifest);
            return m is { Kind: QuarantineManifest.KindName } && m.Schema <= QuarantineManifest.CurrentSchema ? new Batch(dir, m) : null;
        }
        catch (JsonException) { return null; }
    }

    private string NewBatchId() =>
        _now().UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + "-" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(2));

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>Bytes, file count and the newest write/creation time in the item (reparse points are not followed).</summary>
    internal static (long Bytes, long Files, DateTime Newest) Measure(string path, bool isDir)
    {
        try
        {
            if (!isDir)
            {
                var fi = new FileInfo(path);
                return (fi.Length, 1, Max(fi.LastWriteTimeUtc, fi.CreationTimeUtc));
            }
            var di = new DirectoryInfo(path);
            long bytes = 0, files = 0;
            var newest = Max(di.LastWriteTimeUtc, di.CreationTimeUtc);
            var e = new FileSystemEnumerable<(long Len, bool Dir, DateTime T)>(path,
                (ref FileSystemEntry x) => (x.IsDirectory ? 0 : x.Length, x.IsDirectory, Max(x.LastWriteTimeUtc.UtcDateTime, x.CreationTimeUtc.UtcDateTime)),
                new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 })
            {
                ShouldRecursePredicate = (ref FileSystemEntry x) => (x.Attributes & FileAttributes.ReparsePoint) == 0,
            };
            foreach (var (len, dir, t) in e)
            {
                if (!dir) { bytes += len; files++; }
                if (t > newest) newest = t;
            }
            return (bytes, files, newest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (0, 0, DateTime.MaxValue); }
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    private static ItemOutcome Skip(ItemOutcome o, string reason) { o.Status = OutcomeStatus.Skipped; o.Reason = reason; return o; }
    private static ItemOutcome Fail(ItemOutcome o, string reason) { o.Status = OutcomeStatus.Failed; o.Reason = reason; return o; }

    private static void TryDeleteEmpty(string dir)
    {
        try { Directory.Delete(dir, recursive: false); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Removes a batch folder only if nothing but empty slots and the manifest is left.</summary>
    private static void DeleteEmptyBatch(string dir)
    {
        var items = Path.Combine(dir, "items");
        if (Directory.Exists(items))
        {
            foreach (var slot in Directory.EnumerateDirectories(items)) TryDeleteEmpty(slot);
            TryDeleteEmpty(items);
            if (Directory.Exists(items)) return;
        }
        if (Directory.EnumerateFileSystemEntries(dir).Any(p => !string.Equals(Path.GetFileName(p), QuarantineManifest.FileName, StringComparison.OrdinalIgnoreCase))) return;
        try { File.Delete(Path.Combine(dir, QuarantineManifest.FileName)); } catch (IOException) { return; }
        TryDeleteEmpty(dir);
    }

    private sealed class Batch(string dir, QuarantineManifest manifest)
    {
        public string Dir { get; } = dir;
        public QuarantineManifest Manifest { get; } = manifest;

        public void Save()
        {
            var file = Path.Combine(Dir, QuarantineManifest.FileName);
            var tmp = file + ".tmp";
            File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(Manifest, FerahlaJson.Indented.QuarantineManifest));
            File.Move(tmp, file, overwrite: true);
        }
    }
}

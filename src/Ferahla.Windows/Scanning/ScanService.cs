using Ferahla.Core;
using Ferahla.Core.Cli;
using Ferahla.Core.Model;
using Ferahla.Core.Storage;

namespace Ferahla.Windows.Scanning;

/// <summary>Scan + system info + save: the one way a scan is made, shared by the CLI and the UI server.</summary>
public static class ScanService
{
    /// <summary>Full, normalized root; "C:" means the drive (Windows would read it as "current directory on C").</summary>
    public static string ResolveRoot(string? target, IReadOnlyDictionary<string, string> known)
    {
        target ??= known.GetValueOrDefault("SystemDrive", @"C:\");
        if (target.Length == 2 && target[1] == ':') target += "\\";
        return PathUtil.Normalize(Path.GetFullPath(target));
    }

    public static (Snapshot Snapshot, ScanSummary Summary) Run(string root, SnapshotStore store, IReadOnlyDictionary<string, string> known,
        long minDirBytes = 1L << 20, IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        var snap = new Scanner(new ScanOptions
        {
            Root = root, MinDirBytes = minDirBytes, KnownFolders = known, Progress = progress, Cancellation = ct,
        }).Run();
        ct.ThrowIfCancellationRequested();
        snap.Volume = SystemInfo.Volume(PathUtil.Normalize(root[..3]));
        snap.Apps = SystemInfo.InstalledApps();
        var saved = store.Save(snap);
        var used = snap.Volume.TotalBytes - snap.Volume.FreeBytes;
        return (snap, new ScanSummary
        {
            Id = snap.Id, Root = snap.Root, SavedTo = saved, ScannedAt = snap.ScannedAt, ElapsedSeconds = snap.ElapsedSeconds,
            TotalBytes = snap.Volume.TotalBytes, FreeBytes = snap.Volume.FreeBytes, ScannedBytes = snap.Stats.ScannedBytes,
            UnaccountedBytes = root.Length == 3 ? used - snap.Stats.ScannedBytes : 0,
            Files = snap.Stats.Files, Dirs = snap.Stats.Dirs, KeptDirs = snap.Dirs.Count, KeptFiles = snap.Files.Count,
            AccessDeniedDirs = snap.Stats.AccessDeniedDirs, ReparsePointsSkipped = snap.Stats.ReparsePointsSkipped,
            CloudOnlyBytes = snap.Stats.CloudOnlyBytes, InstalledApps = snap.Apps.Count,
        });
    }
}

using System.Text.Json.Serialization;

namespace Ferahla.Core.Model;

/// <summary>
/// One scan of one volume. Directories are stored as a flat list with parent indices; only directories
/// whose total size is at least <see cref="MinDirBytes"/> are kept (their parents' totals still include
/// everything). Files are kept only when notable (large, installers, archives, partial downloads, root files).
/// </summary>
public sealed class Snapshot
{
    public const int CurrentSchema = 1;

    public int Schema { get; set; } = CurrentSchema;
    public string Id { get; set; } = "";
    public string ToolVersion { get; set; } = "";
    public DateTimeOffset ScannedAt { get; set; }
    public double ElapsedSeconds { get; set; }
    /// <summary>Scanned root, e.g. <c>C:\</c>.</summary>
    public string Root { get; set; } = "";
    public VolumeInfo Volume { get; set; } = new();
    public ScanStats Stats { get; set; } = new();
    public long MinDirBytes { get; set; }
    public long BigFileBytes { get; set; }
    /// <summary>Known folder paths at scan time (UserProfile, LocalAppData, Downloads…), used to expand rule paths.</summary>
    public Dictionary<string, string> KnownFolders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Index 0 is the root.</summary>
    public List<DirEntry> Dirs { get; set; } = [];
    public List<FileEntry> Files { get; set; } = [];
    public List<InstalledApp> Apps { get; set; } = [];
}

public sealed class VolumeInfo
{
    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }
    public string Format { get; set; } = "";
    public bool IsSystem { get; set; }
    public string DriveType { get; set; } = "";
}

public sealed class ScanStats
{
    public long Files { get; set; }
    public long Dirs { get; set; }
    /// <summary>Sum of file sizes found (on-disk estimate; cloud-only files count as 0).</summary>
    public long ScannedBytes { get; set; }
    public long CloudOnlyFiles { get; set; }
    /// <summary>Logical size of cloud-only (not downloaded) files; not on disk.</summary>
    public long CloudOnlyBytes { get; set; }
    public long ReparsePointsSkipped { get; set; }
    public long AccessDeniedDirs { get; set; }
}

public sealed class DirEntry
{
    /// <summary>Parent index, -1 for root.</summary>
    [JsonPropertyName("p")] public int Parent { get; set; }
    [JsonPropertyName("n")] public string Name { get; set; } = "";
    /// <summary>Total bytes in this subtree (on-disk estimate).</summary>
    [JsonPropertyName("s")] public long Size { get; set; }
    /// <summary>Bytes of files directly in this directory.</summary>
    [JsonPropertyName("o")] public long OwnSize { get; set; }
    /// <summary>Total file count in this subtree.</summary>
    [JsonPropertyName("f")] public long FileCount { get; set; }
    /// <summary>Latest LastWriteTime of any file in the subtree (UTC ticks), 0 if none.</summary>
    [JsonPropertyName("m")] public long LastWriteTicks { get; set; }
    [JsonPropertyName("k")] public DirMarkers Markers { get; set; }
    [JsonPropertyName("x")] public DirFlags Flags { get; set; }

    [JsonIgnore] public DateTime? LastWriteUtc => LastWriteTicks == 0 ? null : new DateTime(LastWriteTicks, DateTimeKind.Utc);
}

public sealed class FileEntry
{
    [JsonPropertyName("d")] public int Dir { get; set; }
    [JsonPropertyName("n")] public string Name { get; set; } = "";
    /// <summary>On-disk estimate (0 for cloud-only).</summary>
    [JsonPropertyName("s")] public long Size { get; set; }
    /// <summary>Logical length.</summary>
    [JsonPropertyName("l")] public long Length { get; set; }
    [JsonPropertyName("m")] public long LastWriteTicks { get; set; }
    [JsonPropertyName("x")] public FileFlags Flags { get; set; }

    [JsonIgnore] public DateTime LastWriteUtc => new(LastWriteTicks, DateTimeKind.Utc);
}

public sealed class InstalledApp
{
    public string Name { get; set; } = "";
    public string? Version { get; set; }
    public string? Publisher { get; set; }
    public string? InstallLocation { get; set; }
    public long EstimatedBytes { get; set; }
    public string? UninstallString { get; set; }
    /// <summary>HKLM, HKLM32 or HKCU.</summary>
    public string Hive { get; set; } = "";
}

/// <summary>Project marker files/folders found directly inside a directory.</summary>
[Flags]
public enum DirMarkers
{
    None = 0,
    CargoToml = 1 << 0,
    PackageJson = 1 << 1,
    CsProj = 1 << 2,
    Sln = 1 << 3,
    UnityAssets = 1 << 4,
    UnityProjectSettings = 1 << 5,
    Git = 1 << 6,
    PyProject = 1 << 7,
    GoMod = 1 << 8,
    Gradle = 1 << 9,
    PomXml = 1 << 10,
    CMakeLists = 1 << 11,
    Pubspec = 1 << 12,
    ComposerJson = 1 << 13,
    MixExs = 1 << 14,
    StackYaml = 1 << 15,
    BuildZig = 1 << 16,
    UnrealProject = 1 << 17,
    GodotProject = 1 << 18,
    FsProj = 1 << 19,
    VbProj = 1 << 20,
}

[Flags]
public enum DirFlags
{
    None = 0,
    AccessDenied = 1 << 0,
    /// <summary>Directory is a reparse point (junction/symlink); not entered.</summary>
    Reparse = 1 << 1,
    Hidden = 1 << 2,
    System = 1 << 3,
    /// <summary>Some files in the subtree are cloud-only placeholders.</summary>
    HasCloudOnly = 1 << 4,
}

[Flags]
public enum FileFlags
{
    None = 0,
    CloudOnly = 1 << 0,
    Sparse = 1 << 1,
    Compressed = 1 << 2,
    Hidden = 1 << 3,
    System = 1 << 4,
    Big = 1 << 5,
    Installer = 1 << 6,
    Archive = 1 << 7,
    PartialDownload = 1 << 8,
    RootFile = 1 << 9,
    ReadOnly = 1 << 10,
}

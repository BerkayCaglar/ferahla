namespace Ferahla.Core.Cli;

// Versioned JSON shapes for `ferahla <command> --json`. Bump OutputSchema on breaking changes.

public static class OutputSchema
{
    public const int Version = 1;
}

public sealed class ScanSummary
{
    public int Schema { get; set; } = OutputSchema.Version;
    public string Id { get; set; } = "";
    public string Root { get; set; } = "";
    public string SavedTo { get; set; } = "";
    public DateTimeOffset ScannedAt { get; set; }
    public double ElapsedSeconds { get; set; }
    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }
    public long ScannedBytes { get; set; }
    /// <summary>Used space the scan could not see (system files, other users, shadow copies…). Can be negative with hardlinks.</summary>
    public long UnaccountedBytes { get; set; }
    public long Files { get; set; }
    public long Dirs { get; set; }
    public long KeptDirs { get; set; }
    public long KeptFiles { get; set; }
    public long AccessDeniedDirs { get; set; }
    public long ReparsePointsSkipped { get; set; }
    public long CloudOnlyBytes { get; set; }
    public int InstalledApps { get; set; }
}

public sealed class ScanListItem
{
    public string Id { get; set; } = "";
    public string Root { get; set; } = "";
    public DateTimeOffset ScannedAt { get; set; }
    public long FreeBytes { get; set; }
    public long ScannedBytes { get; set; }
}

public sealed class InspectResult
{
    public int Schema { get; set; } = OutputSchema.Version;
    public string ScanId { get; set; } = "";
    public string Path { get; set; } = "";
    /// <summary>The snapshot folder that holds this path (itself, or the nearest kept ancestor if it was folded).</summary>
    public string SnapshotPath { get; set; } = "";
    public bool Exact { get; set; }
    public long Bytes { get; set; }
    public long Files { get; set; }
    public DateTime? LastWriteUtc { get; set; }
    public List<string> Markers { get; set; } = [];
    public List<string> Flags { get; set; } = [];
    public List<InspectChild> Children { get; set; } = [];
    public List<InspectChild> NotableFiles { get; set; } = [];
    /// <summary>Bytes in files directly here plus folders too small to list.</summary>
    public long OtherBytes { get; set; }
    public List<Findings.Finding> Findings { get; set; } = [];
    public long? GrowthBytes { get; set; }
    public string? PreviousScanId { get; set; }
}

public sealed class InspectChild
{
    public string Name { get; set; } = "";
    public long Bytes { get; set; }
    public DateTime? LastWriteUtc { get; set; }
    public string? Kind { get; set; }
}

public sealed class ErrorResult
{
    public int Schema { get; set; } = OutputSchema.Version;
    public string Error { get; set; } = "";
    public string? Detail { get; set; }
}

/// <summary>Process exit codes. Stable: scripts and agents rely on them.</summary>
public static class ExitCodes
{
    public const int Ok = 0;
    public const int Error = 1;
    public const int Usage = 2;
    public const int NotFound = 3;
    /// <summary>Command ran but the result is partial (e.g. scan finished with access-denied folders). Used only where documented.</summary>
    public const int Partial = 4;
}

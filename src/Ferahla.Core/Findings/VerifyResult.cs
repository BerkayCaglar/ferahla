using Ferahla.Core.Cli;

namespace Ferahla.Core.Findings;

/// <summary>Result of <c>ferahla verify</c>: a finding checked against the disk as it is now.</summary>
public sealed class VerifyResult
{
    public int Schema { get; set; } = OutputSchema.Version;
    public string FindingId { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>True if every check passed (items exist; copies identical; archive contents identical).</summary>
    public bool Ok { get; set; }
    public List<VerifyItem> Items { get; set; } = [];
    /// <summary>Human-readable results of the deep checks, in the UI language.</summary>
    public List<string> Checks { get; set; } = [];
    public double Seconds { get; set; }
}

public sealed class VerifyItem
{
    public string Path { get; set; } = "";
    public bool Exists { get; set; }
    public long ScanBytes { get; set; }
    public long? NowBytes { get; set; }
    public DateTime? LastWriteUtc { get; set; }
    public string? Note { get; set; }
}

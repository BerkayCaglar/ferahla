using Ferahla.Core.Cli;
using Ferahla.Core.Rules;

namespace Ferahla.Core.Findings;

public sealed class Finding
{
    /// <summary>Stable across scans for the same rule + location: <c>f-</c> + 10 hex.</summary>
    public string Id { get; set; } = "";
    public string RuleId { get; set; } = "";
    public string Category { get; set; } = "";
    public Risk Risk { get; set; }
    public ActionKind Action { get; set; }
    public string? Command { get; set; }
    public string Title { get; set; } = "";
    public string? Reason { get; set; }
    public string? WhatBreaks { get; set; }
    public string? HowToRestore { get; set; }
    public List<FindingItem> Items { get; set; } = [];
    /// <summary>Sum of item sizes, excluding items that sit inside another finding's item.</summary>
    public long Bytes { get; set; }
    public List<string> Evidence { get; set; } = [];
    /// <summary>Latest change anywhere in the items (UTC).</summary>
    public DateTime? LastWriteUtc { get; set; }
    /// <summary>Growth since the previous scan of the same root, when known.</summary>
    public long? GrowthBytes { get; set; }
    public bool IsNew { get; set; }
    public int? KeepRecentHours { get; set; }
    public string? Note { get; set; }
}

public sealed class FindingItem
{
    public string Path { get; set; } = "";
    public bool IsDir { get; set; }
    public long Bytes { get; set; }
    public DateTime? LastWriteUtc { get; set; }
    /// <summary>Id of the finding whose item contains this one (its bytes are counted there).</summary>
    public string? NestedIn { get; set; }
}

public sealed class FindingsResult
{
    public int Schema { get; set; } = OutputSchema.Version;
    public string ScanId { get; set; } = "";
    public string? PreviousScanId { get; set; }
    public DateTimeOffset ScannedAt { get; set; }
    public string Root { get; set; } = "";
    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }
    public Dictionary<Risk, long> BytesByRisk { get; set; } = [];
    public List<Finding> Findings { get; set; } = [];
    /// <summary>Things deliberately not suggested, with the reason.</summary>
    public List<ProtectedNote> NotSuggested { get; set; } = [];
    public List<string> RuleErrors { get; set; } = [];
}

public sealed class ProtectedNote
{
    public string Path { get; set; } = "";
    public long Bytes { get; set; }
    public string Reason { get; set; } = "";
}

using System.Text.Json.Serialization;

namespace Ferahla.Mcp;

// Tool results: compact, sizes in MB, so Claude spends few tokens per finding.

public sealed class McpSummary
{
    public string ScanId { get; set; } = "";
    public string Root { get; set; } = "";
    public DateTimeOffset ScannedAt { get; set; }
    public double DriveTotalMb { get; set; }
    public double DriveFreeMb { get; set; }
    public long Files { get; set; }
    public int Findings { get; set; }
    public int NotesAlready { get; set; }
    public Dictionary<string, double> SuggestedMbByRisk { get; set; } = [];
    public Dictionary<string, double> TopCategoriesMb { get; set; } = [];
    public List<McpSize> TopFolders { get; set; } = [];
    public string? PreviousScanId { get; set; }
    public double? DaysSincePrevious { get; set; }
    public double? FreeChangeMb { get; set; }
}

public sealed class McpSize
{
    public string Path { get; set; } = "";
    public double Mb { get; set; }
    public string? LastChange { get; set; }
}

public sealed class McpFindingPage
{
    public int Total { get; set; }
    public int Offset { get; set; }
    public List<McpFinding> Findings { get; set; } = [];
}

public sealed class McpFinding
{
    public string Id { get; set; } = "";
    public string Rule { get; set; } = "";
    public string Category { get; set; } = "";
    public string Risk { get; set; } = "";
    public string Action { get; set; } = "";
    public string Title { get; set; } = "";
    public double Mb { get; set; }
    public string? Reason { get; set; }
    public string? WhatBreaks { get; set; }
    public List<string> Evidence { get; set; } = [];
    public List<McpSize> Items { get; set; } = [];
    public int MoreItems { get; set; }
    public string? LastChange { get; set; }
    public bool? IsNew { get; set; }
    public double? GrowthMb { get; set; }
    public string? ExistingNote { get; set; }
}

/// <summary>One finding in full, for the per-suggestion chat.</summary>
public sealed class McpFindingDetail
{
    public string Id { get; set; } = "";
    public string? Error { get; set; }
    public string? Rule { get; set; }
    public string? Category { get; set; }
    public string? Risk { get; set; }
    public string? Action { get; set; }
    public string? Title { get; set; }
    public double Mb { get; set; }
    public string? Reason { get; set; }
    public string? WhatBreaks { get; set; }
    public string? HowToRestore { get; set; }
    public string? Command { get; set; }
    public List<string> Evidence { get; set; } = [];
    public List<McpSize> Items { get; set; } = [];
    public int MoreItems { get; set; }
    public string? LastChange { get; set; }
    public bool? IsNew { get; set; }
    public double? GrowthMb { get; set; }
    public string? Note { get; set; }
    public string? NoteVerdict { get; set; }
    public string? NoteBy { get; set; }
    /// <summary>Items of this finding that already sit in quarantine.</summary>
    public List<McpQuarantineEntry> InQuarantine { get; set; } = [];
}

public sealed class McpInspect
{
    public string Path { get; set; } = "";
    public bool Exact { get; set; }
    public double Mb { get; set; }
    public long Files { get; set; }
    public string? LastChange { get; set; }
    public string? ProjectMarkers { get; set; }
    public List<McpSize> Subfolders { get; set; } = [];
    public List<McpSize> NotableFiles { get; set; } = [];
    public List<string> FindingIds { get; set; } = [];
    public string? Error { get; set; }
}

public sealed class McpVerify
{
    public string FindingId { get; set; } = "";
    public string Verdict { get; set; } = "";
    public List<string> Checks { get; set; } = [];
    public List<McpItemCheck> Items { get; set; } = [];
}

public sealed class McpItemCheck
{
    public string Path { get; set; } = "";
    public bool Exists { get; set; }
    public double ScanMb { get; set; }
    public double? NowMb { get; set; }
    public string? LastChange { get; set; }
    public string? Note { get; set; }
}

public sealed class McpDiff
{
    public string? From { get; set; }
    public string? To { get; set; }
    public double? Days { get; set; }
    public double? FreeChangeMb { get; set; }
    public List<McpChange> Changes { get; set; } = [];
    public string? Note { get; set; }
}

public sealed class McpChange
{
    public string Path { get; set; } = "";
    public double ChangeMb { get; set; }
    public bool? New { get; set; }
    public bool? Gone { get; set; }
}

public sealed class McpNoteResult
{
    public string FindingId { get; set; } = "";
    public bool Saved { get; set; }
    public int Chars { get; set; }
    public string? Error { get; set; }
}

public sealed class McpNoteInput
{
    [System.ComponentModel.Description("Finding id, f- and 10 hex digits")]
    public string FindingId { get; set; } = "";
    [System.ComponentModel.Description("The note: what it is, what to do, and the deciding fact")]
    public string Text { get; set; } = "";
    [System.ComponentModel.Description("remove (safe to quarantine) | check (look first) | keep")]
    public string? Verdict { get; set; }
}

public sealed class McpNotesResult
{
    public int Saved { get; set; }
    public List<McpNoteResult> Failed { get; set; } = [];
    public string? Error { get; set; }
}

public sealed class McpQuarantinePage
{
    public int Total { get; set; }
    public int Offset { get; set; }
    public double TotalMb { get; set; }
    public List<McpQuarantineEntry> Entries { get; set; } = [];
}

public sealed class McpQuarantineEntry
{
    public string? FindingId { get; set; }
    public string? Title { get; set; }
    public string? Rule { get; set; }
    public string OriginalPath { get; set; } = "";
    public bool IsDir { get; set; }
    public double Mb { get; set; }
    public long Files { get; set; }
    public string? MovedAt { get; set; }
    public string? ExistingNote { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(McpSummary))]
[JsonSerializable(typeof(McpFindingPage))]
[JsonSerializable(typeof(McpInspect))]
[JsonSerializable(typeof(McpVerify))]
[JsonSerializable(typeof(McpDiff))]
[JsonSerializable(typeof(McpNoteResult))]
[JsonSerializable(typeof(McpNotesResult))]
[JsonSerializable(typeof(List<McpNoteInput>))]
[JsonSerializable(typeof(McpQuarantinePage))]
[JsonSerializable(typeof(McpFindingDetail))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(double))]
internal sealed partial class McpJson : JsonSerializerContext;

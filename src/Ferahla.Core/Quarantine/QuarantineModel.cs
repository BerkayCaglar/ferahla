using System.Text.Json.Serialization;
using Ferahla.Core.Cli;

namespace Ferahla.Core.Quarantine;

// ---- plan file (written by the HTML report, later by the UI) ----

/// <summary>What the user selected. Untrusted input: every item is checked again before anything moves.</summary>
public sealed class CleanupPlan
{
    public const string KindName = "ferahla-plan";
    public const int CurrentSchema = 1;

    public int Schema { get; set; }
    public string Kind { get; set; } = "";
    public string ScanId { get; set; } = "";
    public DateTimeOffset? CreatedAt { get; set; }
    public List<PlanFinding> Findings { get; set; } = [];
}

public sealed class PlanFinding
{
    public string Id { get; set; } = "";
    public string? RuleId { get; set; }
    public string? Action { get; set; }
    public List<PlanItem> Items { get; set; } = [];
}

public sealed class PlanItem
{
    public string Path { get; set; } = "";
    public long Bytes { get; set; }
}

// ---- manifest (one per batch and volume, next to the quarantined items) ----

[JsonConverter(typeof(JsonStringEnumConverter<EntryState>))]
public enum EntryState
{
    /// <summary>Written before the move; if Ferahla stops here, the item is either still in place or in quarantine.</summary>
    [JsonStringEnumMemberName("moving")] Moving,
    [JsonStringEnumMemberName("quarantined")] Quarantined,
    [JsonStringEnumMemberName("restored")] Restored,
}

public sealed class QuarantineManifest
{
    public const string KindName = "ferahla-quarantine";
    public const int CurrentSchema = 1;
    public const string FileName = "manifest.json";

    public int Schema { get; set; } = CurrentSchema;
    public string Kind { get; set; } = KindName;
    public string Batch { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string? ScanId { get; set; }
    /// <summary>Volume root these items came from, e.g. <c>D:\</c>.</summary>
    public string Volume { get; set; } = "";
    public List<QuarantineEntry> Items { get; set; } = [];
}

public sealed class QuarantineEntry
{
    public int N { get; set; }
    public string OriginalPath { get; set; } = "";
    /// <summary>Path inside the batch folder, e.g. <c>items\3\node_modules</c>.</summary>
    public string StoredPath { get; set; } = "";
    public bool IsDir { get; set; }
    public long Bytes { get; set; }
    public long Files { get; set; }
    public string? FindingId { get; set; }
    public string? RuleId { get; set; }
    public EntryState State { get; set; }
    public DateTimeOffset? MovedAt { get; set; }
    public DateTimeOffset? RestoredAt { get; set; }
}

// ---- results ----

[JsonConverter(typeof(JsonStringEnumConverter<OutcomeStatus>))]
public enum OutcomeStatus
{
    [JsonStringEnumMemberName("moved")] Moved,
    [JsonStringEnumMemberName("wouldMove")] WouldMove,
    [JsonStringEnumMemberName("restored")] Restored,
    [JsonStringEnumMemberName("wouldRestore")] WouldRestore,
    /// <summary>Not attempted: no longer suggested, already gone, protected, recently changed…</summary>
    [JsonStringEnumMemberName("skipped")] Skipped,
    /// <summary>Attempted and failed: in use, access denied.</summary>
    [JsonStringEnumMemberName("failed")] Failed,
}

public sealed class ItemOutcome
{
    public string Path { get; set; } = "";
    public string? FindingId { get; set; }
    public OutcomeStatus Status { get; set; }
    public long Bytes { get; set; }
    public string? Reason { get; set; }
    public List<LockHolder>? LockedBy { get; set; }
}

public sealed class LockHolder
{
    public int Pid { get; set; }
    public string App { get; set; } = "";
}

public sealed class QuarantineResult
{
    public int Schema { get; set; } = OutputSchema.Version;
    /// <summary>Null on a dry run or when nothing moved.</summary>
    public string? Batch { get; set; }
    public bool DryRun { get; set; }
    public int Done { get; set; }
    public long DoneBytes { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public List<ItemOutcome> Items { get; set; } = [];

    public void Add(ItemOutcome o)
    {
        Items.Add(o);
        switch (o.Status)
        {
            case OutcomeStatus.Skipped: Skipped++; break;
            case OutcomeStatus.Failed: Failed++; break;
            default: Done++; DoneBytes += o.Bytes; break;
        }
    }
}

public sealed class BatchInfo
{
    public string Batch { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string? ScanId { get; set; }
    public List<string> Volumes { get; set; } = [];
    /// <summary>Items still in quarantine.</summary>
    public int Items { get; set; }
    public long Bytes { get; set; }
    public int Restored { get; set; }
}

using System.Text.Json.Serialization;
using Ferahla.Core.Cli;
using Ferahla.Core.Quarantine;

namespace Ferahla.Server;

// JSON shapes of the local UI API. Internal to Ferahla (the UI ships with the exe), so they can change freely.

public sealed class StateDto
{
    public string Version { get; set; } = "";
    public string Lang { get; set; } = "en";
    public List<DriveDto> Drives { get; set; } = [];
    public List<ScanListItem> Scans { get; set; } = [];
    public JobStatus? Job { get; set; }
    public int QuarantineItems { get; set; }
    public long QuarantineBytes { get; set; }
    public string? DefaultRoot { get; set; }
    public UpdateDto? Update { get; set; }
}

public sealed class DriveDto
{
    public string Root { get; set; } = "";
    public string Label { get; set; } = "";
    public string Format { get; set; } = "";
    public string Type { get; set; } = "";
    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }
    public bool IsSystem { get; set; }
    public bool CanQuarantine { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<JobKind>))]
public enum JobKind
{
    [JsonStringEnumMemberName("scan")] Scan,
    [JsonStringEnumMemberName("quarantine")] Quarantine,
    [JsonStringEnumMemberName("restore")] Restore,
    [JsonStringEnumMemberName("delete")] Delete,
    [JsonStringEnumMemberName("analysis")] Analysis,
    [JsonStringEnumMemberName("update")] Update,
}

[JsonConverter(typeof(JsonStringEnumConverter<JobState>))]
public enum JobState
{
    [JsonStringEnumMemberName("running")] Running,
    [JsonStringEnumMemberName("done")] Done,
    [JsonStringEnumMemberName("failed")] Failed,
    [JsonStringEnumMemberName("cancelled")] Cancelled,
}

public sealed class JobStatus
{
    public string Id { get; set; } = "";
    public JobKind Kind { get; set; }
    public JobState State { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? Target { get; set; }
    public bool DryRun { get; set; }
    public long Files { get; set; }
    public long Dirs { get; set; }
    public long Bytes { get; set; }
    public string? Current { get; set; }
    /// <summary>Quarantine, restore, delete: items handled so far, of Total (planned bytes alike).</summary>
    public int Done { get; set; }
    public int Total { get; set; }
    public long DoneBytes { get; set; }
    public long TotalBytes { get; set; }
    public string? Error { get; set; }
    public ScanSummary? Scan { get; set; }
    public QuarantineResult? Result { get; set; }

    public JobStatus Clone() => (JobStatus)MemberwiseClone();
}

public sealed class TreeDto
{
    public string ScanId { get; set; } = "";
    public string Root { get; set; } = "";
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Parent { get; set; }
    /// <summary>True if the asked path is itself in the snapshot (not folded into a parent).</summary>
    public bool Exact { get; set; }
    public long Bytes { get; set; }
    public long Files { get; set; }
    public DateTime? LastWriteUtc { get; set; }
    public List<string> Markers { get; set; } = [];
    public List<string> Flags { get; set; } = [];
    public List<TreeChild> Children { get; set; } = [];
    public List<FileDto> FilesHere { get; set; } = [];
    /// <summary>Bytes in files directly here plus folders too small to keep.</summary>
    public long OtherBytes { get; set; }
    /// <summary>Suggested (non-info) bytes inside this folder.</summary>
    public long SuggestedBytes { get; set; }
    public List<string> FindingIds { get; set; } = [];
    public long? GrowthBytes { get; set; }
}

public sealed class TreeChild
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public long Bytes { get; set; }
    public long Files { get; set; }
    public DateTime? LastWriteUtc { get; set; }
    public bool HasChildren { get; set; }
    public List<string> Markers { get; set; } = [];
    public long SuggestedBytes { get; set; }
    public long? GrowthBytes { get; set; }
}

public sealed class FileDto
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public long Bytes { get; set; }
    public long Length { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public List<string> Kinds { get; set; } = [];
    public string? FindingId { get; set; }
}

public sealed class FilesDto
{
    public string ScanId { get; set; } = "";
    public int Total { get; set; }
    public long TotalBytes { get; set; }
    public List<FileDto> Items { get; set; } = [];
}

public sealed class OkDto
{
    public bool Ok { get; set; } = true;
    public string? Message { get; set; }
}

// ---- requests ----

public sealed class ScanRequest { public string? Root { get; set; } }
public sealed class LangRequest { public string Lang { get; set; } = ""; }
public sealed class QuarantineRequest { public CleanupPlan Plan { get; set; } = new(); public bool DryRun { get; set; } }
public sealed class BatchRequest
{
    public string Batch { get; set; } = "";
    public List<string>? Keys { get; set; }
    /// <summary>Must be "delete" for a permanent delete: a second, explicit signal besides the token.</summary>
    public string? Confirm { get; set; }
}
public sealed class PathRequest { public string Path { get; set; } = ""; }
public sealed class IdRequest { public string Id { get; set; } = ""; }
public sealed class VerifyRequest { public string FindingId { get; set; } = ""; public string? Scan { get; set; } }
public sealed class NoteRequest { public string FindingId { get; set; } = ""; public string? Text { get; set; } public string? Verdict { get; set; } }

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StateDto))]
[JsonSerializable(typeof(JobStatus))]
[JsonSerializable(typeof(TreeDto))]
[JsonSerializable(typeof(FilesDto))]
[JsonSerializable(typeof(OkDto))]
[JsonSerializable(typeof(UpdateDto))]
[JsonSerializable(typeof(UpdateSettingsRequest))]
[JsonSerializable(typeof(ErrorResult))]
[JsonSerializable(typeof(ScanRequest))]
[JsonSerializable(typeof(LangRequest))]
[JsonSerializable(typeof(QuarantineRequest))]
[JsonSerializable(typeof(BatchRequest))]
[JsonSerializable(typeof(PathRequest))]
[JsonSerializable(typeof(IdRequest))]
[JsonSerializable(typeof(NoteRequest))]
[JsonSerializable(typeof(VerifyRequest))]
[JsonSerializable(typeof(Ferahla.Core.Findings.VerifyResult))]
[JsonSerializable(typeof(List<Ferahla.Windows.AppEntry>))]
[JsonSerializable(typeof(Ferahla.Core.Findings.FindingsResult))]
[JsonSerializable(typeof(Ferahla.Core.Diff.DiffResult))]
[JsonSerializable(typeof(List<BatchInfo>))]
[JsonSerializable(typeof(Ferahla.Core.Notes.NoteFile))]
[JsonSerializable(typeof(List<ScanListItem>))]
internal sealed partial class ApiJson : JsonSerializerContext
{
    private static ApiJson? _web;

    /// <summary>Readable Turkish letters in responses; the UI never puts this into HTML without escaping.</summary>
    public static ApiJson Web => _web ??= new ApiJson(new System.Text.Json.JsonSerializerOptions(Default.Options)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
}

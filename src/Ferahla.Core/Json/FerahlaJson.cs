using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ferahla.Core.Model;

namespace Ferahla.Core.Json;

/// <summary>
/// The single JSON context for Ferahla. Relaxed escaping keeps Turkish characters readable;
/// output never goes into HTML without separate escaping.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(Snapshot))]
[JsonSerializable(typeof(Rules.RuleFile))]
[JsonSerializable(typeof(Findings.FindingsResult))]
[JsonSerializable(typeof(Diff.DiffResult))]
[JsonSerializable(typeof(Report.ReportData))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(Cli.InspectResult))]
[JsonSerializable(typeof(Cli.ScanSummary))]
[JsonSerializable(typeof(Cli.ErrorResult))]
[JsonSerializable(typeof(List<Cli.ScanListItem>))]
[JsonSerializable(typeof(Cli.ScanListItem))]
[JsonSerializable(typeof(Quarantine.CleanupPlan))]
[JsonSerializable(typeof(Quarantine.QuarantineManifest))]
[JsonSerializable(typeof(Quarantine.QuarantineResult))]
[JsonSerializable(typeof(List<Quarantine.BatchInfo>))]
[JsonSerializable(typeof(Notes.NoteFile))]
public sealed partial class FerahlaJson : JsonSerializerContext
{
    private static FerahlaJson? _relaxed;

    /// <summary>Same context with relaxed escaping (for CLI output and the HTML report's data island).</summary>
    public static FerahlaJson Relaxed => _relaxed ??= new FerahlaJson(new JsonSerializerOptions(Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    public static FerahlaJson Indented => _indented ??= new FerahlaJson(new JsonSerializerOptions(Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    });
    private static FerahlaJson? _indented;
}

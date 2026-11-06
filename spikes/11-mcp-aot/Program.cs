// Spike 11: does the official C# MCP SDK (ModelContextProtocol) work as a stdio server under NativeAOT?
// Results: docs/spikes.md
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

var builder = Host.CreateApplicationBuilder(args);
// stdout belongs to the MCP protocol; all logs go to stderr
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

// The default encoder escapes non-ASCII letters (e.g. Turkish ö) as \u00F6; tool results reach Claude as text,
// so that wastes tokens. Output goes to JSON-RPC, not HTML, so the relaxed encoder is safe.
var json = new JsonSerializerOptions(ToolJson.Default.Options)
{
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
};

builder.Services
    .AddMcpServer(o => o.ServerInfo = new() { Name = "ferahla", Version = "0.0.1-spike" })
    .WithStdioServerTransport()
    .WithTools<FerahlaTools>(json);

await builder.Build().RunAsync();

[McpServerToolType]
sealed class FerahlaTools
{
    [McpServerTool(Name = "get_summary", ReadOnly = true), Description("Summary of the latest scan: disk size, free space, finding counts.")]
    public static Summary GetSummary() =>
        new("C:", 500.0, 42.0, new Dictionary<string, int> { ["safe"] = 3, ["review"] = 1 });

    [McpServerTool(Name = "list_findings", ReadOnly = true), Description("Lists findings, filtered by risk if given.")]
    public static Finding[] ListFindings(
        [Description("safe | regenerable | review | personal")] string? risk = null,
        [Description("Maximum number of findings")] int limit = 10)
    {
        Finding[] all =
        [
            new("f1", "safe", "Sample build cache", 4.2, ["known cache location"]),
            new("f2", "review", "Sample scratch folder — look inside first", 1.5, ["not there in the previous scan"]),
        ];
        return all.Where(f => risk is null || f.Risk == risk).Take(limit).ToArray();
    }

    [McpServerTool(Name = "add_note"), Description("Adds Claude's assessment to a finding as a note.")]
    public static NoteResult AddNote(string findingId, string text) =>
        new(findingId, text.Length, DateTimeOffset.UtcNow);
}

record Summary(string Drive, double TotalGb, double FreeGb, Dictionary<string, int> FindingsByRisk);
record Finding(string Id, string Risk, string Title, double SizeGb, string[] Evidence);
record NoteResult(string FindingId, int Chars, DateTimeOffset SavedAt);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Summary))]
[JsonSerializable(typeof(Finding[]))]
[JsonSerializable(typeof(NoteResult))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
partial class ToolJson : JsonSerializerContext;

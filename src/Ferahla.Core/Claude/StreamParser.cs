using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ferahla.Core.Claude;

[JsonConverter(typeof(JsonStringEnumConverter<AnalysisEventKind>))]
public enum AnalysisEventKind
{
    [JsonStringEnumMemberName("init")] Init,
    /// <summary>Claude is sending a request (thinking).</summary>
    [JsonStringEnumMemberName("thinking")] Thinking,
    /// <summary>A piece of Claude's text, streamed.</summary>
    [JsonStringEnumMemberName("text")] Text,
    [JsonStringEnumMemberName("tool")] Tool,
    [JsonStringEnumMemberName("toolResult")] ToolResult,
    [JsonStringEnumMemberName("note")] Note,
    [JsonStringEnumMemberName("rateLimit")] RateLimit,
    [JsonStringEnumMemberName("result")] Result,
    [JsonStringEnumMemberName("error")] Error,
}

public sealed class AnalysisEvent
{
    public AnalysisEventKind Kind { get; set; }
    public int Seq { get; set; }
    public string? Text { get; set; }
    /// <summary>Tool name without the <c>mcp__ferahla__</c> prefix.</summary>
    public string? Tool { get; set; }
    public string? ToolId { get; set; }
    /// <summary>Tool input as compact JSON.</summary>
    public string? Input { get; set; }
    public bool? IsError { get; set; }
    public string? FindingId { get; set; }
    public string? Verdict { get; set; }
    public string? SessionId { get; set; }
    public string? Model { get; set; }
    public List<string>? Tools { get; set; }
    public bool? McpConnected { get; set; }
    public UsageInfo? Usage { get; set; }
    public string? Subtype { get; set; }
    public int? Turns { get; set; }
    public long? DurationMs { get; set; }
}

/// <summary>Plan usage from <c>rate_limit_event</c>: shown so the user knows what an analysis costs them.</summary>
public sealed class UsageInfo
{
    public string Status { get; set; } = "";
    public double? FiveHour { get; set; }
    public double? SevenDay { get; set; }
    public long? FiveHourResetsAt { get; set; }
    public long? SevenDayResetsAt { get; set; }
    public DateTimeOffset At { get; set; }
}

/// <summary>
/// Turns <c>claude -p --output-format stream-json</c> lines into UI events. Looks at <c>type</c> then the subtype;
/// unknown events and fields are ignored, since Claude Code adds new ones over time (see docs/spikes.md 2–4).
/// </summary>
public sealed class StreamParser
{
    public const string ToolPrefix = "mcp__ferahla__";
    private int _seq;
    private readonly Dictionary<int, (string Id, string Name)> _blocks = [];

    public List<AnalysisEvent> Feed(string line)
    {
        var list = new List<AnalysisEvent>();
        if (string.IsNullOrWhiteSpace(line)) return list;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException) { return list; }
        using (doc)
        {
            var e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Object) return list;
            switch (Str(e, "type"))
            {
                case "system" when Str(e, "subtype") == "init":
                    var tools = e.TryGetProperty("tools", out var t) && t.ValueKind == JsonValueKind.Array
                        ? t.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];
                    var connected = e.TryGetProperty("mcp_servers", out var ms) && ms.ValueKind == JsonValueKind.Array
                        && ms.EnumerateArray().Any(s => Str(s, "name") == "ferahla" && Str(s, "status") == "connected");
                    Add(list, new AnalysisEvent { Kind = AnalysisEventKind.Init, SessionId = Str(e, "session_id"), Model = Str(e, "model"), Tools = tools, McpConnected = connected });
                    break;
                case "system" when Str(e, "subtype") == "status" && Str(e, "status") == "requesting":
                    Add(list, new AnalysisEvent { Kind = AnalysisEventKind.Thinking });
                    break;
                case "stream_event":
                    StreamEvent(e, list);
                    break;
                case "assistant":
                    if (e.TryGetProperty("message", out var m) && m.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                        foreach (var c in content.EnumerateArray())
                        {
                            if (Str(c, "type") != "tool_use") continue;
                            var name = Short(Str(c, "name"));
                            var input = c.TryGetProperty("input", out var inp) ? inp.GetRawText() : "{}";
                            var ev = new AnalysisEvent { Kind = AnalysisEventKind.Tool, Tool = name, ToolId = Str(c, "id"), Input = input };
                            Add(list, ev);
                            if (name == "add_note" && inp.ValueKind == JsonValueKind.Object)
                                Add(list, new AnalysisEvent
                                {
                                    Kind = AnalysisEventKind.Note, ToolId = ev.ToolId, FindingId = Str(inp, "findingId"),
                                    Text = Str(inp, "text"), Verdict = Str(inp, "verdict") ?? "check",
                                });
                        }
                    break;
                case "user":
                    if (e.TryGetProperty("message", out var um) && um.TryGetProperty("content", out var uc) && uc.ValueKind == JsonValueKind.Array)
                        foreach (var c in uc.EnumerateArray())
                        {
                            if (Str(c, "type") != "tool_result") continue;
                            var text = "";
                            if (c.TryGetProperty("content", out var rc))
                                text = rc.ValueKind == JsonValueKind.String ? rc.GetString() ?? ""
                                    : rc.ValueKind == JsonValueKind.Array ? string.Concat(rc.EnumerateArray().Select(x => Str(x, "text") ?? "")) : "";
                            if (text.Length > 1200) text = text[..1200] + "…";
                            Add(list, new AnalysisEvent
                            {
                                Kind = AnalysisEventKind.ToolResult, ToolId = Str(c, "tool_use_id"), Text = text,
                                IsError = c.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True ? true : null,
                            });
                        }
                    break;
                case "rate_limit_event":
                    if (e.TryGetProperty("rate_limit_info", out var ri)) Add(list, new AnalysisEvent { Kind = AnalysisEventKind.RateLimit, Usage = Usage(ri) });
                    break;
                case "result":
                    Add(list, new AnalysisEvent
                    {
                        Kind = AnalysisEventKind.Result, Subtype = Str(e, "subtype"), Text = Str(e, "result"),
                        IsError = e.TryGetProperty("is_error", out var err) && err.ValueKind == JsonValueKind.True,
                        Turns = e.TryGetProperty("num_turns", out var nt) && nt.TryGetInt32(out var n) ? n : null,
                        DurationMs = e.TryGetProperty("duration_ms", out var dm) && dm.TryGetInt64(out var d) ? d : null,
                        SessionId = Str(e, "session_id"),
                    });
                    break;
            }
        }
        return list;
    }

    private void StreamEvent(JsonElement e, List<AnalysisEvent> list)
    {
        if (!e.TryGetProperty("event", out var ev)) return;
        // Text of sub-agents etc. (parent_tool_use_id set) isn't shown; there are none with our tool set anyway.
        if (e.TryGetProperty("parent_tool_use_id", out var parent) && parent.ValueKind == JsonValueKind.String) return;
        switch (Str(ev, "type"))
        {
            case "content_block_start":
                if (ev.TryGetProperty("content_block", out var cb) && Str(cb, "type") == "tool_use" && ev.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var i))
                    _blocks[i] = (Str(cb, "id") ?? "", Short(Str(cb, "name")));
                break;
            case "content_block_delta":
                if (ev.TryGetProperty("delta", out var d) && Str(d, "type") == "text_delta" && Str(d, "text") is { Length: > 0 } text)
                    Add(list, new AnalysisEvent { Kind = AnalysisEventKind.Text, Text = text });
                break;
        }
    }

    private static UsageInfo Usage(JsonElement ri)
    {
        var u = new UsageInfo { Status = Str(ri, "status") ?? "", At = DateTimeOffset.UtcNow };
        if (ri.TryGetProperty("unifiedWindows", out var w))
        {
            if (w.TryGetProperty("five_hour", out var f)) { u.FiveHour = Num(f, "utilization"); u.FiveHourResetsAt = Long(f, "resetsAt"); }
            if (w.TryGetProperty("seven_day", out var s)) { u.SevenDay = Num(s, "utilization"); u.SevenDayResetsAt = Long(s, "resetsAt"); }
        }
        else if (Str(ri, "rateLimitType") == "five_hour") { u.FiveHour = Num(ri, "utilization"); u.FiveHourResetsAt = Long(ri, "resetsAt"); }
        return u;
    }

    private void Add(List<AnalysisEvent> list, AnalysisEvent ev) { ev.Seq = ++_seq; list.Add(ev); }

    private static string Short(string? name) => name is null ? "" : name.StartsWith(ToolPrefix, StringComparison.Ordinal) ? name[ToolPrefix.Length..] : name;
    private static string? Str(JsonElement e, string p) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static double? Num(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.TryGetDouble(out var d) ? d : null;
    private static long? Long(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.TryGetInt64(out var d) ? d : null;

    /// <summary>The init check: only Ferahla's tools, and Ferahla's MCP server connected. Otherwise the run stops.</summary>
    public static string? CheckInit(AnalysisEvent init)
    {
        if (init.McpConnected != true) return "Ferahla's MCP server did not connect";
        var foreign = (init.Tools ?? []).Where(t => !t.StartsWith(ToolPrefix, StringComparison.Ordinal)).ToList();
        return foreign.Count > 0 ? "unexpected tools are enabled: " + string.Join(", ", foreign.Take(5)) : null;
    }
}

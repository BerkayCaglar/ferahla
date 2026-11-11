using System.Diagnostics;
using System.Text.Json;

namespace Ferahla.Core.Claude;

/// <summary>
/// Turns <c>codex exec --json</c> lines (JSONL: <c>thread.started</c>, <c>turn.*</c>, <c>item.started</c>/<c>item.completed</c>,
/// <c>error</c>) into the same UI events as <see cref="StreamParser"/>. Unknown events and fields are ignored.
/// Codex sends no tool list up front, so <see cref="Check"/> looks at every event instead: anything but Ferahla's MCP
/// tools, messages and reasoning stops the run (see docs/spikes.md, Codex).
/// </summary>
public sealed class CodexParser : IAgentParser
{
    public const string Server = "ferahla";
    private int _seq;
    private readonly HashSet<string> _toolsSeen = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _toolCalls;
    private string? _lastError;

    /// <summary>The model Ferahla asked for (Codex doesn't echo it); shown on the init event.</summary>
    public string? Model { get; init; }

    public int ToolCalls => _toolCalls;

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
                case "thread.started":
                    Add(list, new AnalysisEvent { Kind = AnalysisEventKind.Init, SessionId = Str(e, "thread_id"), Model = Model, McpConnected = true });
                    break;
                case "turn.started":
                    Add(list, new AnalysisEvent { Kind = AnalysisEventKind.Thinking });
                    break;
                case "item.started":
                case "item.updated":
                case "item.completed":
                    if (e.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object)
                        Item(item, Str(e, "type") == "item.completed", list);
                    break;
                case "turn.completed":
                {
                    long? tokens = null;
                    if (e.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                        tokens = (Long(u, "input_tokens") ?? 0) + (Long(u, "output_tokens") ?? 0);
                    Add(list, new AnalysisEvent
                    {
                        Kind = AnalysisEventKind.Result, Subtype = "success", Turns = _toolCalls + 1, DurationMs = _clock.ElapsedMilliseconds, Tokens = tokens,
                    });
                    break;
                }
                case "turn.failed":
                {
                    var msg = e.TryGetProperty("error", out var err) ? Message(Str(err, "message")) : null;
                    if (msg is not null && msg != _lastError) Add(list, new AnalysisEvent { Kind = AnalysisEventKind.Error, Text = msg });
                    Add(list, new AnalysisEvent { Kind = AnalysisEventKind.Result, Subtype = "error", IsError = true, Text = msg, Turns = _toolCalls + 1, DurationMs = _clock.ElapsedMilliseconds });
                    break;
                }
                case "error":
                    if (Message(Str(e, "message")) is { } m)
                    {
                        _lastError = m;
                        Add(list, new AnalysisEvent { Kind = AnalysisEventKind.Error, Text = m });
                    }
                    break;
            }
        }
        return list;
    }

    private void Item(JsonElement item, bool completed, List<AnalysisEvent> list)
    {
        var id = Str(item, "id") ?? "";
        switch (Str(item, "type"))
        {
            case "agent_message":
                if (completed && Str(item, "text") is { Length: > 0 } text) Add(list, new AnalysisEvent { Kind = AnalysisEventKind.Text, Text = text });
                break;
            case "reasoning":
            case "todo_list":
            case "error": // non-fatal warnings (e.g. model metadata); fatal ones come as "error" / "turn.failed"
                break;
            case "mcp_tool_call":
            {
                var server = Str(item, "server") ?? "";
                var tool = Str(item, "tool") ?? "";
                var input = "{}";
                if (item.TryGetProperty("arguments", out var a))
                    input = a.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? a.GetRawText() : a.ValueKind == JsonValueKind.String ? a.GetString() ?? "{}" : "{}";
                if (_toolsSeen.Add(id))
                {
                    _toolCalls++;
                    var ev = new AnalysisEvent { Kind = AnalysisEventKind.Tool, Tool = server == Server ? tool : server + "/" + tool, ToolId = id, Input = input };
                    if (server != Server) ev.Subtype = "foreign";
                    Add(list, ev);
                    if (server == Server) Notes(id, tool, input, list);
                }
                if (completed)
                {
                    var failed = Str(item, "status") is "failed" or "error"
                                 || (item.TryGetProperty("error", out var er) && er.ValueKind is JsonValueKind.Object or JsonValueKind.String);
                    var output = failed ? ErrorText(item) : ResultText(item);
                    if (output.Length > 1200) output = output[..1200] + "…";
                    Add(list, new AnalysisEvent { Kind = AnalysisEventKind.ToolResult, ToolId = id, Text = output, IsError = failed ? true : null });
                }
                break;
            }
            default:
                // command_execution, file_change, web_search, …: none of these may happen in Ferahla's runs.
                if (_toolsSeen.Add(id))
                    Add(list, new AnalysisEvent { Kind = AnalysisEventKind.Tool, Tool = Str(item, "type") ?? "unknown", ToolId = id, Input = "{}", Subtype = "foreign" });
                break;
        }
    }

    private void Notes(string id, string tool, string input, List<AnalysisEvent> list)
    {
        if (tool is not ("add_note" or "add_notes")) return;
        try
        {
            using var d = JsonDocument.Parse(input);
            var r = d.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return;
            if (tool == "add_note") AddNote(list, id, r);
            else if (r.TryGetProperty("notes", out var notes) && notes.ValueKind == JsonValueKind.Array)
                foreach (var n in notes.EnumerateArray())
                    if (n.ValueKind == JsonValueKind.Object) AddNote(list, id, n);
        }
        catch (JsonException) { }
    }

    /// <summary>Null when the event is fine; otherwise why the run must stop.</summary>
    public static string? Check(AnalysisEvent ev) =>
        ev.Kind == AnalysisEventKind.Tool && ev.Subtype == "foreign" ? "a tool outside Ferahla was used: " + ev.Tool : null;

    private static string ResultText(JsonElement item)
    {
        if (!item.TryGetProperty("result", out var r)) return "";
        if (r.ValueKind == JsonValueKind.String) return r.GetString() ?? "";
        if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array)
            return string.Concat(c.EnumerateArray().Select(x => Str(x, "text") ?? ""));
        return r.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? "" : r.GetRawText();
    }

    private static string ErrorText(JsonElement item)
    {
        if (item.TryGetProperty("error", out var er))
        {
            if (er.ValueKind == JsonValueKind.String) return er.GetString() ?? "";
            if (er.ValueKind == JsonValueKind.Object) return Str(er, "message") ?? er.GetRawText();
        }
        return ResultText(item);
    }

    /// <summary>Codex wraps API errors as JSON inside the message; the inner message is what the user should read.</summary>
    internal static string? Message(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        if (s.StartsWith('{'))
            try
            {
                using var d = JsonDocument.Parse(s);
                var r = d.RootElement;
                if (r.TryGetProperty("error", out var er) && er.ValueKind == JsonValueKind.Object && Str(er, "message") is { Length: > 0 } inner) return inner;
                if (Str(r, "message") is { Length: > 0 } m) return m;
            }
            catch (JsonException) { }
        return s.Length > 600 ? s[..600] + "…" : s;
    }

    private void Add(List<AnalysisEvent> list, AnalysisEvent ev) { ev.Seq = ++_seq; list.Add(ev); }

    private void AddNote(List<AnalysisEvent> list, string? toolId, JsonElement n) => Add(list, new AnalysisEvent
    {
        Kind = AnalysisEventKind.Note, ToolId = toolId, FindingId = Str(n, "findingId"), Text = Str(n, "text"), Verdict = Str(n, "verdict") ?? "check",
    });

    private static string? Str(JsonElement e, string p) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static long? Long(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.TryGetInt64(out var d) ? d : null;
}

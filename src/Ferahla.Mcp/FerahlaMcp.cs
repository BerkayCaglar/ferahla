using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using Ferahla.Core;
using Ferahla.Core.Findings;
using Ferahla.Core.Model;
using Ferahla.Core.Notes;
using Ferahla.Core.Rules;
using Ferahla.Core.Storage;
using Ferahla.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Ferahla.Mcp;

/// <summary>
/// <c>ferahla mcp</c>: a stdio MCP server for Claude's analysis. Tools read the scan and add notes; there is
/// deliberately no tool that moves, deletes or opens anything. stdout is the protocol; logs go to stderr.
/// </summary>
public static class FerahlaMcp
{
    public static async Task<int> RunAsync(string home, string? scanId, CancellationToken ct)
    {
        Session.Init(home, scanId);
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        // Tool results reach Claude as text: readable non-ASCII letters save tokens. It is JSON-RPC, not HTML.
        var json = new JsonSerializerOptions(McpJson.Default.Options) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        builder.Services
            .AddMcpServer(o => o.ServerInfo = new() { Name = "ferahla", Version = typeof(FerahlaMcp).Assembly.GetName().Version?.ToString(3) ?? "0.0.0" })
            .WithStdioServerTransport()
            .WithTools<FerahlaTools>(json);
        await builder.Build().RunAsync(ct);
        return 0;
    }
}

/// <summary>The scan the tools work on, loaded once per process.</summary>
internal static class Session
{
    private static string _home = "";
    private static string? _scanId;
    private static (Snapshot Snap, SnapshotIndex Index, FindingsResult Findings, Snapshot? Prev)? _loaded;
    private static readonly Lock Gate = new();

    public static void Init(string home, string? scanId) { _home = home; _scanId = scanId; _loaded = null; }

    public static string Home => _home;
    public static NoteStore Notes => new(_home);

    public static (Snapshot Snap, SnapshotIndex Index, FindingsResult Findings, Snapshot? Prev) Get()
    {
        lock (Gate)
        {
            if (_loaded is { } l) return l;
            var store = new SnapshotStore(_home);
            var id = _scanId ?? store.Latest() ?? throw new InvalidOperationException("No scans yet. The user has to run a scan first.");
            var snap = store.Load(id);
            var prev = FindingsService.Previous(store, snap);
            var findings = FindingsService.Compute(snap, prev);
            _loaded = (snap, new SnapshotIndex(snap), findings, prev);
            return _loaded.Value;
        }
    }
}

[McpServerToolType]
internal sealed class FerahlaTools
{
    [McpServerTool(Name = "get_summary", ReadOnly = true, Idempotent = true)]
    [Description("Overview of the scan: drive size and free space, suggested bytes per risk, biggest categories, top folders, and what changed since the previous scan.")]
    public static McpSummary GetSummary() => Tools.Summary();

    [McpServerTool(Name = "list_findings", ReadOnly = true, Idempotent = true)]
    [Description("Ferahla's cleanup suggestions, biggest first, with evidence and any existing note. Use offset to page.")]
    public static McpFindingPage ListFindings(
        [Description("Only this risk: safe | regenerable | review | personal")] string? risk = null,
        [Description("Only this category, e.g. cache, build, installer, archive, duplicate, old-version")] string? category = null,
        [Description("Skip findings smaller than this many MB")] double minMb = 0,
        [Description("Page size, at most 50")] int limit = 25,
        [Description("How many to skip")] int offset = 0) => Tools.Findings(risk, category, minMb, limit, offset);

    [McpServerTool(Name = "inspect_path", ReadOnly = true, Idempotent = true)]
    [Description("Size, file count, last change, biggest subfolders and notable files of a folder in the scan, and the findings inside it.")]
    public static McpInspect InspectPath([Description("Full path, e.g. D:\\Projects\\app")] string path) => Tools.Inspect(path);

    [McpServerTool(Name = "verify_finding", ReadOnly = true)]
    [Description("Checks a finding against the disk now: do the items still exist, their current size and last change; for duplicates, compares the copies byte by byte.")]
    public static McpVerify VerifyFinding([Description("Finding id, f- and 10 hex digits")] string findingId) => Tools.Verify(findingId);

    [McpServerTool(Name = "get_diff", ReadOnly = true, Idempotent = true)]
    [Description("Folders that grew or shrank the most since the previous scan of the same drive.")]
    public static McpDiff GetDiff([Description("Only changes at least this many MB")] double minMb = 200) => Tools.Diff(minMb);

    [McpServerTool(Name = "add_note", Destructive = false)]
    [Description("Saves your assessment of one finding; the user sees it next to the suggestion. 1-3 short sentences. Replaces your earlier note on the same finding.")]
    public static McpNoteResult AddNote(
        [Description("Finding id, f- and 10 hex digits")] string findingId,
        [Description("The note: what it is, what to do, and the deciding fact")] string text,
        [Description("remove (safe to quarantine) | check (look first) | keep")] string verdict = "check") => Tools.Note(findingId, text, verdict);
}

internal static class Tools
{
    private const long MB = 1L << 20;
    private static double Mb(long b) => Math.Round(b / (double)MB, 1);

    public static McpSummary Summary()
    {
        var (snap, ix, f, prev) = Session.Get();
        var actionable = f.Findings.Where(x => x.Action != ActionKind.Info).ToList();
        var notes = Session.Notes.Load().Notes;
        var s = new McpSummary
        {
            ScanId = snap.Id, Root = snap.Root, ScannedAt = snap.ScannedAt,
            DriveTotalMb = Mb(snap.Volume.TotalBytes), DriveFreeMb = Mb(snap.Volume.FreeBytes),
            Files = snap.Stats.Files, Findings = f.Findings.Count,
            NotesAlready = f.Findings.Count(x => notes.ContainsKey(x.Id)),
            PreviousScanId = prev?.Id,
        };
        foreach (var g in actionable.GroupBy(x => x.Risk))
            s.SuggestedMbByRisk[RiskName(g.Key)] = Mb(g.Sum(x => x.Bytes));
        foreach (var g in actionable.GroupBy(x => x.Category).OrderByDescending(g => g.Sum(x => x.Bytes)).Take(8))
            s.TopCategoriesMb[g.Key] = Mb(g.Sum(x => x.Bytes));
        foreach (var c in ix.Children(0).OrderByDescending(c => ix.Dir(c).Size).Take(10))
            s.TopFolders.Add(new McpSize { Path = ix.PathOf(c), Mb = Mb(ix.Dir(c).Size) });
        if (prev is not null)
        {
            s.FreeChangeMb = Mb(snap.Volume.FreeBytes - prev.Volume.FreeBytes);
            s.DaysSincePrevious = Math.Round((snap.ScannedAt - prev.ScannedAt).TotalDays, 1);
        }
        return s;
    }

    public static McpFindingPage Findings(string? risk, string? category, double minMb, int limit, int offset)
    {
        var (_, _, f, _) = Session.Get();
        var notes = Session.Notes.Load().Notes;
        IEnumerable<Finding> q = f.Findings.OrderByDescending(x => x.Bytes);
        if (!string.IsNullOrWhiteSpace(risk)) q = q.Where(x => RiskName(x.Risk) == risk.Trim().ToLowerInvariant());
        if (!string.IsNullOrWhiteSpace(category)) q = q.Where(x => x.Category.Equals(category.Trim(), StringComparison.OrdinalIgnoreCase));
        if (minMb > 0) q = q.Where(x => x.Bytes >= minMb * MB);
        var all = q.ToList();
        limit = Math.Clamp(limit, 1, 50);
        offset = Math.Max(0, offset);
        return new McpFindingPage
        {
            Total = all.Count, Offset = offset,
            Findings = all.Skip(offset).Take(limit).Select(x => new McpFinding
            {
                Id = x.Id, Rule = x.RuleId, Category = x.Category, Risk = RiskName(x.Risk), Action = x.Action.ToString().ToLowerInvariant(),
                Title = x.Title, Mb = Mb(x.Bytes), Reason = x.Reason, WhatBreaks = x.WhatBreaks, Evidence = x.Evidence,
                Items = x.Items.Where(i => i.NestedIn is null).Take(6).Select(i => new McpSize { Path = i.Path, Mb = Mb(i.Bytes) }).ToList(),
                MoreItems = Math.Max(0, x.Items.Count(i => i.NestedIn is null) - 6),
                LastChange = x.LastWriteUtc?.ToString("yyyy-MM-dd"), IsNew = x.IsNew ? true : null,
                GrowthMb = x.GrowthBytes is long g && Math.Abs(g) >= 50 * MB ? Mb(g) : null,
                ExistingNote = notes.TryGetValue(x.Id, out var n) ? n.Text : null,
            }).ToList(),
        };
    }

    public static McpInspect Inspect(string path)
    {
        var (snap, ix, f, _) = Session.Get();
        var want = PathUtil.Normalize(path);
        if (!PathUtil.IsUnder(want, snap.Root)) return new McpInspect { Path = want, Error = $"Not in this scan; it covers {snap.Root}." };
        var d = ix.FindNearest(want);
        var dir = ix.Dir(d);
        var here = ix.PathOf(d);
        var r = new McpInspect
        {
            Path = here, Exact = string.Equals(here, want, StringComparison.OrdinalIgnoreCase),
            Mb = Mb(dir.Size), Files = dir.FileCount, LastChange = dir.LastWriteUtc?.ToString("yyyy-MM-dd"),
            ProjectMarkers = dir.Markers == DirMarkers.None ? null : dir.Markers.ToString(),
        };
        if (!r.Exact) r.Error = "That exact folder was too small to keep in the scan; showing the closest kept parent.";
        foreach (var c in ix.Children(d).OrderByDescending(c => ix.Dir(c).Size).Take(15))
            r.Subfolders.Add(new McpSize { Path = ix.Dir(c).Name, Mb = Mb(ix.Dir(c).Size), LastChange = ix.Dir(c).LastWriteUtc?.ToString("yyyy-MM-dd") });
        foreach (var fe in ix.FilesIn(d).Select(i => snap.Files[i]).OrderByDescending(x => x.Size).Take(10))
            r.NotableFiles.Add(new McpSize { Path = fe.Name, Mb = Mb(fe.Size), LastChange = fe.LastWriteUtc.ToString("yyyy-MM-dd") });
        r.FindingIds = f.Findings.Where(x => x.Items.Any(i => PathUtil.IsUnder(i.Path, here))).Select(x => x.Id).Take(30).ToList();
        return r;
    }

    public static McpVerify Verify(string findingId)
    {
        var (_, _, f, _) = Session.Get();
        var x = f.Findings.FirstOrDefault(y => y.Id == findingId?.Trim());
        if (x is null) return new McpVerify { FindingId = findingId ?? "", Verdict = "unknown finding id" };
        var r = Verifier.Verify(x);
        return new McpVerify
        {
            FindingId = x.Id,
            Verdict = r.Ok ? "verified" : "not fully verified",
            Checks = r.Checks,
            Items = r.Items.Select(i => new McpItemCheck { Path = i.Path, Exists = i.Exists, ScanMb = Mb(i.ScanBytes), NowMb = i.NowBytes is long b ? Mb(b) : null, LastChange = i.LastWriteUtc?.ToString("yyyy-MM-dd HH:mm"), Note = i.Note }).ToList(),
        };
    }
    public static McpDiff Diff(double minMb)
    {
        var (snap, _, _, prev) = Session.Get();
        if (prev is null) return new McpDiff { Note = "No previous scan of this drive yet." };
        var d = Core.Diff.SnapshotDiff.Compare(prev, snap, (long)(Math.Max(1, minMb) * MB));
        return new McpDiff
        {
            From = prev.Id, To = snap.Id, Days = Math.Round((snap.ScannedAt - prev.ScannedAt).TotalDays, 1), FreeChangeMb = Mb(d.FreeBytesDelta),
            Changes = d.Entries.Take(25).Select(e => new McpChange { Path = e.Path, ChangeMb = Mb(e.Delta), New = e.IsNew ? true : null, Gone = e.IsGone ? true : null }).ToList(),
        };
    }

    public static McpNoteResult Note(string findingId, string text, string verdict)
    {
        var (snap, _, f, _) = Session.Get();
        var id = (findingId ?? "").Trim();
        if (f.Findings.All(x => x.Id != id)) return new McpNoteResult { FindingId = id, Saved = false, Error = "Unknown finding id; use an id from list_findings." };
        var v = (verdict ?? "").Trim().ToLowerInvariant() switch { "remove" => NoteVerdict.Remove, "keep" => NoteVerdict.Keep, "check" => NoteVerdict.Check, _ => NoteVerdict.None };
        var n = Session.Notes.Set(id, text, v, "claude", snap.Id, DateTimeOffset.UtcNow);
        return new McpNoteResult { FindingId = id, Saved = true, Chars = n.Text.Length };
    }

    private static string RiskName(Risk r) => r.ToString().ToLowerInvariant();
}

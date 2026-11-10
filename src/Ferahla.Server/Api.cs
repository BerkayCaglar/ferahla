using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Ferahla.Core;
using Ferahla.Core.Claude;
using Ferahla.Core.Cli;
using Ferahla.Core.Model;
using Ferahla.Core.Notes;
using Ferahla.Core.Quarantine;
using Ferahla.Core.Report;
using Ferahla.Core.Rules;
using Ferahla.Core.Text;
using Ferahla.Windows;
using Ferahla.Windows.Quarantine;
using Ferahla.Windows.Scanning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Ferahla.Server;

/// <summary>The UI's API. Handlers are plain <see cref="RequestDelegate"/>s with source-generated JSON (NativeAOT).</summary>
[SupportedOSPlatform("windows")]
internal sealed class Api(AppState state, Jobs jobs, EventHub hub, Analysis analysis, Chat chat, Action requestStop)
{
    private static readonly string Version = typeof(Api).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>FERAHLA_DEMO=1: nothing from this machine (drives, apps) is shown; used for README screenshots.</summary>
    private static bool Demo => Environment.GetEnvironmentVariable("FERAHLA_DEMO") == "1";

    public void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/ping", ctx => Json(ctx, new OkDto(), ApiJson.Web.OkDto));
        app.MapGet("/api/state", ctx => Json(ctx, BuildState(), ApiJson.Web.StateDto));
        app.MapGet("/api/events", ctx => hub.Stream(ctx, () =>
            $"event: hello\ndata: {JsonSerializer.Serialize(BuildState(), ApiJson.Web.StateDto)}\n\n"));
        app.MapGet("/api/findings", ctx => Guarded(ctx, () => Json(ctx, state.Findings(ctx.Request.Query["scan"]), ApiJson.Web.FindingsResult)));
        app.MapGet("/api/diff", ctx => Guarded(ctx, () => Json(ctx, state.Diff(ctx.Request.Query["scan"], ctx.Request.Query["from"]) ?? new Core.Diff.DiffResult(), ApiJson.Web.DiffResult)));
        app.MapGet("/api/tree", ctx => Guarded(ctx, () => Json(ctx, Tree(ctx.Request.Query["scan"], ctx.Request.Query["path"]), ApiJson.Web.TreeDto)));
        app.MapGet("/api/files", ctx => Guarded(ctx, () => Json(ctx, Files(ctx.Request.Query["scan"], ctx.Request.Query["kind"]), ApiJson.Web.FilesDto)));
        app.MapGet("/api/quarantine", ctx => Json(ctx, PlanRunner.Engine(state.Store).List(), ApiJson.Web.ListBatchInfo));
        app.MapGet("/api/notes", ctx => Json(ctx, new NoteStore(state.Store.Home).Load(), ApiJson.Web.NoteFile));
        app.MapGet("/api/report", Report);

        app.MapPost("/api/lang", ctx => Body(ctx, ApiJson.Web.LangRequest, r =>
        {
            if (r.Lang is not ("en" or "tr")) return Error(ctx, 400, "lang must be en or tr");
            Lang.Current = r.Lang;
            state.InvalidateFindings();
            hub.Changed("lang");
            return Json(ctx, new OkDto(), ApiJson.Web.OkDto);
        }));
        app.MapPost("/api/scan", ctx => Body(ctx, ApiJson.Web.ScanRequest, r => StartScan(ctx, r)));
        app.MapPost("/api/cancel", ctx => Body(ctx, ApiJson.Web.OkDto, _ => Json(ctx, new OkDto { Ok = jobs.Cancel() }, ApiJson.Web.OkDto)));
        app.MapPost("/api/quarantine", ctx => Body(ctx, ApiJson.Web.QuarantineRequest, r => StartQuarantine(ctx, r)));
        app.MapPost("/api/restore", ctx => Body(ctx, ApiJson.Web.BatchRequest, r => StartBatchJob(ctx, r, JobKind.Restore)));
        app.MapPost("/api/delete", ctx => Body(ctx, ApiJson.Web.BatchRequest, r =>
            r.Confirm == "delete" ? StartBatchJob(ctx, r, JobKind.Delete) : Error(ctx, 400, "confirm must be \"delete\"")));
        app.MapPost("/api/reveal", ctx => Body(ctx, ApiJson.Web.PathRequest, r => Reveal(ctx, r.Path)));
        app.MapPost("/api/scans/delete", ctx => Body(ctx, ApiJson.Web.IdRequest, r =>
        {
            if (jobs.Running) return Error(ctx, 409, Lang.T("Wait for the running job", "Çalışan işin bitmesini bekle"));
            var ok = state.Store.Delete(r.Id);
            state.InvalidateAll();
            hub.Changed("scans");
            return Json(ctx, new OkDto { Ok = ok }, ApiJson.Web.OkDto);
        }));
        // ---- deep checks, system actions, apps ----
        app.MapPost("/api/verify", ctx => Body(ctx, ApiJson.Web.VerifyRequest, async r =>
        {
            var f = state.Findings(r.Scan).Findings.FirstOrDefault(x => x.Id == r.FindingId);
            if (f is null) { await Error(ctx, 404, Lang.T("No such finding", "Böyle bir bulgu yok")); return; }
            var result = await Task.Run(() => Verifier.Verify(f, ctx.RequestAborted), ctx.RequestAborted);
            await Json(ctx, result, ApiJson.Web.VerifyResult);
        }));
        app.MapPost("/api/system", ctx => Body(ctx, ApiJson.Web.IdRequest, r =>
        {
            // Only the fixed actions in SystemActions.All; each asks for elevation itself (UAC).
            if (SystemActions.Get(r.Id) is not { } a) return Error(ctx, 404, "unknown action");
            return Json(ctx, new OkDto { Ok = SystemActions.Run(a), Message = a.CommandLine }, ApiJson.Web.OkDto);
        }));
        app.MapGet("/api/apps", ctx => Guarded(ctx, async () =>
        {
            Snapshot? snap = null;
            try { snap = state.Scan(ctx.Request.Query["scan"]).Snap; } catch (FileNotFoundException) { }
            var apps = Demo ? new List<AppEntry>() : await Task.Run(() => AppsService.List(snap));
            await Json(ctx, apps, ApiJson.Web.ListAppEntry);
        }));
        app.MapPost("/api/apps/uninstall", ctx => Body(ctx, ApiJson.Web.IdRequest, r =>
            Json(ctx, new OkDto { Ok = AppsService.Uninstall(r.Id) }, ApiJson.Web.OkDto)));
        app.MapPost("/api/apps/settings", ctx => Body(ctx, ApiJson.Web.OkDto, _ =>
        {
            AppsService.OpenAppsSettings();
            return Json(ctx, new OkDto(), ApiJson.Web.OkDto);
        }));

        // ---- Claude analysis (optional; Ferahla works fully without it) ----
        app.MapGet("/api/analysis", async ctx => await Json(ctx, analysis.State(await analysis.InfoAsync()), AnalysisJson.Web.AnalysisDto));
        app.MapPost("/api/analysis/refresh", ctx => Body(ctx, ApiJson.Web.OkDto, async _ =>
            await Json(ctx, analysis.State(await analysis.InfoAsync(refresh: true)), AnalysisJson.Web.AnalysisDto)));
        app.MapPost("/api/analysis/start", ctx => Body(ctx, AnalysisJson.Default.AnalysisRequest, r => StartAnalysis(ctx, r)));
        app.MapPost("/api/analysis/stop", ctx => Body(ctx, ApiJson.Web.OkDto, _ => Json(ctx, new OkDto { Ok = analysis.Stop() }, ApiJson.Web.OkDto)));
        app.MapPost("/api/analysis/terminal", ctx => Body(ctx, ApiJson.Web.OkDto, _ => Json(ctx, new OkDto { Ok = analysis.OpenTerminal() }, ApiJson.Web.OkDto)));
        app.MapGet("/api/chat", async ctx =>
        {
            string id = ctx.Request.Query["finding"].ToString();
            if (!NoteStore.IsFindingId(id)) { await Error(ctx, 400, "not a finding id"); return; }
            await Json(ctx, chat.State(id, await analysis.InfoAsync()), AnalysisJson.Web.ChatDto);
        });
        app.MapPost("/api/chat/send", ctx => Body(ctx, AnalysisJson.Default.ChatRequest, r => SendChat(ctx, r)));
        app.MapPost("/api/chat/stop", ctx => Body(ctx, ApiJson.Web.OkDto, _ => Json(ctx, new OkDto { Ok = chat.Stop() }, ApiJson.Web.OkDto)));
        app.MapPost("/api/note", ctx => Body(ctx, ApiJson.Web.NoteRequest, r =>
        {
            if (!NoteStore.IsFindingId(r.FindingId)) return Error(ctx, 400, "not a finding id");
            var v = r.Verdict switch { "remove" => NoteVerdict.Remove, "keep" => NoteVerdict.Keep, "check" => NoteVerdict.Check, _ => NoteVerdict.None };
            new NoteStore(state.Store.Home).Set(r.FindingId, r.Text ?? "", v, "user", null, DateTimeOffset.UtcNow);
            hub.Changed("notes");
            return Json(ctx, new OkDto(), ApiJson.Web.OkDto);
        }));

        app.MapPost("/api/quit", ctx => Body(ctx, ApiJson.Web.OkDto, async _ =>
        {
            await Json(ctx, new OkDto(), ApiJson.Web.OkDto);
            requestStop();
        }));
    }

    // ---- state ----

    internal StateDto BuildState()
    {
        var known = SystemInfo.KnownFolders();
        var s = new StateDto
        {
            Version = Version, Lang = Lang.Current, Scans = state.Store.ListMeta(), Job = jobs.Current,
            DefaultRoot = known.GetValueOrDefault("SystemDrive"),
        };
        if (Demo)
        {
            // Screenshots for the README: drives come from the (invented) demo scans, not from this machine.
            foreach (var g in s.Scans.GroupBy(x => x.Root[..3].ToUpperInvariant()))
            {
                var m = g.First();
                s.Drives.Add(new DriveDto { Root = g.Key, Format = "NTFS", Type = "fixed", TotalBytes = m.TotalBytes, FreeBytes = m.FreeBytes, IsSystem = true, CanQuarantine = true });
            }
            s.DefaultRoot = s.Drives.FirstOrDefault()?.Root;
            return s;
        }
        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
            try
            {
                if (!d.IsReady) continue;
                var root = PathUtil.Normalize(d.RootDirectory.FullName);
                s.Drives.Add(new DriveDto
                {
                    Root = root, Label = d.VolumeLabel, Format = d.DriveFormat, Type = d.DriveType.ToString().ToLowerInvariant(),
                    TotalBytes = d.TotalSize, FreeBytes = d.TotalFreeSpace,
                    IsSystem = string.Equals(root, s.DefaultRoot, StringComparison.OrdinalIgnoreCase),
                    CanQuarantine = d.DriveType == DriveType.Fixed && d.DriveFormat is "NTFS" or "ReFS",
                });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        try
        {
            foreach (var b in PlanRunner.Engine(state.Store).List()) { s.QuarantineItems += b.Items; s.QuarantineBytes += b.Bytes; }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return s;
    }

    // ---- jobs ----

    private Task StartScan(HttpContext ctx, ScanRequest r)
    {
        var known = SystemInfo.KnownFolders();
        string root;
        try { root = ScanService.ResolveRoot(string.IsNullOrWhiteSpace(r.Root) ? null : r.Root, known); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return Error(ctx, 400, ex.Message); }
        if (root.StartsWith(@"\\", StringComparison.Ordinal)) return Error(ctx, 400, Lang.T("Network paths are not scanned", "Ağ yolları taranmaz"));
        if (!Directory.Exists(root)) return Error(ctx, 404, Lang.T($"Not found: {root}", $"Bulunamadı: {root}"));

        return Started(ctx, jobs.TryStart(JobKind.Scan, root, false, ct =>
        {
            var progress = new SyncProgress<ScanProgress>(p => jobs.Update(s => { s.Files = p.Files; s.Dirs = p.Dirs; s.Bytes = p.Bytes; s.Current = p.Current; }));
            var (_, summary) = ScanService.Run(root, state.Store, known, progress: progress, ct: ct);
            jobs.Update(s => { s.Scan = summary; s.Files = summary.Files; s.Bytes = summary.ScannedBytes; s.Current = null; }, force: true);
            return Task.CompletedTask;
        }, () => { state.InvalidateAll(); hub.Changed("scans"); }, out var status), status);
    }

    private Task StartQuarantine(HttpContext ctx, QuarantineRequest r)
    {
        try { PlanValidator.CheckHeader(r.Plan); }
        catch (InvalidDataException ex) { return Error(ctx, 400, ex.Message); }
        Snapshot snap;
        try { snap = state.Scan(r.Plan.ScanId).Snap; }
        catch (FileNotFoundException ex) { return Error(ctx, 404, ex.Message); }

        return Started(ctx, jobs.TryStart(JobKind.Quarantine, null, r.DryRun, _ =>
        {
            var result = PlanRunner.Run(r.Plan, snap, state.Store, r.DryRun, ReportMove);
            jobs.Update(s => { s.Result = result; s.Current = null; s.Done = s.Total; s.DoneBytes = s.TotalBytes; }, force: true);
            return Task.CompletedTask;
        }, () => { if (!r.DryRun) { state.InvalidateFindings(); hub.Changed("quarantine"); } }, out var status), status);
    }

    private void ReportMove(MoveProgress p) => jobs.Update(s =>
    {
        s.Current = p.Path; s.Done = p.Done; s.Total = p.Total; s.DoneBytes = p.DoneBytes; s.TotalBytes = p.TotalBytes;
    });

    private Task StartBatchJob(HttpContext ctx, BatchRequest r, JobKind kind)
    {
        if (!QuarantineEngine.IsBatchId(r.Batch)) return Error(ctx, 400, Lang.T("Not a batch id", "Geçerli bir karantina kaydı değil"));
        var keys = r.Keys is { Count: > 0 } k ? new HashSet<string>(k, StringComparer.Ordinal) : null;
        return Started(ctx, jobs.TryStart(kind, r.Batch, false, _ =>
        {
            var engine = PlanRunner.Engine(state.Store);
            var result = kind == JobKind.Delete ? engine.Delete(r.Batch, keys, ReportMove) : engine.Restore(r.Batch, false, ReportMove, keys);
            jobs.Update(s => { s.Result = result; s.Current = null; s.Done = s.Total; s.DoneBytes = s.TotalBytes; }, force: true);
            return Task.CompletedTask;
        }, () => { state.InvalidateFindings(); hub.Changed("quarantine"); }, out var status), status);
    }

    private async Task SendChat(HttpContext ctx, ChatRequest r)
    {
        if (!NoteStore.IsFindingId(r.FindingId)) { await Error(ctx, 400, "not a finding id"); return; }
        var info = await analysis.InfoAsync();
        if (!info.Installed) { await Error(ctx, 400, Lang.T("Claude Code is not installed", "Claude Code kurulu değil")); return; }
        if (!info.LoggedIn) { await Error(ctx, 400, Lang.T("Claude Code is not logged in", "Claude Code'da oturum açılmamış")); return; }
        Snapshot snap;
        try { snap = state.Scan(r.Scan).Snap; }
        catch (FileNotFoundException ex) { await Error(ctx, 404, ex.Message); return; }
        if (chat.Start(snap.Id, r, () => { state.InvalidateFindings(); hub.Changed("notes"); }) is { } problem) { await Error(ctx, 409, problem); return; }
        ctx.Response.StatusCode = 202;
        await Json(ctx, new OkDto(), ApiJson.Web.OkDto);
    }

    private async Task StartAnalysis(HttpContext ctx, AnalysisRequest r)
    {
        var info = await analysis.InfoAsync(refresh: true);
        if (!info.Installed) { await Error(ctx, 400, Lang.T("Claude Code is not installed", "Claude Code kurulu değil")); return; }
        if (!info.LoggedIn) { await Error(ctx, 400, Lang.T("Claude Code is not logged in", "Claude Code'da oturum açılmamış")); return; }
        if (chat.Running) { await Error(ctx, 409, Lang.T("Wait for Claude to finish answering", "Claude'un yanıtı bitirmesini bekle")); return; }
        Snapshot snap;
        try { snap = state.Scan(r.Scan).Snap; }
        catch (FileNotFoundException ex) { await Error(ctx, 404, ex.Message); return; }
        // How many notes the run should write: the UI's progress bar and the turn budget follow from it.
        var scope = AnalysisScope.Normalize(r.Scope);
        int target;
        if (scope == AnalysisScope.Quarantine)
            target = PlanRunner.Engine(state.Store).List().Sum(b => b.Entries.Count(e => e.FindingId is not null));
        else
        {
            var eligible = state.Findings(snap.Id).Findings.Where(AnalysisScope.Eligible);
            if (scope == AnalysisScope.Unnoted)
            {
                var notes = new NoteStore(state.Store.Home).Load().Notes;
                eligible = eligible.Where(f => !notes.ContainsKey(f.Id));
            }
            target = eligible.Count();
        }
        if (target == 0 && scope != AnalysisScope.All) { await Error(ctx, 400, Lang.T("Nothing to analyze", "Analiz edilecek bir şey yok")); return; }
        var work = analysis.Prepare(snap.Id, r, Math.Max(1, target));
        await Started(ctx, jobs.TryStart(JobKind.Analysis, snap.Id, false, work, () => { state.InvalidateFindings(); hub.Changed("notes"); }, out var status), status);
    }

    private static Task Started(HttpContext ctx, bool started, JobStatus status)
    {
        if (!started) return Error(ctx, 409, Lang.T("Another job is running", "Başka bir iş çalışıyor"));
        ctx.Response.StatusCode = 202;
        return Json(ctx, status, ApiJson.Web.JobStatus);
    }

    // ---- data views ----

    internal TreeDto Tree(string? scanId, string? path)
    {
        var (snap, ix) = state.Scan(scanId);
        var findings = state.Findings(snap.Id);
        var want = string.IsNullOrEmpty(path) ? snap.Root : PathUtil.Normalize(path);
        if (!PathUtil.IsUnder(want, snap.Root)) want = snap.Root;
        var d = ix.FindNearest(want);
        var dir = ix.Dir(d);
        var here = ix.PathOf(d);
        var prev = PreviousIndex(snap);

        var items = findings.Findings.Where(f => f.Action != ActionKind.Info)
            .SelectMany(f => f.Items.Where(i => i.NestedIn is null).Select(i => (Path: PathUtil.Normalize(i.Path), i.Bytes, f.Id))).ToList();
        long Suggested(string p) => items.Where(i => PathUtil.IsUnder(i.Path, p)).Sum(i => i.Bytes);
        long? Growth(int dirIndex)
        {
            if (prev is null) return null;
            var pd = prev.Find(ix.PathOf(dirIndex));
            return ix.Dir(dirIndex).Size - (pd >= 0 ? prev.Dir(pd).Size : 0);
        }

        var t = new TreeDto
        {
            ScanId = snap.Id, Root = snap.Root, Path = here, Name = d == 0 ? here : dir.Name, Parent = d == 0 ? null : ix.PathOf(dir.Parent),
            Exact = string.Equals(here, want, StringComparison.OrdinalIgnoreCase),
            Bytes = dir.Size, Files = dir.FileCount, LastWriteUtc = dir.LastWriteUtc,
            Markers = Names(dir.Markers), Flags = Names(dir.Flags), SuggestedBytes = Suggested(here), GrowthBytes = Growth(d),
            FindingIds = items.Where(i => PathUtil.IsUnder(i.Path, here) || PathUtil.IsUnder(here, i.Path)).Select(i => i.Id).Distinct().Take(200).ToList(),
        };
        var kids = ix.Children(d).OrderByDescending(c => ix.Dir(c).Size).ToList();
        foreach (var c in kids.Take(400))
        {
            var cd = ix.Dir(c);
            var cp = ix.PathOf(c);
            t.Children.Add(new TreeChild
            {
                Name = cd.Name, Path = cp, Bytes = cd.Size, Files = cd.FileCount, LastWriteUtc = cd.LastWriteUtc,
                HasChildren = ix.Children(c).Count > 0, Markers = Names(cd.Markers), SuggestedBytes = Suggested(cp), GrowthBytes = Growth(c),
            });
        }
        t.OtherBytes = dir.Size - kids.Sum(c => ix.Dir(c).Size);
        var fileFinding = FileFindings(findings);
        foreach (var fi in ix.FilesIn(d).Select(i => snap.Files[i]).OrderByDescending(f => f.Size).Take(200))
            t.FilesHere.Add(ToDto(ix, fi, fileFinding));
        return t;
    }

    internal FilesDto Files(string? scanId, string? kind)
    {
        var (snap, ix) = state.Scan(scanId);
        var findings = state.Findings(snap.Id);
        var fileFinding = FileFindings(findings);
        var mask = kind switch
        {
            "installer" => FileFlags.Installer,
            "archive" => FileFlags.Archive,
            "partial" => FileFlags.PartialDownload,
            "big" => FileFlags.Big,
            _ => FileFlags.Big | FileFlags.Installer | FileFlags.Archive | FileFlags.PartialDownload,
        };
        var all = snap.Files.Where(f => (f.Flags & mask) != 0 && (f.Flags & FileFlags.CloudOnly) == 0).OrderByDescending(f => f.Size).ToList();
        return new FilesDto
        {
            ScanId = snap.Id, Total = all.Count, TotalBytes = all.Sum(f => f.Size),
            Items = all.Take(1000).Select(f => ToDto(ix, f, fileFinding)).ToList(),
        };
    }

    private static Dictionary<string, string> FileFindings(Core.Findings.FindingsResult r)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in r.Findings)
            foreach (var i in f.Items.Where(i => !i.IsDir)) map.TryAdd(PathUtil.Normalize(i.Path), f.Id);
        return map;
    }

    private static FileDto ToDto(SnapshotIndex ix, FileEntry f, Dictionary<string, string> findings)
    {
        var p = ix.PathOf(f);
        return new FileDto
        {
            Path = p, Name = f.Name, Bytes = f.Size, Length = f.Length, LastWriteUtc = f.LastWriteUtc,
            Kinds = Names(f.Flags & (FileFlags.Big | FileFlags.Installer | FileFlags.Archive | FileFlags.PartialDownload | FileFlags.RootFile | FileFlags.Sparse | FileFlags.Compressed)),
            FindingId = findings.GetValueOrDefault(p),
        };
    }

    private (string For, SnapshotIndex? Index)? _prev;
    private readonly Lock _prevLock = new();

    private SnapshotIndex? PreviousIndex(Snapshot snap)
    {
        lock (_prevLock)
            if (_prev is { } p && p.For == snap.Id) return p.Index;
        var prev = FindingsService.Previous(state.Store, snap);
        var ix = prev is null ? null : new SnapshotIndex(prev);
        lock (_prevLock) _prev = (snap.Id, ix);
        return ix;
    }

    private static List<string> Names<T>(T flags) where T : struct, Enum =>
        Convert.ToInt64(flags) == 0 ? [] : flags.ToString().Split(", ").Select(s => char.ToLowerInvariant(s[0]) + s[1..]).ToList();

    private async Task Report(HttpContext ctx)
    {
        try
        {
            var (snap, _) = state.Scan(ctx.Request.Query["scan"]);
            var html = HtmlReport.Render(snap, state.Findings(snap.Id), state.Diff(snap.Id), DateTimeOffset.Now);
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"ferahla-report-{snap.Id}.html\"";
            await ctx.Response.WriteAsync(html);
        }
        catch (FileNotFoundException ex) { await Error(ctx, 404, ex.Message); }
    }

    private static Task Reveal(HttpContext ctx, string path)
    {
        // Only an existing, full local path; explorer gets it as one /select argument.
        if (path.Length < 4 || path[1] != ':' || path[2] != '\\' || path.Contains('"') || !(File.Exists(path) || Directory.Exists(path)))
            return Error(ctx, 404, Lang.T("Not found", "Bulunamadı"));
        Explorer.Reveal(path);
        return Json(ctx, new OkDto(), ApiJson.Web.OkDto);
    }

    // ---- plumbing ----

    private static Task Json<T>(HttpContext ctx, T value, JsonTypeInfo<T> info)
    {
        ctx.Response.ContentType = "application/json; charset=utf-8";
        return JsonSerializer.SerializeAsync(ctx.Response.Body, value, info, ctx.RequestAborted);
    }

    internal static Task Error(HttpContext ctx, int status, string message)
    {
        ctx.Response.StatusCode = status;
        return Json(ctx, new ErrorResult { Error = message }, ApiJson.Web.ErrorResult);
    }

    private static async Task Guarded(HttpContext ctx, Func<Task> run)
    {
        try { await run(); }
        catch (FileNotFoundException ex) { await Error(ctx, 404, ex.Message); }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException) { await Error(ctx, 500, ex.Message); }
    }

    private static async Task Body<T>(HttpContext ctx, JsonTypeInfo<T> info, Func<T, Task> run)
    {
        T? body;
        try { body = await JsonSerializer.DeserializeAsync(ctx.Request.Body, info, ctx.RequestAborted); }
        catch (JsonException ex) { await Error(ctx, 400, "bad JSON: " + ex.Message); return; }
        if (body is null) { await Error(ctx, 400, "empty body"); return; }
        await Guarded(ctx, () => run(body));
    }

    /// <summary>Progress&lt;T&gt; posts to the thread pool; the scanner already throttles, so report inline.</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

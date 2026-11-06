using System.CommandLine;
using Ferahla.Core;
using Ferahla.Core.Cli;
using Ferahla.Core.Diff;
using Ferahla.Core.Json;
using Ferahla.Core.Model;
using Ferahla.Core.Storage;
using Ferahla.Core.Text;

namespace Ferahla.Commands;

internal static class DiffCommand
{
    public static Command Create(Option<bool> json)
    {
        var from = new Argument<string?>("from") { Description = Lang.T("Older scan id (default: the one before 'to')", "Eski tarama (varsayılan: 'to'dan öncekisi)"), Arity = ArgumentArity.ZeroOrOne };
        var to = new Argument<string?>("to") { Description = Lang.T("Newer scan id (default: latest)", "Yeni tarama (varsayılan: en son)"), Arity = ArgumentArity.ZeroOrOne };
        var min = new Option<int>("--min-mb") { Description = Lang.T("Hide changes smaller than this", "Bundan küçük değişimleri gizle"), DefaultValueFactory = _ => 500 };
        var cmd = new Command("diff", Lang.T("What grew or shrank between two scans", "İki tarama arasında ne büyüdü, ne küçüldü"));
        cmd.Arguments.Add(from); cmd.Arguments.Add(to); cmd.Options.Add(min);
        cmd.SetAction(pr => Run(pr.GetValue(from), pr.GetValue(to), pr.GetValue(min), pr.GetValue(json)));
        return cmd;
    }

    private static int Run(string? fromId, string? toId, int minMb, bool json)
    {
        var store = SnapshotStore.Default();
        // One argument means "compare this with the latest".
        if (fromId is not null && toId is null && store.Latest() is { } latest && latest != fromId) toId = latest;
        if (!Scans.TryLoad(store, toId, json, out var to, out var code)) return code;
        Snapshot? from;
        if (fromId is null)
        {
            from = Scans.Previous(store, to);
            if (from is null)
                return Output.Error(json, Lang.T("Need two scans of the same drive. Run ferahla scan again later.", "Aynı sürücünün iki taraması gerekiyor. Bir süre sonra yeniden ferahla scan çalıştır."), code: ExitCodes.NotFound);
        }
        else if (!Scans.TryLoad(store, fromId, json, out from, out code)) return code;

        if (!string.Equals(PathUtil.Normalize(from.Root), PathUtil.Normalize(to.Root), StringComparison.OrdinalIgnoreCase))
            return Output.Error(json, Lang.T($"Scans are of different places: {from.Root} vs {to.Root}", $"Taramalar farklı yerlerin: {from.Root} ve {to.Root}"), code: ExitCodes.Usage);
        if (from.ScannedAt > to.ScannedAt) (from, to) = (to, from);

        var result = SnapshotDiff.Compare(from, to, Math.Max(1, minMb) * (1L << 20));
        if (json) { Output.Json(result, FerahlaJson.Indented.DiffResult); return ExitCodes.Ok; }

        var c = Lang.Culture;
        var span = to.ScannedAt - from.ScannedAt;
        Console.WriteLine(Lang.T(
            $"{from.Id} → {to.Id} ({span.Days} days): free space {Fmt.SignedSize(result.FreeBytesDelta, c)}, files {Fmt.SignedSize(result.ScannedBytesDelta, c)}",
            $"{from.Id} → {to.Id} ({span.Days} gün): boş alan {Fmt.SignedSize(result.FreeBytesDelta, c)}, dosyalar {Fmt.SignedSize(result.ScannedBytesDelta, c)}"));
        if (result.Entries.Count == 0)
            Console.WriteLine(Lang.T($"No folder changed by more than {Fmt.Size(result.Threshold, c)}.", $"Hiçbir klasör {Fmt.Size(result.Threshold, c)}'tan fazla değişmedi."));
        foreach (var e in result.Entries.Take(40))
        {
            var tag = e.IsNew ? Lang.T(" (new)", " (yeni)") : e.IsGone ? Lang.T(" (gone)", " (gitti)") : "";
            Console.WriteLine($"{Fmt.SignedSize(e.Delta, c),11}  {e.Path}{tag}");
        }
        return ExitCodes.Ok;
    }
}

internal static class InspectCommand
{
    public static Command Create(Option<bool> json)
    {
        var path = new Argument<string>("path") { Description = Lang.T("Folder or file to look at", "Bakılacak klasör ya da dosya") };
        var scan = new Option<string?>("--scan") { Description = Lang.T("Scan id (default: latest)", "Tarama kimliği (varsayılan: en son)") };
        var cmd = new Command("inspect", Lang.T("Details of a path in the latest scan, and the findings that touch it", "Bir yolun son taramadaki ayrıntıları ve ona dokunan bulgular"));
        cmd.Arguments.Add(path); cmd.Options.Add(scan);
        cmd.SetAction(pr => Run(pr.GetValue(path)!, pr.GetValue(scan), pr.GetValue(json)));
        return cmd;
    }

    private static int Run(string rawPath, string? scanId, bool json)
    {
        var store = SnapshotStore.Default();
        if (!Scans.TryLoad(store, scanId, json, out var snap, out var code)) return code;
        if (rawPath.Length == 2 && rawPath[1] == ':') rawPath += "\\";
        var path = PathUtil.Normalize(Path.GetFullPath(rawPath));
        if (!PathUtil.IsUnder(path, snap.Root))
            return Output.Error(json, Lang.T($"{path} is not inside scan {snap.Id} ({snap.Root})", $"{path}, {snap.Id} taramasının ({snap.Root}) içinde değil"), code: ExitCodes.NotFound);

        var ix = new SnapshotIndex(snap);
        var d = ix.FindNearest(path);
        var dir = ix.Dir(d);
        var prev = Scans.Previous(store, snap);
        var findings = FindingsCommand.Run(snap, prev);

        var r = new InspectResult
        {
            ScanId = snap.Id, Path = path, SnapshotPath = ix.PathOf(d), Exact = ix.Find(path) == d,
            Bytes = dir.Size, Files = dir.FileCount, LastWriteUtc = dir.LastWriteUtc,
            Markers = dir.Markers == DirMarkers.None ? [] : dir.Markers.ToString().Split(", ").ToList(),
            Flags = dir.Flags == DirFlags.None ? [] : dir.Flags.ToString().Split(", ").ToList(),
            PreviousScanId = prev?.Id,
        };
        var kids = ix.Children(d).Select(c => ix.Dir(c)).OrderByDescending(c => c.Size).ToList();
        r.Children = kids.Take(30).Select(c => new InspectChild { Name = c.Name, Bytes = c.Size, LastWriteUtc = c.LastWriteUtc, Kind = "dir" }).ToList();
        r.OtherBytes = dir.Size - kids.Sum(c => c.Size);
        r.NotableFiles = ix.FilesIn(d).Select(i => snap.Files[i]).OrderByDescending(f => f.Size).Take(30)
            .Select(f => new InspectChild { Name = f.Name, Bytes = f.Size, LastWriteUtc = f.LastWriteUtc, Kind = f.Flags.ToString() }).ToList();
        r.Findings = findings.Findings.Where(f => f.Items.Any(i => PathUtil.IsUnder(i.Path, ix.PathOf(d)) || PathUtil.IsUnder(ix.PathOf(d), i.Path))).ToList();
        if (prev is not null)
        {
            var pix = new SnapshotIndex(prev);
            var pd = pix.Find(ix.PathOf(d));
            r.GrowthBytes = dir.Size - (pd >= 0 ? pix.Dir(pd).Size : 0);
        }
        if (json) { Output.Json(r, FerahlaJson.Indented.InspectResult); return ExitCodes.Ok; }

        var c = Lang.Culture;
        Console.WriteLine($"{r.SnapshotPath}{(r.Exact ? "" : Lang.T($"  (smallest scanned folder containing {path})", $"  ({path} yolunu içeren en küçük taranan klasör)"))}");
        Console.WriteLine(Lang.T(
            $"{Fmt.Size(r.Bytes, c)} in {Fmt.Count(r.Files, c)} files · last change {(r.LastWriteUtc?.ToLocalTime().ToString("g", c) ?? "-")}",
            $"{Fmt.Count(r.Files, c)} dosyada {Fmt.Size(r.Bytes, c)} · son değişiklik {(r.LastWriteUtc?.ToLocalTime().ToString("g", c) ?? "-")}"));
        if (r.GrowthBytes is long g && prev is not null)
            Console.WriteLine(Lang.T($"Since {prev.Id}: {Fmt.SignedSize(g, c)}", $"{prev.Id} taramasından beri: {Fmt.SignedSize(g, c)}"));
        if (r.Markers.Count > 0) Console.WriteLine(Lang.T("Project markers: ", "Proje işaretleri: ") + string.Join(", ", r.Markers));
        if (r.Flags.Count > 0) Console.WriteLine(Lang.T("Flags: ", "Bayraklar: ") + string.Join(", ", r.Flags));
        if (r.Children.Count > 0)
        {
            Console.WriteLine();
            foreach (var k in r.Children.Take(20)) Console.WriteLine($"{Fmt.Size(k.Bytes, c),10}  {k.Name}\\");
            if (r.OtherBytes > 0) Console.WriteLine($"{Fmt.Size(r.OtherBytes, c),10}  {Lang.T("(files here and small folders)", "(buradaki dosyalar ve küçük klasörler)")}");
        }
        if (r.NotableFiles.Count > 0)
        {
            Console.WriteLine();
            foreach (var f in r.NotableFiles.Take(10)) Console.WriteLine($"{Fmt.Size(f.Bytes, c),10}  {f.Name}");
        }
        if (r.Findings.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine(Lang.T("Findings:", "Bulgular:"));
            foreach (var f in r.Findings) Console.WriteLine($"{Fmt.Size(f.Bytes, c),10}  [{FindingsCommand.RiskName(f.Risk)}] {f.Title}  [{f.Id}]");
        }
        return ExitCodes.Ok;
    }
}

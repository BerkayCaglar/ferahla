using System.CommandLine;
using Ferahla.Core;
using Ferahla.Core.Cli;
using Ferahla.Core.Json;
using Ferahla.Core.Model;
using Ferahla.Core.Storage;
using Ferahla.Core.Text;
using Ferahla.Windows;
using Ferahla.Windows.Scanning;

namespace Ferahla.Commands;

internal static class ScanCommand
{
    public static Command Create(Option<bool> json)
    {
        var drive = new Argument<string?>("drive")
        {
            Description = Lang.T("Drive or folder to scan (default: system drive)", "Taranacak sürücü ya da klasör (varsayılan: sistem sürücüsü)"),
            Arity = ArgumentArity.ZeroOrOne,
        };
        var minDir = new Option<int>("--min-dir-mb")
        {
            Description = Lang.T("Keep folders at least this big in the snapshot", "Anlık görüntüde bu boyuttan büyük klasörleri tut"),
            DefaultValueFactory = _ => 1,
        };
        var cmd = new Command("scan", Lang.T("Scan a drive and save the snapshot", "Bir sürücüyü tara ve anlık görüntüyü kaydet"));
        cmd.Arguments.Add(drive);
        cmd.Options.Add(minDir);
        cmd.SetAction(pr => Run(pr.GetValue(drive), pr.GetValue(minDir), pr.GetValue(json)));
        return cmd;
    }

    private static int Run(string? target, int minDirMb, bool json)
    {
        var known = SystemInfo.KnownFolders();
        var root = ScanService.ResolveRoot(target, known);
        if (!Directory.Exists(root))
            return Output.Error(json, Lang.T($"Not found: {root}", $"Bulunamadı: {root}"), code: ExitCodes.NotFound);

        var c = Lang.Culture;
        var progress = new Progress<ScanProgress>(p => Output.Status(Lang.T(
            $"Scanning… {Fmt.Count(p.Files, c)} files, {Fmt.Size(p.Bytes, c)}  {p.Current}",
            $"Taranıyor… {Fmt.Count(p.Files, c)} dosya, {Fmt.Size(p.Bytes, c)}  {p.Current}")));
        Snapshot snap;
        ScanSummary summary;
        try
        {
            (snap, summary) = ScanService.Run(root, SnapshotStore.Default(), known, Math.Max(0, minDirMb) * (1L << 20), progress);
        }
        finally
        {
            Output.EndStatus();
        }
        if (json)
        {
            Output.Json(summary, FerahlaJson.Indented.ScanSummary);
            return ExitCodes.Ok;
        }

        Console.WriteLine(Lang.T(
            $"Scanned {snap.Root} in {snap.ElapsedSeconds.ToString(c)} s: {Fmt.Count(summary.Files, c)} files, {Fmt.Count(summary.Dirs, c)} folders.",
            $"{snap.Root} {snap.ElapsedSeconds.ToString(c)} sn'de tarandı: {Fmt.Count(summary.Files, c)} dosya, {Fmt.Count(summary.Dirs, c)} klasör."));
        Console.WriteLine(Lang.T(
            $"Drive: {Fmt.Size(summary.TotalBytes, c)} total, {Fmt.Size(summary.FreeBytes, c)} free. Found {Fmt.Size(summary.ScannedBytes, c)} in files.",
            $"Sürücü: {Fmt.Size(summary.TotalBytes, c)} toplam, {Fmt.Size(summary.FreeBytes, c)} boş. Dosyalarda {Fmt.Size(summary.ScannedBytes, c)} bulundu."));
        if (root.Length == 3 && Math.Abs(summary.UnaccountedBytes) > (1L << 30))
            Console.WriteLine(summary.UnaccountedBytes > 0
                ? Lang.T($"Not visible to the scan: {Fmt.Size(summary.UnaccountedBytes, c)} (system files, other users, restore points).",
                         $"Taramanın göremediği: {Fmt.Size(summary.UnaccountedBytes, c)} (sistem dosyaları, diğer kullanıcılar, geri yükleme noktaları).")
                : Lang.T($"Files add up to {Fmt.Size(-summary.UnaccountedBytes, c)} more than used space: hardlinks (e.g. WinSxS) are counted more than once.",
                         $"Dosyalar kullanılan alandan {Fmt.Size(-summary.UnaccountedBytes, c)} fazla tutuyor: hardlink'ler (ör. WinSxS) birden çok kez sayılıyor."));
        if (summary.AccessDeniedDirs > 0)
            Console.WriteLine(Lang.T($"{Fmt.Count(summary.AccessDeniedDirs, c)} folders could not be read (access denied).",
                                     $"{Fmt.Count(summary.AccessDeniedDirs, c)} klasör okunamadı (erişim reddedildi)."));
        if (summary.CloudOnlyBytes > 0)
            Console.WriteLine(Lang.T($"{Fmt.Size(summary.CloudOnlyBytes, c)} is in the cloud only (not on this disk).",
                                     $"{Fmt.Size(summary.CloudOnlyBytes, c)} sadece bulutta (bu diskte değil)."));
        Console.WriteLine(Lang.T($"Saved: {snap.Id}", $"Kaydedildi: {snap.Id}"));
        return ExitCodes.Ok;
    }
}

internal static class ScansCommand
{
    public static Command Create(Option<bool> json)
    {
        var cmd = new Command("scans", Lang.T("List saved scans", "Kayıtlı taramaları listele"));
        cmd.SetAction(pr => Run(pr.GetValue(json)));
        return cmd;
    }

    private static int Run(bool json)
    {
        var store = SnapshotStore.Default();
        var items = store.ListMeta(); // a broken file never hides the others
        if (json) { Output.Json(items, FerahlaJson.Indented.ListScanListItem); return ExitCodes.Ok; }
        var c = Lang.Culture;
        if (items.Count == 0) Console.WriteLine(Lang.T("No scans yet. Run: ferahla scan", "Henüz tarama yok. Çalıştır: ferahla scan"));
        foreach (var i in items)
            Console.WriteLine($"{i.Id}  {i.ScannedAt.LocalDateTime.ToString("g", c)}  {Lang.T("free", "boş")} {Fmt.Size(i.FreeBytes, c)}");
        return ExitCodes.Ok;
    }
}

using System.CommandLine;
using System.Diagnostics;
using Ferahla.Core.Cli;
using Ferahla.Core.Diff;
using Ferahla.Core.Report;
using Ferahla.Core.Storage;
using Ferahla.Core.Text;

namespace Ferahla.Commands;

internal static class ReportCommand
{
    public static Command Create(Option<bool> json)
    {
        var scan = new Option<string?>("--scan") { Description = Lang.T("Scan id (default: latest)", "Tarama kimliği (varsayılan: en son)") };
        var output = new Option<string?>("--output", "-o") { Description = Lang.T("Where to write the file", "Dosyanın yazılacağı yer") };
        var open = new Option<bool>("--open") { Description = Lang.T("Open it in the browser", "Tarayıcıda aç") };
        var html = new Option<bool>("--html") { Description = Lang.T("Single-file HTML report (the only format for now)", "Tek dosyalık HTML rapor (şimdilik tek biçim)") };
        var cmd = new Command("report", Lang.T(
            "Write a single-file, read-only HTML report. It contains your file paths: look before sharing it.",
            "Tek dosyalık, salt okunur HTML rapor üret. Dosya yollarını içerir: paylaşmadan önce bak."));
        cmd.Options.Add(scan); cmd.Options.Add(output); cmd.Options.Add(open); cmd.Options.Add(html);
        cmd.SetAction(pr => Run(pr.GetValue(scan), pr.GetValue(output), pr.GetValue(open), pr.GetValue(json)));
        return cmd;
    }

    private static int Run(string? scanId, string? output, bool open, bool json)
    {
        var store = SnapshotStore.Default();
        if (!Scans.TryLoad(store, scanId, json, out var snap, out var code)) return code;
        var prev = Scans.Previous(store, snap);
        var findings = FindingsCommand.Run(snap, prev);
        var diff = prev is null ? null : SnapshotDiff.Compare(prev, snap, threshold: 200L << 20);
        var html = HtmlReport.Render(snap, findings, diff, DateTimeOffset.Now);

        var path = Path.GetFullPath(output ?? Path.Combine(store.Home, "reports", snap.Id + ".html"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, html, new System.Text.UTF8Encoding(false));

        if (open) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        if (json) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(path, Core.Json.FerahlaJson.Indented.String));
        else Console.WriteLine(Lang.T($"Report: {path}", $"Rapor: {path}"));
        return ExitCodes.Ok;
    }
}

using System.CommandLine;
using Ferahla.Core.Cli;
using Ferahla.Core.Findings;
using Ferahla.Core.Json;
using Ferahla.Core.Rules;
using Ferahla.Core.Storage;
using Ferahla.Core.Text;

namespace Ferahla.Commands;

internal static class FindingsCommand
{
    public static Command Create(Option<bool> json)
    {
        var scan = new Option<string?>("--scan") { Description = Lang.T("Scan id (default: latest)", "Tarama kimliği (varsayılan: en son)") };
        var risk = new Option<string?>("--risk") { Description = "safe | regenerable | review | personal" };
        var all = new Option<bool>("--all") { Description = Lang.T("Show every finding (default: top 15 per risk)", "Tüm bulguları göster (varsayılan: her riskte ilk 15)") };
        var pending = new Option<bool>("--include-pending") { Description = Lang.T("Include rules still in testing", "Henüz test aşamasındaki kuralları da kullan") };
        var cmd = new Command("findings", Lang.T("List classified findings for a scan", "Bir taramanın sınıflandırılmış bulgularını listele"));
        cmd.Options.Add(scan); cmd.Options.Add(risk); cmd.Options.Add(all); cmd.Options.Add(pending);
        cmd.SetAction(pr => Run(pr.GetValue(scan), pr.GetValue(risk), pr.GetValue(all), pr.GetValue(pending), pr.GetValue(json)));
        return cmd;
    }

    internal static FindingsResult? Compute(string? scanId, bool includePending, bool json, out int exitCode)
    {
        var store = SnapshotStore.Default();
        if (!Scans.TryLoad(store, scanId, json, out var snap, out exitCode)) return null;
        var r = Run(snap, Scans.Previous(store, snap), includePending);
        new Core.Notes.NoteStore(store.Home).Apply(r);
        return r;
    }

    internal static FindingsResult Run(Core.Model.Snapshot snap, Core.Model.Snapshot? prev, bool includePending = false) =>
        Windows.FindingsService.Compute(snap, prev, includePending);

    private static int Run(string? scanId, string? riskFilter, bool all, bool includePending, bool json)
    {
        var result = Compute(scanId, includePending, json, out var code);
        if (result is null) return code;
        if (riskFilter is not null)
        {
            if (!Enum.TryParse<Risk>(riskFilter, ignoreCase: true, out var r))
                return Output.Error(json, Lang.T($"Unknown risk: {riskFilter}", $"Bilinmeyen risk: {riskFilter}"), code: ExitCodes.Usage);
            result.Findings.RemoveAll(f => f.Risk != r);
        }
        if (json) { Output.Json(result, FerahlaJson.Indented.FindingsResult); return ExitCodes.Ok; }

        var c = Lang.Culture;
        Console.WriteLine(Lang.T(
            $"Scan {result.ScanId} · {Fmt.Size(result.FreeBytes, c)} free of {Fmt.Size(result.TotalBytes, c)}",
            $"Tarama {result.ScanId} · {Fmt.Size(result.TotalBytes, c)} diskin {Fmt.Size(result.FreeBytes, c)}'ı boş"));
        if (result.PreviousScanId is not null)
            Console.WriteLine(Lang.T($"Compared with {result.PreviousScanId}", $"Karşılaştırılan: {result.PreviousScanId}"));
        foreach (var g in result.Findings.GroupBy(f => f.Risk))
        {
            Console.WriteLine();
            // Info findings (a VM disk, unseen space) explain space but can't be acted on, so they aren't in the total.
            Console.WriteLine($"== {RiskName(g.Key)} · {Fmt.Size(g.Where(f => f.Action != ActionKind.Info).Sum(f => f.Bytes), c)} ==");
            foreach (var f in all ? g : g.Take(15))
            {
                var growth = f.IsNew ? Lang.T("  new", "  yeni") : f.GrowthBytes is long gb && Math.Abs(gb) >= (100L << 20) ? $"  {Fmt.SignedSize(gb, c)}" : "";
                Console.WriteLine($"{Fmt.Size(f.Bytes, c),10}  {f.Title}{growth}  [{f.Id}]");
                if (f.Items.Count == 1) Console.WriteLine($"{"",10}  {f.Items[0].Path}");
                else if (f.Items.Count > 1) Console.WriteLine($"{"",10}  {f.Items[0].Path}  (+{f.Items.Count - 1})");
                if (f.Command is { Length: > 0 } cmdText && f.Action is ActionKind.Command or ActionKind.Info)
                    Console.WriteLine($"{"",10}  > {cmdText}");
            }
            if (!all && g.Count() > 15) Console.WriteLine(Lang.T($"{"",10}  … {g.Count() - 15} more (use --all)", $"{"",10}  … {g.Count() - 15} tane daha (--all ile)"));
        }
        if (result.NotSuggested.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine(Lang.T("Not suggested:", "Önerilmeyenler:"));
            foreach (var n in result.NotSuggested.OrderByDescending(n => n.Bytes).Take(all ? int.MaxValue : 5))
                Console.WriteLine($"{Fmt.Size(n.Bytes, c),10}  {n.Path} — {n.Reason}");
        }
        foreach (var e in result.RuleErrors) Console.Error.WriteLine("rule error: " + e);
        return ExitCodes.Ok;
    }

    public static string RiskName(Risk r) => r switch
    {
        Risk.Safe => Lang.T("Safe", "Risksiz"),
        Risk.Regenerable => Lang.T("Regenerable", "Yeniden üretilir"),
        Risk.Review => Lang.T("Review", "Bak, karar ver"),
        Risk.Personal => Lang.T("Personal (info)", "Kişisel (bilgi)"),
        _ => Lang.T("Protected", "Korumalı"),
    };
}



using System.CommandLine;
using Ferahla.Core.Cli;
using Ferahla.Core.Json;
using Ferahla.Core.Text;
using Ferahla.Windows;

namespace Ferahla.Commands;

internal static class VerifyCommand
{
    public static Command Create(Option<bool> json)
    {
        var id = new Argument<string>("finding") { Description = Lang.T("Finding id (f-…)", "Bulgu kimliği (f-…)") };
        var scan = new Option<string?>("--scan") { Description = Lang.T("Scan id (default: latest)", "Tarama kimliği (varsayılan: en son)") };
        var cmd = new Command("verify", Lang.T(
            "Check a finding against the disk now: duplicates byte by byte, zip and folder by CRC-32",
            "Bir bulguyu şu anki diske karşı kontrol et: kopyalar bayt bayt, zip ve klasör CRC-32 ile"));
        cmd.Arguments.Add(id); cmd.Options.Add(scan);
        cmd.SetAction(pr =>
        {
            var j = pr.GetValue(json);
            var result = FindingsCommand.Compute(pr.GetValue(scan), false, j, out var code);
            if (result is null) return code;
            var fid = pr.GetValue(id)!.Trim();
            var f = result.Findings.FirstOrDefault(x => x.Id == fid);
            if (f is null) return Output.Error(j, Lang.T($"No finding {fid} in scan {result.ScanId}", $"{result.ScanId} taramasında {fid} bulgusu yok"), code: ExitCodes.NotFound);
            if (!j) Output.Status(Lang.T("Checking…", "Kontrol ediliyor…"));
            var r = Verifier.Verify(f);
            if (!j) Output.EndStatus();
            if (j) { Output.Json(r, FerahlaJson.Indented.VerifyResult); return r.Ok ? ExitCodes.Ok : ExitCodes.Partial; }
            Console.WriteLine($"{f.Title}  [{f.Id}]");
            foreach (var c in r.Checks) Console.WriteLine((r.Ok ? "  ✓ " : "  · ") + c);
            Console.WriteLine(r.Ok ? Lang.T("Verified.", "Doğrulandı.") : Lang.T("Not fully verified: look before acting.", "Tam doğrulanmadı: harekete geçmeden önce bak."));
            return r.Ok ? ExitCodes.Ok : ExitCodes.Partial;
        });
        return cmd;
    }
}

using System.CommandLine;
using Ferahla.Core.Cli;
using Ferahla.Core.Notes;
using Ferahla.Core.Storage;
using Ferahla.Core.Text;
using Ferahla.Mcp;

namespace Ferahla.Commands;

internal static class McpCommand
{
    public static Command Create()
    {
        var scan = new Option<string?>("--scan") { Description = Lang.T("Scan id (default: latest)", "Tarama kimliği (varsayılan: en son)") };
        var home = new Option<string?>("--home") { Description = Lang.T("Ferahla's data folder", "Ferahla'nın veri klasörü") };
        var agent = new Option<string?>("--agent") { Description = Lang.T("Who writes the notes: claude (default) or codex", "Notları kim yazıyor: claude (varsayılan) ya da codex") };
        var cmd = new Command("mcp", Lang.T(
            "MCP server on stdin/stdout for the AI analysis, Claude Code or Codex (read-only tools + notes)",
            "Yapay zekâ analizi (Claude Code ya da Codex) için stdin/stdout üzerinde MCP sunucusu (salt okuma araçları + notlar)"));
        cmd.Options.Add(scan); cmd.Options.Add(home); cmd.Options.Add(agent);
        cmd.SetAction((pr, ct) => FerahlaMcp.RunAsync(pr.GetValue(home) ?? SnapshotStore.DefaultHome(), pr.GetValue(scan), ct, pr.GetValue(agent)));
        return cmd;
    }
}

internal static class NoteCommand
{
    public static Command Create(Option<bool> json)
    {
        var id = new Argument<string>("finding") { Description = Lang.T("Finding id (f-…)", "Bulgu kimliği (f-…)") };
        var text = new Argument<string>("text") { Description = Lang.T("The note; empty removes it", "Not; boş bırakılırsa silinir") };
        var verdict = new Option<string?>("--verdict") { Description = "remove | check | keep" };
        var cmd = new Command("note", Lang.T("Add a note to a finding", "Bir bulguya not ekle"));
        cmd.Arguments.Add(id); cmd.Arguments.Add(text); cmd.Options.Add(verdict);
        cmd.SetAction(pr =>
        {
            var j = pr.GetValue(json);
            var fid = pr.GetValue(id)!.Trim();
            if (!NoteStore.IsFindingId(fid)) return Output.Error(j, Lang.T($"Not a finding id: {fid}", $"Geçerli bir bulgu kimliği değil: {fid}"), code: ExitCodes.Usage);
            var v = pr.GetValue(verdict)?.ToLowerInvariant() switch { "remove" => NoteVerdict.Remove, "keep" => NoteVerdict.Keep, "check" => NoteVerdict.Check, _ => NoteVerdict.None };
            new NoteStore(SnapshotStore.DefaultHome()).Set(fid, pr.GetValue(text) ?? "", v, "user", null, DateTimeOffset.UtcNow);
            if (!j) Console.WriteLine(Lang.T("Saved.", "Kaydedildi."));
            else Console.WriteLine("{\"ok\":true}");
            return ExitCodes.Ok;
        });
        return cmd;
    }
}

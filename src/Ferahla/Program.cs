using System.CommandLine;
using System.Text;
using Ferahla.Commands;
using Ferahla.Core.Text;

Console.OutputEncoding = new UTF8Encoding(false);

var json = new Option<bool>("--json") { Description = Lang.T("Machine-readable JSON output (versioned schema)", "Makine okunur JSON çıktı (sürümlü şema)"), Recursive = true };

var root = new RootCommand(Lang.T(
    "Ferahla: find what fills your disk and clean it up safely.",
    "Ferahla: diskini neyin doldurduğunu bul, güvenle temizle."));
root.Options.Add(json);
root.Subcommands.Add(ScanCommand.Create(json));
root.Subcommands.Add(ScansCommand.Create(json));
root.Subcommands.Add(FindingsCommand.Create(json));
root.Subcommands.Add(DiffCommand.Create(json));
root.Subcommands.Add(InspectCommand.Create(json));
root.Subcommands.Add(ReportCommand.Create(json));
root.Subcommands.Add(QuarantineCommand.Create(json));
root.Subcommands.Add(RestoreCommand.Create(json));
root.Subcommands.Add(UiCommand.Create());
root.Subcommands.Add(NoteCommand.Create(json));
root.Subcommands.Add(VerifyCommand.Create(json));
root.Subcommands.Add(McpCommand.Create());
// No command: the UI.
root.Options.Add(UiCommand.NoBrowser);
root.Options.Add(UiCommand.Port);
root.SetAction((pr, ct) => UiCommand.Run(pr.GetValue(UiCommand.NoBrowser), pr.GetValue(UiCommand.Port), ct));

return await root.Parse(args).InvokeAsync();

using System.CommandLine;
using System.Text.Json;
using Ferahla.Core.Cli;
using Ferahla.Core.Json;
using Ferahla.Core.Quarantine;
using Ferahla.Core.Storage;
using Ferahla.Core.Text;
using Ferahla.Windows.Quarantine;

namespace Ferahla.Commands;

// There is deliberately no command that deletes quarantined items for good: that is a button in the UI only.

internal static class QuarantineCommand
{
    public static Command Create(Option<bool> json)
    {
        var plan = new Option<string?>("--plan") { Description = Lang.T("Plan file from the report (\"Download plan file\")", "Rapordan indirilen plan dosyası (\"Plan dosyasını indir\")") };
        var dryRun = new Option<bool>("--dry-run") { Description = Lang.T("Check everything, move nothing", "Her şeyi kontrol et, hiçbir şeyi taşıma") };
        var list = new Option<bool>("--list") { Description = Lang.T("List what is in quarantine", "Karantinada olanları listele") };
        var cmd = new Command("quarantine", Lang.T(
            "Move the items of a plan into quarantine (undo with: ferahla restore <batch>)",
            "Plandaki öğeleri karantinaya taşı (geri almak için: ferahla restore <kayıt>)"));
        cmd.Options.Add(plan); cmd.Options.Add(dryRun); cmd.Options.Add(list);
        cmd.SetAction(pr => pr.GetValue(list) ? List(pr.GetValue(json)) : Run(pr.GetValue(plan), pr.GetValue(dryRun), pr.GetValue(json)));
        return cmd;
    }

    internal static QuarantineEngine Engine(SnapshotStore store) => PlanRunner.Engine(store);

    private static int Run(string? planFile, bool dryRun, bool json)
    {
        if (planFile is null)
            return Output.Error(json, Lang.T("Give a plan file: ferahla quarantine --plan <file> [--dry-run]", "Plan dosyası ver: ferahla quarantine --plan <dosya> [--dry-run]"), code: ExitCodes.Usage);

        CleanupPlan plan;
        try
        {
            plan = JsonSerializer.Deserialize(File.ReadAllBytes(planFile), FerahlaJson.Default.CleanupPlan)
                   ?? throw new InvalidDataException(Lang.T("Empty plan file", "Boş plan dosyası"));
            PlanValidator.CheckHeader(plan);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Output.Error(json, Lang.T($"Plan file not found: {planFile}", $"Plan dosyası bulunamadı: {planFile}"), code: ExitCodes.NotFound);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return Output.Error(json, Lang.T("The plan file could not be read", "Plan dosyası okunamadı"), ex.Message, ExitCodes.Usage);
        }

        // Findings are computed again from the plan's scan; only what Ferahla suggests now can move.
        var store = SnapshotStore.Default();
        if (!Scans.TryLoad(store, plan.ScanId, json, out var snap, out var code)) return code;
        if (!json) Output.Status(Lang.T("Checking the plan against the current findings…", "Plan güncel bulgularla karşılaştırılıyor…"));
        var result = PlanRunner.Run(plan, snap, store, dryRun, json ? null : p => Output.Status((dryRun ? "… " : "→ ") + p.Path));
        if (!json) Output.EndStatus();
        return Report(result, json, restore: false);
    }

    internal static int Report(QuarantineResult r, bool json, bool restore)
    {
        var code = r.Skipped + r.Failed == 0 ? ExitCodes.Ok : r.Done > 0 ? ExitCodes.Partial : ExitCodes.Error;
        if (r.Items.Count == 0) code = ExitCodes.Ok;
        if (json) { Output.Json(r, FerahlaJson.Indented.QuarantineResult); return code; }

        var c = Lang.Culture;
        foreach (var o in r.Items.OrderBy(o => o.Status))
        {
            var tag = o.Status switch
            {
                OutcomeStatus.Moved => Lang.T("moved", "taşındı"),
                OutcomeStatus.WouldMove => Lang.T("would move", "taşınacak"),
                OutcomeStatus.Restored => Lang.T("restored", "geri alındı"),
                OutcomeStatus.WouldRestore => Lang.T("would restore", "geri alınacak"),
                OutcomeStatus.Skipped => Lang.T("skipped", "atlandı"),
                _ => Lang.T("FAILED", "BAŞARISIZ"),
            };
            Console.WriteLine($"{Fmt.Size(o.Bytes, c),10}  {tag,-14} {o.Path}");
            if (o.Reason is not null) Console.WriteLine($"{"",10}  {"",-14} {o.Reason}");
        }
        Console.WriteLine();
        var done = Fmt.Size(r.DoneBytes, c);
        Console.WriteLine((restore, r.DryRun) switch
        {
            (false, true) => Lang.T($"Dry run: {r.Done} item(s), {done} would move. Nothing was moved.", $"Deneme: {r.Done} öğe, {done} taşınacak. Hiçbir şey taşınmadı."),
            (false, false) => Lang.T($"Moved {r.Done} item(s), {done}.", $"{r.Done} öğe taşındı, {done}."),
            (true, true) => Lang.T($"Dry run: {r.Done} item(s), {done} would be restored.", $"Deneme: {r.Done} öğe, {done} geri alınacak."),
            _ => Lang.T($"Restored {r.Done} item(s), {done}.", $"{r.Done} öğe geri alındı, {done}."),
        });
        if (r.Skipped + r.Failed > 0)
            Console.WriteLine(Lang.T($"{r.Skipped} skipped, {r.Failed} failed (see above).", $"{r.Skipped} atlandı, {r.Failed} başarısız (yukarıda)."));
        if (!restore && !r.DryRun && r.Batch is not null)
            Console.WriteLine(Lang.T($"Undo: ferahla restore {r.Batch}", $"Geri almak için: ferahla restore {r.Batch}"));
        return code;
    }

    private static int List(bool json)
    {
        var batches = Engine(SnapshotStore.Default()).List();
        if (json) { Output.Json(batches, FerahlaJson.Indented.ListBatchInfo); return ExitCodes.Ok; }
        if (batches.Count == 0) { Console.WriteLine(Lang.T("Quarantine is empty.", "Karantina boş.")); return ExitCodes.Ok; }
        var c = Lang.Culture;
        foreach (var b in batches)
            Console.WriteLine($"{b.Batch}  {b.CreatedAt.ToLocalTime().ToString("g", c)}  {Fmt.Size(b.Bytes, c),10}  " +
                              Lang.T($"{b.Items} item(s)", $"{b.Items} öğe") + $"  {string.Join(" ", b.Volumes)}");
        return ExitCodes.Ok;
    }
}

internal static class RestoreCommand
{
    public static Command Create(Option<bool> json)
    {
        var batch = new Argument<string>("batch") { Description = Lang.T("Batch id (see: ferahla quarantine --list)", "Karantina kaydı (bkz. ferahla quarantine --list)") };
        var dryRun = new Option<bool>("--dry-run") { Description = Lang.T("Check, restore nothing", "Kontrol et, hiçbir şeyi geri alma") };
        var cmd = new Command("restore", Lang.T("Put a quarantine batch back where it came from", "Bir karantina kaydını eski yerine geri koy"));
        cmd.Arguments.Add(batch); cmd.Options.Add(dryRun);
        cmd.SetAction(pr => Run(pr.GetValue(batch)!, pr.GetValue(dryRun), pr.GetValue(json)));
        return cmd;
    }

    private static int Run(string batch, bool dryRun, bool json)
    {
        try
        {
            var r = QuarantineCommand.Engine(SnapshotStore.Default()).Restore(batch, dryRun, json ? null : p => Output.Status("← " + p.Path));
            if (!json) Output.EndStatus();
            return QuarantineCommand.Report(r, json, restore: true);
        }
        catch (ArgumentException ex) { return Output.Error(json, ex.Message, code: ExitCodes.Usage); }
        catch (FileNotFoundException ex) { return Output.Error(json, ex.Message, code: ExitCodes.NotFound); }
    }
}

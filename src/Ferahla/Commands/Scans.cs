using Ferahla.Core.Cli;
using Ferahla.Core.Model;
using Ferahla.Core.Storage;
using Ferahla.Core.Text;

namespace Ferahla.Commands;

/// <summary>Loading saved scans with friendly errors; shared by findings, diff, inspect and report.</summary>
internal static class Scans
{
    public static bool TryLoad(SnapshotStore store, string? id, bool json, out Snapshot snap, out int exitCode)
    {
        snap = null!;
        id ??= store.Latest();
        if (id is null)
        {
            exitCode = Output.Error(json, Lang.T("No scans yet. Run: ferahla scan", "Henüz tarama yok. Çalıştır: ferahla scan"), code: ExitCodes.NotFound);
            return false;
        }
        try
        {
            snap = store.Load(id);
            exitCode = ExitCodes.Ok;
            return true;
        }
        catch (FileNotFoundException)
        {
            exitCode = Output.Error(json, Lang.T($"Scan not found: {id}", $"Tarama bulunamadı: {id}"), code: ExitCodes.NotFound);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or IOException)
        {
            exitCode = Output.Error(json, Lang.T($"Scan {id} could not be read. Run ferahla scan again.", $"{id} taraması okunamadı. ferahla scan ile yeniden tara."), ex.Message);
        }
        return false;
    }

    /// <summary>The readable scan of the same root right before <paramref name="snap"/>, if any.</summary>
    public static Snapshot? Previous(SnapshotStore store, Snapshot snap)
    {
        foreach (var id in store.List(snap.Root).SkipWhile(x => x != snap.Id).Skip(1))
        {
            try { return store.Load(id); }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or IOException) { }
        }
        return null;
    }
}

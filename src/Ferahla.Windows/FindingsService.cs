using Ferahla.Core.Findings;
using Ferahla.Core.Model;
using Ferahla.Core.Rules;
using Ferahla.Core.Storage;
using Ferahla.Windows.Detectors;

namespace Ferahla.Windows;

/// <summary>Built-in rules + coded detectors, the one way findings are computed (CLI, UI server, quarantine checks).</summary>
public static class FindingsService
{
    public static FindingsResult Compute(Snapshot snap, Snapshot? previous, bool includePending = false) =>
        new FindingsEngine(RuleLoader.LoadBuiltIn(includePending), BuiltInDetectors.All()).Run(snap, previous);

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

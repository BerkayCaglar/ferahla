using System.Runtime.Versioning;
using Ferahla.Core.Model;
using Ferahla.Core.Quarantine;
using Ferahla.Core.Storage;

namespace Ferahla.Windows.Quarantine;

/// <summary>Plan → checks → moves. Shared by <c>ferahla quarantine</c> and the UI server.</summary>
[SupportedOSPlatform("windows")]
public static class PlanRunner
{
    /// <summary>The guard knows this machine's folders now, plus the scan's (in case they moved since).</summary>
    public static MoveGuard Guard(SnapshotStore store, Snapshot? snap = null)
    {
        var folders = new Dictionary<string, string>(SystemInfo.KnownFolders(), StringComparer.OrdinalIgnoreCase);
        var i = 0;
        foreach (var (k, v) in snap?.KnownFolders ?? []) folders[$"scan{i++}:{k}"] = v;
        return new MoveGuard(folders, store.Home);
    }

    public static QuarantineEngine Engine(SnapshotStore store, MoveGuard? guard = null) => new(store.Home, guard ?? Guard(store));

    /// <summary>
    /// Findings are computed again from the plan's scan; only what Ferahla suggests now can move.
    /// </summary>
    /// <exception cref="InvalidDataException">Not a valid plan.</exception>
    public static QuarantineResult Run(CleanupPlan plan, Snapshot snap, SnapshotStore store, bool dryRun, Action<MoveProgress>? progress = null)
    {
        PlanValidator.CheckHeader(plan);
        if (plan.ScanId != snap.Id) throw new InvalidDataException("plan and scan don't match");
        var findings = FindingsService.Compute(snap, FindingsService.Previous(store, snap));
        var guard = Guard(store, snap);
        var rejected = new List<ItemOutcome>();
        var moves = PlanValidator.Validate(plan, findings, guard, rejected);
        var result = Engine(store, guard).Quarantine(moves, snap.Id, dryRun, progress);
        foreach (var r in rejected) result.Add(r);
        return result;
    }
}

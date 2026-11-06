using Ferahla.Core.Findings;
using Ferahla.Core.Rules;
using Ferahla.Core.Text;

namespace Ferahla.Core.Quarantine;

/// <summary>An item that passed every check and may be moved.</summary>
public sealed record ApprovedMove(string Path, bool IsDir, long PlannedBytes, string FindingId, string RuleId, int? KeepRecentHours);

/// <summary>
/// Turns a plan into moves. A plan is only a selection: an item is accepted only if Ferahla itself suggests it
/// right now (same finding id, same item, quarantine action, computed again from the plan's scan) and the
/// <see cref="MoveGuard"/> allows it. A hand-edited or stale plan therefore can't move anything else.
/// </summary>
public static class PlanValidator
{
    public static void CheckHeader(CleanupPlan plan)
    {
        if (plan.Kind != CleanupPlan.KindName)
            throw new InvalidDataException(Lang.T("Not a Ferahla plan file", "Ferahla plan dosyası değil"));
        if (plan.Schema < 1 || plan.Schema > CleanupPlan.CurrentSchema)
            throw new InvalidDataException(Lang.T($"Plan schema {plan.Schema} is not supported. Update Ferahla.", $"Plan şeması {plan.Schema} desteklenmiyor. Ferahla'yı güncelle."));
        // A scan id, never a path: the plan must not point Ferahla at some other snapshot file.
        if (string.IsNullOrEmpty(plan.ScanId) || plan.ScanId.Length > 64 || !plan.ScanId.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            throw new InvalidDataException(Lang.T("The plan has no valid scan id", "Planda geçerli bir tarama kimliği yok"));
    }

    public static List<ApprovedMove> Validate(CleanupPlan plan, FindingsResult current, MoveGuard guard, List<ItemOutcome> rejected)
    {
        CheckHeader(plan);
        var byId = current.Findings.ToDictionary(f => f.Id, StringComparer.Ordinal);
        var approved = new List<ApprovedMove>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pf in plan.Findings)
        {
            byId.TryGetValue(pf.Id ?? "", out var f);
            foreach (var pi in pf.Items)
            {
                var path = pi.Path ?? "";
                string? reason = null;
                FindingItem? item = null;
                if (f is null) reason = Lang.T("No longer suggested (the disk changed since the report, or the plan was edited)", "Artık önerilmiyor (rapordan sonra disk değişti ya da plan elle değiştirildi)");
                else if (f.Action != ActionKind.Quarantine) reason = Lang.T($"Not moved by Ferahla (action: {f.Action.ToString().ToLowerInvariant()})", $"Ferahla taşımaz (eylem: {f.Action.ToString().ToLowerInvariant()})");
                else if ((item = f.Items.FirstOrDefault(i => string.Equals(PathUtil.Normalize(i.Path), PathUtil.Normalize(path), StringComparison.OrdinalIgnoreCase))) is null)
                    reason = Lang.T("Not part of this finding", "Bu bulgunun parçası değil");
                else reason = guard.Check(item.Path);

                if (reason is null && !seen.Add(PathUtil.Normalize(item!.Path))) continue; // listed twice
                if (reason is not null)
                {
                    rejected.Add(new ItemOutcome { Path = path, FindingId = pf.Id, Status = OutcomeStatus.Skipped, Bytes = pi.Bytes, Reason = reason });
                    continue;
                }
                approved.Add(new ApprovedMove(PathUtil.Normalize(item!.Path), item.IsDir, item.Bytes, f!.Id, f.RuleId, f.KeepRecentHours));
            }
        }

        // Moving a folder takes everything in it: an item inside another selected folder goes with it.
        var dirs = approved.Where(a => a.IsDir).Select(a => a.Path).ToList();
        approved.RemoveAll(a =>
        {
            var outer = dirs.FirstOrDefault(d => !string.Equals(d, a.Path, StringComparison.OrdinalIgnoreCase) && PathUtil.IsUnder(a.Path, d));
            if (outer is null) return false;
            rejected.Add(new ItemOutcome { Path = a.Path, FindingId = a.FindingId, Status = OutcomeStatus.Skipped, Bytes = a.PlannedBytes, Reason = Lang.T($"Moves with {outer}", $"{outer} ile birlikte taşınır") });
            return true;
        });
        return approved;
    }
}

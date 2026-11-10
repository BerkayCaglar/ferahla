using Ferahla.Core.Findings;
using Ferahla.Core.Rules;

namespace Ferahla.Core.Claude;

/// <summary>What an analysis run covers. The UI, the server's target count and the MCP tools share these rules.</summary>
public static class AnalysisScope
{
    public const string All = "all";
    /// <summary>Only suggestions that have no note yet.</summary>
    public const string Unnoted = "unnoted";
    /// <summary>What is in quarantine, before the user deletes it for good.</summary>
    public const string Quarantine = "quarantine";

    public static string Normalize(string? scope) => scope?.Trim().ToLowerInvariant() switch
    {
        Unnoted => Unnoted,
        Quarantine => Quarantine,
        _ => All,
    };

    /// <summary>A suggestion Claude should write a note on: not info-only, and at least 10 MB or one to review.</summary>
    public static bool Eligible(Finding f) => f.Action != ActionKind.Info && (f.Bytes >= 10L << 20 || f.Risk == Risk.Review);

    /// <summary>Turn budget for a run with <paramref name="target"/> notes to write (notes go in batches; the rest is reading).</summary>
    public static int Turns(int target) => Math.Clamp(20 + (target + 1) / 2, 25, 150);
}

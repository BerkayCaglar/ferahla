namespace Ferahla.Core.Claude;

/// <summary>
/// The AI coding agents Ferahla can ask for a second opinion: the user's own Claude Code or Codex CLI installation.
/// The id is also the note source written by <c>ferahla mcp --agent</c>.
/// </summary>
public static class Agents
{
    public const string Claude = "claude";
    public const string Codex = "codex";
    public static readonly string[] All = [Claude, Codex];

    public static bool IsKnown(string? id) => id is Claude or Codex;

    /// <summary>A known agent id, or Claude for anything else (older clients send none).</summary>
    public static string Normalize(string? id)
    {
        var s = (id ?? "").Trim().ToLowerInvariant();
        return IsKnown(s) ? s : Claude;
    }

    public static string Name(string id) => id == Codex ? "Codex" : "Claude";
}

/// <summary>Turns one line of an agent's JSON output into UI events.</summary>
public interface IAgentParser
{
    List<AnalysisEvent> Feed(string line);
}

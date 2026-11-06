using System.Text.Json.Serialization;
using Ferahla.Core.Text;

namespace Ferahla.Core.Rules;

[JsonConverter(typeof(JsonStringEnumConverter<Risk>))]
public enum Risk
{
    /// <summary>Cache or junk; regenerated automatically.</summary>
    [JsonStringEnumMemberName("safe")] Safe,
    /// <summary>Comes back, but at a cost (rebuild, re-download).</summary>
    [JsonStringEnumMemberName("regenerable")] Regenerable,
    /// <summary>Look before deciding.</summary>
    [JsonStringEnumMemberName("review")] Review,
    /// <summary>The user's own data; shown for information only.</summary>
    [JsonStringEnumMemberName("personal")] Personal,
    /// <summary>Never suggested.</summary>
    [JsonStringEnumMemberName("protected")] Protected,
}

[JsonConverter(typeof(JsonStringEnumConverter<Maturity>))]
public enum Maturity { [JsonStringEnumMemberName("pending")] Pending, [JsonStringEnumMemberName("release")] Release }

[JsonConverter(typeof(JsonStringEnumConverter<ActionKind>))]
public enum ActionKind
{
    /// <summary>Move to quarantine (undoable).</summary>
    [JsonStringEnumMemberName("quarantine")] Quarantine,
    /// <summary>Run a system command (e.g. powercfg -h off); shown, run only from the UI with elevation.</summary>
    [JsonStringEnumMemberName("command")] Command,
    /// <summary>Uninstall through the app's UninstallString.</summary>
    [JsonStringEnumMemberName("uninstall")] Uninstall,
    /// <summary>Remove through a launcher/app (Steam, Unity Hub…).</summary>
    [JsonStringEnumMemberName("launcher")] Launcher,
    /// <summary>Information only.</summary>
    [JsonStringEnumMemberName("info")] Info,
}

public sealed class RuleFile
{
    [JsonPropertyName("$schema")] public string? Schema { get; set; }
    public List<Rule> Rules { get; set; } = [];
}

public sealed class Rule
{
    public string Id { get; set; } = "";
    public string Category { get; set; } = "";
    public Risk Risk { get; set; }
    public Maturity Maturity { get; set; } = Maturity.Release;
    public RuleMatch Match { get; set; } = new();
    public ActionKind Action { get; set; } = ActionKind.Quarantine;
    /// <summary>For <see cref="ActionKind.Command"/>.</summary>
    public string? Command { get; set; }
    /// <summary>Ignore matches smaller than this.</summary>
    public long MinBytes { get; set; } = 10L << 20;
    public LocalizedText Title { get; set; } = new();
    public LocalizedText? Reason { get; set; }
    public LocalizedText? WhatBreaks { get; set; }
    public LocalizedText? HowToRestore { get; set; }
    /// <summary>Files modified within this many hours are left alone when acting (e.g. Temp).</summary>
    public int? KeepRecentHours { get; set; }
}

/// <summary>Exactly one of Path, Marker or DirName (or Detector) is set.</summary>
public sealed class RuleMatch
{
    /// <summary>Known locations, with %KnownFolder% tokens; '*' matches one segment, '**' any number.</summary>
    public List<string>? Path { get; set; }
    /// <summary>Folder names (globs) that a path rule must not match, e.g. "Steamworks Shared".</summary>
    public List<string>? ExcludeNames { get; set; }
    /// <summary>Project marker(s); all must be present in the directory (e.g. UnityAssets + UnityProjectSettings).</summary>
    public List<string>? Marker { get; set; }
    /// <summary>Folders next to the marker to suggest (e.g. "target", "node_modules", "cmake-build-*").</summary>
    public List<string>? Targets { get; set; }
    /// <summary>A folder name to look for anywhere under <see cref="Under"/>.</summary>
    public List<string>? DirName { get; set; }
    /// <summary>Roots for DirName rules (default: %UserProfile%).</summary>
    public List<string>? Under { get; set; }
    /// <summary>Name of a coded detector (installers, archive-twins…).</summary>
    public string? Detector { get; set; }
}

public sealed class LocalizedText
{
    public string En { get; set; } = "";
    public string Tr { get; set; } = "";

    public string Resolve() => Lang.IsTurkish && Tr.Length > 0 ? Tr : En;

    /// <summary>Resolve with {name} placeholders filled.</summary>
    public string Resolve(IReadOnlyDictionary<string, string> args)
    {
        var s = Resolve();
        foreach (var (k, v) in args) s = s.Replace("{" + k + "}", v, StringComparison.Ordinal);
        return s;
    }
}

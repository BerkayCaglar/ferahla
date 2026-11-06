using System.Text.Json;
using Ferahla.Core.Json;
using Ferahla.Core.Model;

namespace Ferahla.Core.Rules;

public sealed class RuleSet(IReadOnlyList<Rule> rules, IReadOnlyList<string> errors)
{
    public IReadOnlyList<Rule> Rules { get; } = rules;
    /// <summary>Validation problems; rules with errors are not loaded.</summary>
    public IReadOnlyList<string> Errors { get; } = errors;
}

public static class RuleLoader
{
    /// <summary>Built-in rules (embedded <c>rules/*.json</c>).</summary>
    public static RuleSet LoadBuiltIn(bool includePending = false)
    {
        var asm = typeof(RuleLoader).Assembly;
        var sources = asm.GetManifestResourceNames()
            .Where(n => n.StartsWith("rules/", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal) && !n.EndsWith("schema.json", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n => (n, (Func<Stream>)(() => asm.GetManifestResourceStream(n)!)));
        return Load(sources, includePending);
    }

    public static RuleSet LoadFiles(IEnumerable<string> paths, bool includePending = false) =>
        Load(paths.Select(p => (p, (Func<Stream>)(() => File.OpenRead(p)))), includePending);

    public static RuleSet Load(IEnumerable<(string Name, Func<Stream> Open)> sources, bool includePending)
    {
        var rules = new List<Rule>();
        var errors = new List<string>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, open) in sources)
        {
            RuleFile? file;
            try
            {
                using var s = open();
                file = JsonSerializer.Deserialize(s, FerahlaJson.Default.RuleFile);
            }
            catch (JsonException ex)
            {
                errors.Add($"{name}: {ex.Message}");
                continue;
            }
            foreach (var r in file?.Rules ?? [])
            {
                var problems = Validate(r);
                if (!ids.Add(r.Id)) problems.Add("duplicate id");
                if (problems.Count > 0) { errors.Add($"{name}: {r.Id}: {string.Join("; ", problems)}"); continue; }
                if (r.Maturity == Maturity.Pending && !includePending) continue;
                rules.Add(r);
            }
        }
        return new RuleSet(rules, errors);
    }

    public static List<string> Validate(Rule r)
    {
        var p = new List<string>();
        if (string.IsNullOrWhiteSpace(r.Id)) p.Add("missing id");
        if (string.IsNullOrWhiteSpace(r.Category)) p.Add("missing category");
        if (string.IsNullOrWhiteSpace(r.Title.En) || string.IsNullOrWhiteSpace(r.Title.Tr)) p.Add("title needs en and tr");
        var m = r.Match;
        var kinds = (m.Path is { Count: > 0 } ? 1 : 0) + (m.Marker is { Count: > 0 } ? 1 : 0) + (m.DirName is { Count: > 0 } ? 1 : 0) + (m.Detector is { Length: > 0 } ? 1 : 0);
        if (kinds != 1) p.Add("match needs exactly one of path, marker, dirName, detector");
        if (m.Marker is { Count: > 0 })
        {
            foreach (var mk in m.Marker)
                if (!Enum.TryParse<DirMarkers>(mk, out var v) || v == DirMarkers.None) p.Add($"unknown marker '{mk}'");
            if (m.Targets is not { Count: > 0 }) p.Add("marker rule needs targets");
        }
        if (r.Action == ActionKind.Command && string.IsNullOrWhiteSpace(r.Command)) p.Add("command action needs command");
        if (r.Risk == Risk.Protected && r.Action != ActionKind.Info) p.Add("protected rules can only be info");
        if (r.Risk == Risk.Personal && r.Action == ActionKind.Quarantine) p.Add("personal data is never suggested for quarantine");
        return p;
    }
}

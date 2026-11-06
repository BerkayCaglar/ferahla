using System.Text.Json;
using System.Text.Json.Serialization;
using Ferahla.Core.Findings;
using Ferahla.Core.Json;

namespace Ferahla.Core.Notes;

[JsonConverter(typeof(JsonStringEnumConverter<NoteVerdict>))]
public enum NoteVerdict
{
    [JsonStringEnumMemberName("none")] None,
    /// <summary>Safe to move to quarantine.</summary>
    [JsonStringEnumMemberName("remove")] Remove,
    /// <summary>Look first.</summary>
    [JsonStringEnumMemberName("check")] Check,
    [JsonStringEnumMemberName("keep")] Keep,
}

public sealed class Note
{
    public string FindingId { get; set; } = "";
    public string? ScanId { get; set; }
    public string Text { get; set; } = "";
    public NoteVerdict Verdict { get; set; }
    /// <summary><c>claude</c> or <c>user</c>.</summary>
    public string Source { get; set; } = "user";
    public DateTimeOffset At { get; set; }
}

public sealed class NoteFile
{
    public int Schema { get; set; } = 1;
    public Dictionary<string, Note> Notes { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Notes on findings, keyed by finding id. Ids are stable across scans (rule + path), so a note stays with its
/// finding after a rescan. One small JSON file; written by the MCP server (Claude) and the CLI, read by the UI.
/// </summary>
public sealed class NoteStore(string home)
{
    public const int MaxText = 1500;
    public const int MaxNotes = 2000;
    private static readonly Lock WriteLock = new();

    public string FilePath { get; } = Path.Combine(home, "notes", "notes.json");

    public NoteFile Load()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(FilePath)) return new NoteFile();
                using var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return JsonSerializer.Deserialize(fs, FerahlaJson.Default.NoteFile) ?? new NoteFile();
            }
            catch (IOException) when (attempt < 5) { Thread.Sleep(40); }
            catch (JsonException) { return new NoteFile(); }
        }
    }

    /// <summary>Adds or replaces the note of a finding. Text is trimmed to <see cref="MaxText"/> characters.</summary>
    public Note Set(string findingId, string text, NoteVerdict verdict, string source, string? scanId, DateTimeOffset now)
    {
        if (!IsFindingId(findingId)) throw new ArgumentException("not a finding id: " + findingId);
        text = (text ?? "").Trim();
        if (text.Length > MaxText) text = text[..MaxText] + "…";
        var note = new Note { FindingId = findingId, ScanId = scanId, Text = text, Verdict = verdict, Source = source, At = now };
        lock (WriteLock)
        {
            var file = Load();
            if (text.Length == 0) file.Notes.Remove(findingId);
            else file.Notes[findingId] = note;
            if (file.Notes.Count > MaxNotes)
                foreach (var old in file.Notes.Values.OrderBy(n => n.At).Take(file.Notes.Count - MaxNotes).ToList())
                    file.Notes.Remove(old.FindingId);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + "." + Environment.ProcessId + ".tmp";
            File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(file, FerahlaJson.Indented.NoteFile));
            for (var attempt = 0; ; attempt++)
            {
                try { File.Move(tmp, FilePath, overwrite: true); break; }
                catch (IOException) when (attempt < 10) { Thread.Sleep(30); }
            }
        }
        return note;
    }

    /// <summary>Copies notes onto the findings they belong to.</summary>
    public void Apply(FindingsResult r)
    {
        var notes = Load().Notes;
        if (notes.Count == 0) return;
        foreach (var f in r.Findings)
            if (notes.TryGetValue(f.Id, out var n))
            {
                f.Note = n.Text;
                f.NoteVerdict = n.Verdict;
                f.NoteSource = n.Source;
                f.NoteAt = n.At;
            }
    }

    public static bool IsFindingId(string id) =>
        id.Length == 12 && id.StartsWith("f-", StringComparison.Ordinal) && id.AsSpan(2).ContainsAnyExcept("0123456789abcdef") == false;
}

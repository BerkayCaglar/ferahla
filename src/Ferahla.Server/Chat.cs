using System.Runtime.Versioning;
using Ferahla.Core.Claude;
using Ferahla.Core.Text;
using Ferahla.Windows;

namespace Ferahla.Server;

public sealed class ChatRequest
{
    public string? Scan { get; set; }
    public string FindingId { get; set; } = "";
    /// <summary><see cref="Core.Claude.Agents"/>: claude (default) or codex.</summary>
    public string? Agent { get; set; }
    /// <summary><c>ask</c> (free text), <c>quick</c> or <c>deep</c> analysis.</summary>
    public string Mode { get; set; } = "ask";
    public string? Text { get; set; }
    public string? Model { get; set; }
}

public sealed class ChatDto
{
    public List<AgentInfo> Agents { get; set; } = [];
    /// <summary>The agent this finding's conversation is with, if it has one.</summary>
    public string? Agent { get; set; }
    public string FindingId { get; set; } = "";
    public bool Running { get; set; }
    /// <summary>The finding another chat is running for, if any (one run at a time).</summary>
    public string? BusyWith { get; set; }
    public List<AnalysisEvent> Events { get; set; } = [];
}

/// <summary>A chat event, keyed by the finding it belongs to.</summary>
public sealed class ChatEventDto
{
    public string FindingId { get; set; } = "";
    public AnalysisEvent Event { get; set; } = new();
    /// <summary>True on the last event of a run.</summary>
    public bool? Done { get; set; }
}

/// <summary>
/// An agent (Claude Code or Codex) about one suggestion: a quick or deep look, or the user's own questions, as one
/// session per finding (follow-ups resume it; switching the agent starts a new session and keeps the conversation on
/// screen). Same isolation as the scan analysis (<see cref="AgentCli"/>). One run at a time and never next to an
/// analysis, so two MCP servers don't write the notes file at once. Chats live in memory only.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class Chat(string home, EventHub hub, Analysis analysis, Jobs jobs)
{
    public const int MaxText = 2000;
    private const int MaxChats = 50;

    private sealed class Session
    {
        public string Agent = Agents.Claude;
        /// <summary>Claude: set up front. Codex: the thread id from its first event.</summary>
        public string? SessionId;
        public string ScanId = "";
        public bool Started;
        public DateTimeOffset LastUsed;
        public List<AnalysisEvent> Events = [];
    }

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Session> _chats = new(StringComparer.Ordinal);
    private string? _running;
    private string? _runningAgent;
    private JobObject? _job;
    private int _seq;

    public bool Running { get { lock (_lock) return _running is not null; } }

    public string RunningName { get { lock (_lock) return Agents.Name(_runningAgent ?? Agents.Claude); } }

    public ChatDto State(string findingId, List<AgentInfo> agents)
    {
        lock (_lock)
        {
            var dto = new ChatDto { Agents = agents, FindingId = findingId, Running = _running == findingId, BusyWith = _running };
            if (_chats.TryGetValue(findingId, out var s) && s.ScanId.Length > 0) { dto.Events = [.. s.Events]; dto.Agent = s.Agent; }
            return dto;
        }
    }

    /// <returns>An error for the user, or null when the run started.</returns>
    public string? Start(string scanId, ChatRequest r, Action onFinished)
    {
        var agent = Agents.Normalize(r.Agent);
        var cli = AgentCli.Get(agent);
        var mode = r.Mode is "quick" or "deep" ? r.Mode : "ask";
        var text = (r.Text ?? "").Trim();
        if (mode == "ask" && text.Length == 0) return Lang.T("Write a question first", "Önce bir soru yaz");
        if (text.Length > MaxText) text = text[..MaxText];
        if (jobs.Running && jobs.Current?.Kind is JobKind.Analysis or JobKind.Update) return Lang.T("Wait for the running job to finish", "Çalışan işin bitmesini bekle");

        Session s;
        lock (_lock)
        {
            if (_running is not null) return Lang.T($"{Agents.Name(_runningAgent ?? agent)} is still answering", $"{Agents.Name(_runningAgent ?? agent)} hâlâ yanıtlıyor");
            if (!_chats.TryGetValue(r.FindingId, out s!) || s.ScanId != scanId)
            {
                // A new scan means new data: start a fresh session for it.
                s = new Session { ScanId = scanId, Agent = agent };
                _chats[r.FindingId] = s;
                if (_chats.Count > MaxChats)
                    foreach (var old in _chats.Where(c => c.Key != r.FindingId).OrderBy(c => c.Value.LastUsed).Take(_chats.Count - MaxChats).ToList())
                        _chats.Remove(old.Key);
            }
            else if (s.Agent != agent)
            {
                // Another agent: its own new session; the conversation so far stays on screen.
                s = new Session { ScanId = scanId, Agent = agent, Events = s.Events };
                _chats[r.FindingId] = s;
            }
            if (s.SessionId is null && cli.PresetSessionId) s.SessionId = Guid.NewGuid().ToString();
            s.LastUsed = DateTimeOffset.UtcNow;
            _running = r.FindingId;
            _runningAgent = agent;
        }

        var shown = mode switch
        {
            "quick" => Lang.T("Quick analysis", "Hızlı analiz"),
            "deep" => Lang.T("Deep analysis", "Derin analiz"),
            _ => text,
        };
        Add(r.FindingId, s, new AnalysisEvent { Kind = AnalysisEventKind.User, Text = shown, Subtype = mode, Agent = agent });
        var message = Message(r.FindingId, mode, text, first: !s.Started);
        var resume = s.Started;
        s.Started = true;
        var turns = mode switch { "deep" => 30, "quick" => 8, _ => 15 };

        _ = Task.Run(async () =>
        {
            try
            {
                await cli.Run(new AgentRun
                {
                    WorkDir = cli.WorkDir(home), PromptName = "chat", Home = home, ScanId = scanId, SessionId = s.SessionId,
                    Resume = resume, MaxTurns = turns, Model = r.Model, Message = message,
                }, ev =>
                {
                    analysis.RecordUsage(ev);
                    if (ev.Kind == AnalysisEventKind.Init && ev.SessionId is { } sid) lock (_lock) s.SessionId = sid;
                    if (ev.Kind is AnalysisEventKind.RateLimit or AnalysisEventKind.Thinking or AnalysisEventKind.Init) return;
                    Add(r.FindingId, s, ev);
                }, j => { lock (_lock) _job = j; }, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Add(r.FindingId, s, new AnalysisEvent { Kind = AnalysisEventKind.Error, Text = ex.Message });
                // A failed first run leaves no usable session; the next message starts a new one.
                if (!resume) lock (_lock) { s.SessionId = cli.PresetSessionId ? Guid.NewGuid().ToString() : null; s.Started = false; }
            }
            finally
            {
                lock (_lock) { _running = null; _runningAgent = null; _job = null; }
                hub.Publish("chat", new ChatEventDto { FindingId = r.FindingId, Event = new AnalysisEvent { Kind = AnalysisEventKind.Result, Subtype = "finished", Seq = Interlocked.Increment(ref _seq) }, Done = true }, AnalysisJson.Web.ChatEventDto);
                onFinished();
            }
        });
        return null;
    }

    public bool Stop()
    {
        lock (_lock)
        {
            if (_job is null) return false;
            _job.Terminate();
            return true;
        }
    }

    private void Add(string findingId, Session s, AnalysisEvent ev)
    {
        ev.Seq = Interlocked.Increment(ref _seq);
        // Which agent wrote each reply, for the labels in the drawer.
        if (ev.Kind is AnalysisEventKind.Text or AnalysisEventKind.Note or AnalysisEventKind.Tool) ev.Agent ??= s.Agent;
        // Live tabs get every streamed piece; the kept history joins them into one text event per reply.
        hub.Publish("chat", new ChatEventDto { FindingId = findingId, Event = ev }, AnalysisJson.Web.ChatEventDto);
        lock (_lock)
        {
            if (ev.Kind == AnalysisEventKind.Text && s.Events.Count > 0 && s.Events[^1] is { Kind: AnalysisEventKind.Text } last)
                s.Events[^1] = new AnalysisEvent { Kind = AnalysisEventKind.Text, Seq = ev.Seq, Text = last.Text + ev.Text, Agent = last.Agent };
            else if (s.Events.Count < 2000) s.Events.Add(ev);
            // Which agent wrote each reply, for the labels in the drawer.
        }
    }

    internal static string Message(string findingId, string mode, string text, bool first)
    {
        var tr = Lang.IsTurkish;
        var lang = tr ? "Türkçe yanıt ver." : "Answer in English.";
        var about = first
            ? (tr ? $"Kullanıcı {findingId} önerisine bakıyor. Önce get_finding ile onu oku. " : $"The user is looking at suggestion {findingId}. Read it with get_finding first. ")
            : "";
        return mode switch
        {
            "quick" => about + (tr
                ? $"Hızlı analiz: {findingId} için kararını ver ve add_note ile 1-2 cümlelik bir not kaydet. Sonra kullanıcıya 2-3 cümleyle anlat. {lang}"
                : $"Quick analysis: decide on {findingId} and save a 1-2 sentence note with add_note. Then tell the user in 2-3 sentences. {lang}"),
            "deep" => about + (tr
                ? $"Derin analiz: {findingId} öğelerinin içine inspect_path ile birkaç seviye in, verify_finding ile diske karşı doğrula, işe yarıyorsa get_diff'e bak. Sonra add_note ile en fazla 5 cümlelik ayrıntılı bir not kaydet ve bulduklarını kullanıcıya anlat. {lang}"
                : $"Deep analysis: go a few levels into the items of {findingId} with inspect_path, check them against the disk with verify_finding, and look at get_diff if it helps. Then save a detailed note (up to 5 sentences) with add_note and tell the user what you found. {lang}"),
            _ => about + (tr ? $"Kullanıcının sorusu: {text}\n\n{lang}" : $"The user's question: {text}\n\n{lang}"),
        };
    }
}

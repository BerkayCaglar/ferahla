using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;

namespace Ferahla.Server;

/// <summary>Server-sent events to every open UI tab: job progress, and "state" when data changed (refetch).</summary>
internal sealed class EventHub
{
    private readonly Lock _lock = new();
    private readonly List<Channel<string>> _subs = [];

    public int Subscribers { get { lock (_lock) return _subs.Count; } }
    /// <summary>Last time a tab connected or disconnected (for the idle shutdown).</summary>
    public DateTimeOffset LastChange { get; private set; } = DateTimeOffset.UtcNow;

    public void Publish<T>(string type, T value, JsonTypeInfo<T> info)
    {
        var frame = $"event: {type}\ndata: {JsonSerializer.Serialize(value, info)}\n\n";
        lock (_lock)
            foreach (var c in _subs) c.Writer.TryWrite(frame);
    }

    public void Changed(string what) => Publish("state", new OkDto { Message = what }, ApiJson.Web.OkDto);

    public async Task Stream(HttpContext ctx, Func<string> hello)
    {
        var ch = Channel.CreateBounded<string>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        lock (_lock) { _subs.Add(ch); LastChange = DateTimeOffset.UtcNow; }
        var ct = ctx.RequestAborted;
        try
        {
            var res = ctx.Response;
            res.ContentType = "text/event-stream; charset=utf-8";
            res.Headers.CacheControl = "no-store";
            res.Headers["X-Accel-Buffering"] = "no";
            await Write(res, hello(), ct);
            while (!ct.IsCancellationRequested)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                string frame;
                try { frame = await ch.Reader.ReadAsync(timeout.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { frame = ": keep-alive\n\n"; }
                await Write(res, frame, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        finally
        {
            lock (_lock) { _subs.Remove(ch); LastChange = DateTimeOffset.UtcNow; }
        }
    }

    private static async Task Write(HttpResponse res, string frame, CancellationToken ct)
    {
        await res.Body.WriteAsync(Encoding.UTF8.GetBytes(frame), ct);
        await res.Body.FlushAsync(ct);
    }
}

/// <summary>One long-running job at a time (scan, quarantine, restore, delete), with throttled progress events.</summary>
internal sealed class Jobs(EventHub hub)
{
    private readonly Lock _lock = new();
    private JobStatus? _current;
    private CancellationTokenSource? _cts;
    private long _lastPublish;

    public bool Running { get { lock (_lock) return _current?.State == JobState.Running; } }

    public JobStatus? Current { get { lock (_lock) return _current?.Clone(); } }

    /// <summary>Starts <paramref name="work"/> on the thread pool; false if another job is running.</summary>
    public bool TryStart(JobKind kind, string? target, bool dryRun, Func<CancellationToken, Task> work, Action? onFinished, out JobStatus status)
    {
        lock (_lock)
        {
            if (_current?.State == JobState.Running) { status = _current.Clone(); return false; }
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _current = new JobStatus
            {
                Id = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)),
                Kind = kind, State = JobState.Running, StartedAt = DateTimeOffset.UtcNow, Target = target, DryRun = dryRun,
            };
            status = _current.Clone();
        }
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await work(ct);
                Update(s => s.State = JobState.Done, force: true);
            }
            catch (OperationCanceledException)
            {
                Update(s => s.State = JobState.Cancelled, force: true);
            }
            catch (Exception ex)
            {
                Update(s => { s.State = JobState.Failed; s.Error = ex.Message; }, force: true);
            }
            finally
            {
                Update(s => s.FinishedAt = DateTimeOffset.UtcNow, force: true);
                try { onFinished?.Invoke(); } catch { /* a refresh failure must not hide the job result */ }
            }
        });
        hub.Publish("job", status, ApiJson.Web.JobStatus);
        return true;
    }

    public bool Cancel()
    {
        lock (_lock)
        {
            if (_current?.State != JobState.Running) return false;
            _cts?.Cancel();
            return true;
        }
    }

    /// <summary>Changes the current job; progress is published at most 5 times a second, final states always.</summary>
    public void Update(Action<JobStatus> change, bool force = false)
    {
        JobStatus snapshot;
        lock (_lock)
        {
            if (_current is null) return;
            change(_current);
            var now = Environment.TickCount64;
            if (!force && now - _lastPublish < 200) return;
            _lastPublish = now;
            snapshot = _current.Clone();
        }
        hub.Publish("job", snapshot, ApiJson.Web.JobStatus);
    }
}

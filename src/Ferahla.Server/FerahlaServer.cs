using System.Net;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ferahla.Core.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ferahla.Server;

public sealed class ServerOptions
{
    public required string Home { get; init; }
    /// <summary>0 = a random free port.</summary>
    public int Port { get; init; }
    /// <summary>Stop when no UI tab has been connected for this long and no job runs. Null = never.</summary>
    public TimeSpan? IdleTimeout { get; init; } = TimeSpan.FromMinutes(3);
}

/// <summary>The local UI server: 127.0.0.1 only, random port, per-launch token (see <see cref="Security"/>).</summary>
[SupportedOSPlatform("windows")]
public sealed class FerahlaServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly CancellationTokenSource _stop = new();
    private int _port;

    public string Token { get; }
    public int Port => _port;
    public string BaseUrl => $"http://127.0.0.1:{_port}";
    /// <summary>The address to open: the token travels in the fragment, which browsers never send to a server.</summary>
    public string LaunchUrl => $"{BaseUrl}/#token={Token}";
    internal EventHub Hub { get; }
    internal Jobs Jobs { get; }

    private FerahlaServer(WebApplication app, string token, EventHub hub, Jobs jobs)
    {
        _app = app;
        Token = token;
        Hub = hub;
        Jobs = jobs;
    }

    public static async Task<FerahlaServer> StartAsync(ServerOptions o)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = 16L << 20;
            k.Listen(IPAddress.Loopback, o.Port);
        });
        var app = builder.Build();

        var token = Security.NewToken();
        var hub = new EventHub();
        var jobs = new Jobs(hub);
        var state = new AppState(new SnapshotStore(o.Home));
        FerahlaServer? server = null;
        var security = new Security(token, () => server!._port);
        var web = new StaticWeb();
        server = new FerahlaServer(app, token, hub, jobs);
        var analysis = new Analysis(o.Home, hub, jobs);
        var chat = new Chat(o.Home, hub, analysis, jobs);
        var api = new Api(state, jobs, hub, analysis, chat, () => server._stop.Cancel());

        app.Use((ctx, next) => security.Invoke(ctx, next));
        app.Use(async (ctx, next) =>
        {
            if (!ctx.Request.Path.StartsWithSegments("/api") && await web.TryServe(ctx)) return;
            await next(ctx);
        });
        api.Map(app);

        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        server._port = new Uri(address).Port;
        if (o.IdleTimeout is { } idle) _ = server.Watch(idle);
        return server;
    }

    /// <summary>Stops once the last tab has been gone for <paramref name="idle"/> (never while a job runs).</summary>
    private async Task Watch(TimeSpan idle)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), _stop.Token);
                if (Hub.Subscribers == 0 && !Jobs.Running && DateTimeOffset.UtcNow - Hub.LastChange > idle) _stop.Cancel();
            }
        }
        catch (OperationCanceledException) { }
    }

    public void RequestStop() => _stop.Cancel();

    public async Task WaitForShutdownAsync(CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        try { await Task.Delay(Timeout.Infinite, linked.Token); }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        Jobs.Cancel();
        using (var t = new CancellationTokenSource(TimeSpan.FromSeconds(3))) await _app.StopAsync(t.Token);
        await _app.DisposeAsync();
        _stop.Dispose();
    }
}

/// <summary>
/// <c>&lt;home&gt;\ui.json</c>: where the running UI listens, so a second <c>ferahla</c> opens the same one.
/// The home folder is in the user's own profile; nobody else can read the token.
/// </summary>
public sealed class InstanceInfo
{
    public int Pid { get; set; }
    public int Port { get; set; }
    public string Token { get; set; } = "";

    public static string PathIn(string home) => System.IO.Path.Combine(home, "ui.json");

    public void Save(string home)
    {
        Directory.CreateDirectory(home);
        File.WriteAllBytes(PathIn(home), JsonSerializer.SerializeToUtf8Bytes(this, InstanceJson.Default.InstanceInfo));
    }

    public static InstanceInfo? Load(string home)
    {
        try { return JsonSerializer.Deserialize(File.ReadAllBytes(PathIn(home)), InstanceJson.Default.InstanceInfo); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>True if that server answers with that token.</summary>
    public async Task<bool> AliveAsync()
    {
        try
        {
            System.Diagnostics.Process.GetProcessById(Pid).Dispose();
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{Port}/api/ping");
            req.Headers.Add(Security.TokenHeader, Token);
            using var res = await http.SendAsync(req);
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is ArgumentException or HttpRequestException or TaskCanceledException or InvalidOperationException) { return false; }
    }

    public string LaunchUrl => $"http://127.0.0.1:{Port}/#token={Token}";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(InstanceInfo))]
internal sealed partial class InstanceJson : JsonSerializerContext;

// Spike 01: embedded static files + SSE + random port under NativeAOT.
// Results: docs/spikes.md
using System.Net;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0)); // random port
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJson.Default));

var app = builder.Build();

var files = new ManifestEmbeddedFileProvider(typeof(Program).Assembly, "wwwroot");
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
app.UseStaticFiles(new StaticFileOptions { FileProvider = files });

app.MapGet("/api/info", () => new Info(
    Environment.Version.ToString(),
    RuntimeFeature.IsDynamicCodeSupported,
    Environment.ProcessPath ?? ""));

// .NET 10 built-in SSE result, typed JSON payload
app.MapGet("/sse/typed", (CancellationToken ct) =>
    TypedResults.ServerSentEvents(TypedTicks(ct), eventType: "tick"));

// .NET 10 built-in SSE with SseItem (event type + id)
app.MapGet("/sse/items", (CancellationToken ct) =>
    TypedResults.ServerSentEvents(ItemTicks(ct)));

// Hand-written SSE (fallback)
app.MapGet("/sse/manual", async (HttpContext ctx, CancellationToken ct) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    for (var i = 1; i <= 5; i++)
    {
        var json = JsonSerializer.Serialize(new Tick(i, DateTimeOffset.UtcNow, "manual ğüşıöç"), AppJson.Default.Tick);
        await ctx.Response.WriteAsync($"event: tick\ndata: {json}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
        await Task.Delay(300, ct);
    }
});

await app.StartAsync();
var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
Console.WriteLine($"LISTENING {address}");
await app.WaitForShutdownAsync();

static async IAsyncEnumerable<Tick> TypedTicks([EnumeratorCancellation] CancellationToken ct)
{
    for (var i = 1; i <= 5; i++)
    {
        yield return new Tick(i, DateTimeOffset.UtcNow, "typed ğüşıöç");
        await Task.Delay(300, ct);
    }
}

static async IAsyncEnumerable<SseItem<Tick>> ItemTicks([EnumeratorCancellation] CancellationToken ct)
{
    for (var i = 1; i <= 5; i++)
    {
        yield return new SseItem<Tick>(new Tick(i, DateTimeOffset.UtcNow, "item"), i == 5 ? "done" : "tick") { EventId = i.ToString() };
        await Task.Delay(300, ct);
    }
}

record Info(string Runtime, bool DynamicCode, string ProcessPath);
record Tick(int N, DateTimeOffset At, string Text);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Info))]
[JsonSerializable(typeof(Tick))]
partial class AppJson : JsonSerializerContext;

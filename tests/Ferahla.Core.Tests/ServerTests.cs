using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Ferahla.Server;

namespace Ferahla.Core.Tests;

public sealed class ServerTests : IAsyncLifetime
{
    private readonly TempTree _t = new(outsideTemp: true);
    private FerahlaServer _server = null!;
    private readonly HttpClient _http = new();

    public async ValueTask InitializeAsync() =>
        _server = await FerahlaServer.StartAsync(new ServerOptions { Home = _t.Dir("home"), IdleTimeout = null });

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        _http.Dispose();
        _t.Dispose();
    }

    private HttpRequestMessage Req(HttpMethod m, string path, string? json = null, bool token = true, string contentType = "application/json")
    {
        var r = new HttpRequestMessage(m, _server.BaseUrl + path);
        if (token) r.Headers.Add("X-Ferahla-Token", _server.Token);
        if (json is not null) r.Content = new StringContent(json, Encoding.UTF8, contentType);
        return r;
    }

    private async Task<(HttpStatusCode Code, string Body)> Send(HttpRequestMessage r)
    {
        using var res = await _http.SendAsync(r, TestContext.Current.CancellationToken);
        return (res.StatusCode, await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Api_needs_the_token()
    {
        Assert.Equal(HttpStatusCode.OK, (await Send(Req(HttpMethod.Get, "/api/state"))).Code);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(Req(HttpMethod.Get, "/api/state", token: false))).Code);
        var wrong = Req(HttpMethod.Get, "/api/state", token: false);
        wrong.Headers.Add("X-Ferahla-Token", new string('0', 64));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(wrong)).Code);
        // a token in the query string is not accepted (it would end up in logs and history)
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(Req(HttpMethod.Get, "/api/state?token=" + _server.Token, token: false))).Code);
    }

    [Theory]
    [InlineData("evil.example")]
    [InlineData("evil.example:80")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1.nip.io")]
    public async Task Wrong_host_is_rejected_even_with_the_token(string host)
    {
        var r = Req(HttpMethod.Get, "/api/state");
        r.Headers.Host = host;
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(r)).Code);
        var page = Req(HttpMethod.Get, "/", token: false);
        page.Headers.Host = host;
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(page)).Code); // DNS rebinding can't even read the page
    }

    [Fact]
    public async Task Localhost_host_is_accepted()
    {
        var r = Req(HttpMethod.Get, "/api/ping");
        r.Headers.Host = $"localhost:{_server.Port}";
        Assert.Equal(HttpStatusCode.OK, (await Send(r)).Code);
    }

    [Theory]
    [InlineData("http://evil.example")]
    [InlineData("null")]
    [InlineData("http://127.0.0.1:1")]
    public async Task Foreign_origin_is_rejected(string origin)
    {
        var r = Req(HttpMethod.Post, "/api/scan", "{}");
        r.Headers.Add("Origin", origin);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(r)).Code);
        var sse = Req(HttpMethod.Get, "/api/events");
        sse.Headers.Add("Origin", origin);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(sse)).Code);
    }

    [Fact]
    public async Task Cross_site_fetch_metadata_is_rejected()
    {
        var r = Req(HttpMethod.Get, "/api/state");
        r.Headers.Add("Sec-Fetch-Site", "cross-site");
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(r)).Code);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("multipart/form-data")]
    public async Task Post_must_be_json(string contentType)
    {
        var (code, _) = await Send(Req(HttpMethod.Post, "/api/scan", "{}", contentType: contentType));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, code);
    }

    [Fact]
    public async Task Only_get_and_post()
    {
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await Send(Req(HttpMethod.Put, "/api/scan", "{}"))).Code);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await Send(Req(HttpMethod.Delete, "/api/scans/delete"))).Code);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await Send(Req(HttpMethod.Post, "/index.html", "{}"))).Code);
    }

    [Fact]
    public async Task Page_is_served_with_a_strict_csp()
    {
        using var res = await _http.SendAsync(Req(HttpMethod.Get, "/", token: false), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.StartsWith("text/html", res.Content.Headers.ContentType!.ToString());
        var csp = res.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("default-src 'none'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.DoesNotContain("unsafe", csp);
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task Permanent_delete_needs_explicit_confirmation()
    {
        var (code, _) = await Send(Req(HttpMethod.Post, "/api/delete", """{"batch":"20260101T000000Z-abcd"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, code);
    }

    [Fact]
    public async Task Events_stream_starts_with_hello()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        using var res = await _http.SendAsync(Req(HttpMethod.Get, "/api/events"), HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal("text/event-stream", res.Content.Headers.ContentType!.MediaType);
        using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(cts.Token));
        Assert.Equal("event: hello", await reader.ReadLineAsync(cts.Token));
        Assert.StartsWith("data: {", await reader.ReadLineAsync(cts.Token));
    }

    [Fact]
    public async Task Scan_quarantine_restore_and_delete_through_the_api()
    {
        var work = _t.Dir("work");
        foreach (var name in new[] { "one", "two" })
        {
            _t.File($"work\\{name}\\package.json", 2);
            _t.File($"work\\{name}\\node_modules\\pkg\\big.bin", 12L << 20);
        }

        var (code, _) = await Send(Req(HttpMethod.Post, "/api/scan", JsonSerializer.Serialize(new { root = work })));
        Assert.Equal(HttpStatusCode.Accepted, code);
        var job = await WaitForJob();
        Assert.Equal("done", job.GetProperty("state").GetString());
        var scanId = job.GetProperty("scan").GetProperty("id").GetString()!;

        var findings = JsonDocument.Parse((await Send(Req(HttpMethod.Get, "/api/findings?scan=" + scanId))).Body).RootElement;
        var nm = findings.GetProperty("findings").EnumerateArray().Where(f => f.GetProperty("ruleId").GetString() == "build.node").ToList();
        Assert.Equal(2, nm.Count);

        var plan = new
        {
            plan = new
            {
                schema = 1, kind = "ferahla-plan", scanId,
                findings = nm.Select(f => new { id = f.GetProperty("id").GetString(), items = f.GetProperty("items").EnumerateArray().Select(i => new { path = i.GetProperty("path").GetString() }).ToArray() }).ToArray(),
            },
            dryRun = false,
        };
        Assert.Equal(HttpStatusCode.Accepted, (await Send(Req(HttpMethod.Post, "/api/quarantine", JsonSerializer.Serialize(plan)))).Code);
        job = await WaitForJob();
        Assert.Equal(2, job.GetProperty("result").GetProperty("done").GetInt32());
        Assert.False(Directory.Exists(_t.P("work\\one\\node_modules")));

        // Findings now leave out what went to quarantine.
        findings = JsonDocument.Parse((await Send(Req(HttpMethod.Get, "/api/findings?scan=" + scanId))).Body).RootElement;
        Assert.DoesNotContain(findings.GetProperty("findings").EnumerateArray(), f => f.GetProperty("ruleId").GetString() == "build.node");

        var batches = JsonDocument.Parse((await Send(Req(HttpMethod.Get, "/api/quarantine"))).Body).RootElement;
        var batch = batches[0].GetProperty("batch").GetString()!;
        var entries = batches[0].GetProperty("entries").EnumerateArray().ToList();
        var oneKey = entries.Single(e => e.GetProperty("originalPath").GetString()!.EndsWith("one\\node_modules")).GetProperty("key").GetString();

        Assert.Equal(HttpStatusCode.Accepted, (await Send(Req(HttpMethod.Post, "/api/restore", JsonSerializer.Serialize(new { batch, keys = new[] { oneKey } })))).Code);
        await WaitForJob();
        Assert.True(Directory.Exists(_t.P("work\\one\\node_modules")));

        Assert.Equal(HttpStatusCode.Accepted, (await Send(Req(HttpMethod.Post, "/api/delete", JsonSerializer.Serialize(new { batch, confirm = "delete" })))).Code);
        job = await WaitForJob();
        Assert.Equal(1, job.GetProperty("result").GetProperty("done").GetInt32());
        Assert.False(Directory.Exists(_t.P("work\\two\\node_modules")));
        Assert.Equal("[]", (await Send(Req(HttpMethod.Get, "/api/quarantine"))).Body);
    }

    private async Task<JsonElement> WaitForJob()
    {
        for (var i = 0; i < 600; i++)
        {
            var state = JsonDocument.Parse((await Send(Req(HttpMethod.Get, "/api/state"))).Body).RootElement;
            if (state.TryGetProperty("job", out var job) && job.GetProperty("state").GetString() != "running" && job.TryGetProperty("finishedAt", out _))
                return job;
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException("job did not finish");
    }
}

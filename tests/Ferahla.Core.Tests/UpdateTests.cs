using System.Net;
using Ferahla.Server;

namespace Ferahla.Core.Tests;

/// <summary>Which GitHub releases count as an update (no network: the release objects are built here).</summary>
public sealed class UpdateTests
{
    private const string Repo = "https://github.com/" + Updates.Repo + "/releases/";

    private static GhRelease Release(string tag, string? repoPrefix = null, bool draft = false, bool pre = false, long size = 28_000_000)
    {
        var p = repoPrefix ?? Repo;
        return new GhRelease
        {
            TagName = tag, HtmlUrl = p + "tag/" + tag, Body = "Sample notes", Draft = draft, Prerelease = pre,
            Assets =
            [
                new GhAsset { Name = "ferahla.exe", BrowserDownloadUrl = p + "download/" + tag + "/ferahla.exe", Size = size },
                new GhAsset { Name = "ferahla.exe.sha256", BrowserDownloadUrl = p + "download/" + tag + "/ferahla.exe.sha256", Size = 79 },
            ],
        };
    }

    [Theory]
    [InlineData("v1.0.2", "1.0.1", "1.0.2")]
    [InlineData("v1.1.0", "1.0.9", "1.1.0")]
    [InlineData("v1.0.10", "1.0.9", "1.0.10")]
    [InlineData("v2.0.0", "1.9.9", "2.0.0")]
    public void Newer_release_is_offered(string tag, string current, string expected)
    {
        var u = Updates.Evaluate(Release(tag), current);
        Assert.NotNull(u);
        Assert.Equal(expected, u.Version);
        Assert.Equal(Repo + "download/" + tag + "/ferahla.exe", u.ExeUrl);
    }

    [Theory]
    [InlineData("v1.0.1", "1.0.1")]
    [InlineData("v1.0.0", "1.0.1")]
    [InlineData("1.0.2", "1.0.1")]
    [InlineData("v1.0.2-beta", "1.0.1")]
    [InlineData("v1.0", "0.9.0")]
    public void Same_older_or_malformed_tags_are_not_offered(string tag, string current) =>
        Assert.Null(Updates.Evaluate(Release(tag), current));

    [Fact]
    public void Drafts_and_prereleases_are_not_offered()
    {
        Assert.Null(Updates.Evaluate(Release("v9.0.0", draft: true), "1.0.0"));
        Assert.Null(Updates.Evaluate(Release("v9.0.0", pre: true), "1.0.0"));
    }

    [Fact]
    public void Files_from_another_repository_are_refused()
    {
        Assert.Null(Updates.Evaluate(Release("v9.0.0", "https://github.com/someone/ferahla/releases/"), "1.0.0"));
        Assert.Null(Updates.Evaluate(Release("v9.0.0", "http://github.com/" + Updates.Repo + "/releases/"), "1.0.0"));
        var r = Release("v9.0.0");
        r.Assets[0].BrowserDownloadUrl = "https://example.com/ferahla.exe";
        Assert.Null(Updates.Evaluate(r, "1.0.0"));
        var other = Release("v9.0.0");
        other.Assets[0].BrowserDownloadUrl = Repo + "download/v8.0.0/ferahla.exe"; // asset of another tag
        Assert.Null(Updates.Evaluate(other, "1.0.0"));
    }

    [Fact]
    public void Missing_checksum_or_absurd_size_is_refused()
    {
        var r = Release("v9.0.0");
        r.Assets.RemoveAt(1);
        Assert.Null(Updates.Evaluate(r, "1.0.0"));
        Assert.Null(Updates.Evaluate(Release("v9.0.0", size: 0), "1.0.0"));
        Assert.Null(Updates.Evaluate(Release("v9.0.0", size: 1L << 30), "1.0.0"));
    }

    [Fact]
    public void Release_json_from_github_parses()
    {
        const string json = """
            {"tag_name":"v1.2.3","html_url":"https://github.com/BerkayCaglar/ferahla/releases/tag/v1.2.3","draft":false,"prerelease":false,
             "published_at":"2026-01-02T03:04:05Z","body":"Sample notes\r\nline two",
             "assets":[{"name":"ferahla.exe","browser_download_url":"https://github.com/BerkayCaglar/ferahla/releases/download/v1.2.3/ferahla.exe","size":1234},
                       {"name":"ferahla.exe.sha256","browser_download_url":"https://github.com/BerkayCaglar/ferahla/releases/download/v1.2.3/ferahla.exe.sha256","size":79}]}
            """;
        var r = System.Text.Json.JsonSerializer.Deserialize(json, GitHubJson.Default.GhRelease)!;
        var u = Updates.Evaluate(r, "1.0.0");
        Assert.NotNull(u);
        Assert.Equal("1.2.3", u.Version);
        Assert.Equal("Sample notes\nline two", u.Notes);
        Assert.Equal(1234, u.Size);
    }

    [Fact]
    public void Switch_is_on_by_default_and_persists()
    {
        using var t = new TempTree(outsideTemp: true);
        var home = t.Dir("home");
        Assert.True(Updates.Load(home).CheckForUpdates);
        Updates.Save(home, new UpdateSettings { CheckForUpdates = false });
        Assert.False(Updates.Load(home).CheckForUpdates);
    }

    [Fact]
    public async Task Server_reports_updates_without_going_online_in_tests()
    {
        using var t = new TempTree(outsideTemp: true);
        await using var server = await FerahlaServer.StartAsync(new ServerOptions { Home = t.Dir("home"), IdleTimeout = null });
        using var http = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, server.BaseUrl + "/api/update/check") { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
        req.Headers.Add("X-Ferahla-Token", server.Token);
        using var res = await http.SendAsync(req, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("\"enabled\":true", body);
        Assert.DoesNotContain("\"available\"", body);
        Assert.DoesNotContain("\"checkedAt\"", body); // CheckUpdates is off: no request went out
        using var install = new HttpRequestMessage(HttpMethod.Post, server.BaseUrl + "/api/update/install") { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
        install.Headers.Add("X-Ferahla-Token", server.Token);
        using var ires = await http.SendAsync(install, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, ires.StatusCode);
    }
}

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Ferahla.Server;

/// <summary>What the UI shows about updates (part of <see cref="StateDto"/> and the <c>update</c> event).</summary>
public sealed class UpdateDto
{
    public bool Enabled { get; set; }
    public string Current { get; set; } = "";
    public UpdateInfo? Available { get; set; }
    public DateTimeOffset? CheckedAt { get; set; }
    /// <summary>False when this exe can't replace itself (run through <c>dotnet</c>, e.g. in tests).</summary>
    public bool CanInstall { get; set; }
}

public sealed class UpdateInfo
{
    public string Version { get; set; } = "";
    public string Url { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTimeOffset? PublishedAt { get; set; }
    public long Size { get; set; }
    internal string ExeUrl { get; set; } = "";
    internal string ShaUrl { get; set; } = "";
}

public sealed class UpdateSettingsRequest { public bool Enabled { get; set; } }

/// <summary><c>&lt;home&gt;\update.json</c>: the on/off switch, and the old exe to delete after an update.</summary>
internal sealed class UpdateSettings
{
    public bool CheckForUpdates { get; set; } = true;
    public string? Cleanup { get; set; }
}

/// <summary>The parts of GitHub's "latest release" answer that are used.</summary>
internal sealed class GhRelease
{
    public string TagName { get; set; } = "";
    public string HtmlUrl { get; set; } = "";
    public string? Body { get; set; }
    public bool Draft { get; set; }
    public bool Prerelease { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public List<GhAsset> Assets { get; set; } = [];
}

internal sealed class GhAsset
{
    public string Name { get; set; } = "";
    public string BrowserDownloadUrl { get; set; } = "";
    public long Size { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(GhRelease))]
internal sealed partial class GitHubJson : JsonSerializerContext;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(UpdateSettings))]
internal sealed partial class UpdateSettingsJson : JsonSerializerContext;

/// <summary>
/// Update check and self-update. The only network access Ferahla has: while a window is open (and the switch is on),
/// it asks GitHub for this repository's latest release, on start, every 15 minutes and when the window gets focus
/// (at most every 2 minutes). Nothing is sent except the request itself. Installing downloads <c>ferahla.exe</c> from
/// that release only, checks its SHA-256 and version, swaps it in next to the running exe (a running exe can be
/// renamed, not overwritten) and restarts on the same address, so the open tab only reloads.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class Updates
{
    public const string Repo = "BerkayCaglar/ferahla";
    private const string LatestUrl = $"https://api.github.com/repos/{Repo}/releases/latest";
    private const string PagePrefix = $"https://github.com/{Repo}/releases/";
    private const string DownloadPrefix = $"https://github.com/{Repo}/releases/download/";
    private const long MaxExe = 256L << 20;
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan OnFocus = TimeSpan.FromMinutes(2);

    private static readonly HttpClient Http = CreateHttp();

    private readonly string _home;
    private readonly EventHub _hub;
    private readonly string _current;
    private readonly SemaphoreSlim _checking = new(1, 1);
    private readonly Lock _lock = new();
    private UpdateInfo? _available;
    private DateTimeOffset? _checkedAt;
    private EntityTagHeaderValue? _etag;
    private GhRelease? _last;

    public Updates(string home, EventHub hub, string currentVersion)
    {
        _home = home;
        _hub = hub;
        _current = currentVersion;
    }

    /// <summary>Set once a new exe is in place; the server then stops and <c>UiCommand</c> starts the new one.</summary>
    public bool Installed { get; private set; }

    public UpdateDto State()
    {
        lock (_lock)
            return new UpdateDto { Enabled = Load().CheckForUpdates, Current = _current, Available = _available, CheckedAt = _checkedAt, CanInstall = CanInstall() };
    }

    public void SetEnabled(bool on)
    {
        var s = Load();
        s.CheckForUpdates = on;
        Save(s);
        if (!on) lock (_lock) _available = null;
        _hub.Publish("update", State(), ApiJson.Web.UpdateDto);
    }

    /// <summary>Checks while a tab is open: now, then every <see cref="Every"/>.</summary>
    public async Task Loop(Func<int> tabs, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            while (!ct.IsCancellationRequested)
            {
                if (tabs() > 0) await CheckAsync(Every, ct);
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Asks GitHub unless the last check is younger than <paramref name="minAge"/>. Never throws for network trouble.</summary>
    public async Task CheckAsync(TimeSpan minAge, CancellationToken ct = default)
    {
        if (!Load().CheckForUpdates) return;
        lock (_lock) if (_checkedAt is { } at && DateTimeOffset.UtcNow - at < minAge) return;
        if (!await _checking.WaitAsync(0, ct)) return;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            if (_etag is not null) req.Headers.IfNoneMatch.Add(_etag);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            GhRelease? rel;
            try
            {
                using var res = await Http.SendAsync(req, timeout.Token);
                if (res.StatusCode == HttpStatusCode.NotModified) rel = _last;
                else if (!res.IsSuccessStatusCode) rel = null; // 404: no release yet; 403: rate limit. Try again later.
                else
                {
                    rel = await JsonSerializer.DeserializeAsync(await res.Content.ReadAsStreamAsync(timeout.Token), GitHubJson.Default.GhRelease, timeout.Token);
                    _etag = res.Headers.ETag;
                    _last = rel;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                return; // offline or GitHub unreachable: say nothing, try again later
            }
            var found = rel is null ? null : Evaluate(rel, _current);
            bool changed;
            lock (_lock)
            {
                _checkedAt = DateTimeOffset.UtcNow;
                changed = found?.Version != _available?.Version;
                _available = found;
            }
            if (changed) _hub.Publish("update", State(), ApiJson.Web.UpdateDto);
        }
        finally { _checking.Release(); }
    }

    /// <summary>The update a release offers over <paramref name="current"/>, or null (older, malformed, or not from this repository).</summary>
    internal static UpdateInfo? Evaluate(GhRelease r, string current)
    {
        if (r.Draft || r.Prerelease) return null;
        var m = TagRegex().Match(r.TagName);
        if (!m.Success || !System.Version.TryParse(m.Groups[1].Value, out var v)) return null;
        if (!System.Version.TryParse(current, out var cur) || v <= cur) return null;
        if (!r.HtmlUrl.StartsWith(PagePrefix, StringComparison.Ordinal)) return null;
        var prefix = DownloadPrefix + r.TagName + "/";
        var exe = r.Assets.FirstOrDefault(a => a.Name == "ferahla.exe" && a.BrowserDownloadUrl == prefix + "ferahla.exe");
        var sha = r.Assets.FirstOrDefault(a => a.Name == "ferahla.exe.sha256" && a.BrowserDownloadUrl == prefix + "ferahla.exe.sha256");
        if (exe is null || sha is null || exe.Size <= 0 || exe.Size > MaxExe) return null;
        var notes = (r.Body ?? "").Replace("\r\n", "\n").Trim();
        if (notes.Length > 4000) notes = notes[..4000] + "…";
        return new UpdateInfo
        {
            Version = m.Groups[1].Value, Url = r.HtmlUrl, Notes = notes, PublishedAt = r.PublishedAt, Size = exe.Size,
            ExeUrl = exe.BrowserDownloadUrl, ShaUrl = sha.BrowserDownloadUrl,
        };
    }

    [GeneratedRegex(@"^v(\d{1,4}\.\d{1,4}\.\d{1,4})$")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"^\s*([0-9a-fA-F]{64})\b")]
    private static partial Regex ShaRegex();

    private static bool CanInstall()
    {
        var exe = Environment.ProcessPath;
        return exe is not null && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && !Path.GetFileName(exe).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Downloads, verifies and swaps in the new exe. Throws with a message for the UI if anything is off.</summary>
    public async Task InstallAsync(Action<long, long> progress, CancellationToken ct)
    {
        UpdateInfo u;
        lock (_lock) u = _available ?? throw new InvalidOperationException(Core.Text.Lang.T("No update available", "Güncelleme yok"));
        if (!CanInstall()) throw new InvalidOperationException("this build can't update itself");
        var exe = Environment.ProcessPath!;
        var dir = Path.GetDirectoryName(exe)!;
        var name = Path.GetFileNameWithoutExtension(exe);
        var fresh = Path.Combine(dir, name + ".new.exe");

        var shaText = await Http.GetStringAsync(u.ShaUrl, ct);
        var sm = ShaRegex().Match(shaText);
        if (!sm.Success) throw new InvalidDataException(Core.Text.Lang.T("The release has no valid checksum", "Sürümde geçerli bir sağlama değeri yok"));
        var expected = sm.Groups[1].Value.ToLowerInvariant();

        try
        {
            if (File.Exists(fresh)) File.Delete(fresh);
            using (var res = await Http.GetAsync(u.ExeUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                res.EnsureSuccessStatusCode();
                var total = res.Content.Headers.ContentLength ?? u.Size;
                if (total > MaxExe) throw new InvalidDataException("download too large");
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var src = await res.Content.ReadAsStreamAsync(ct))
                await using (var dst = new FileStream(fresh, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
                {
                    var buf = new byte[1 << 16];
                    long done = 0;
                    int n;
                    while ((n = await src.ReadAsync(buf, ct)) > 0)
                    {
                        done += n;
                        if (done > MaxExe) throw new InvalidDataException("download too large");
                        hash.AppendData(buf, 0, n);
                        await dst.WriteAsync(buf.AsMemory(0, n), ct);
                        progress(done, total);
                    }
                }
                var got = Convert.ToHexStringLower(hash.GetHashAndReset());
                if (got != expected) throw new InvalidDataException(Core.Text.Lang.T("The download doesn't match its checksum", "İndirilen dosya sağlama değeriyle eşleşmiyor"));
            }

            // The new exe must say it is the version we asked for.
            var psi = new ProcessStartInfo(fresh, "--version") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            using (var p = Process.Start(psi) ?? throw new InvalidDataException("can't run the download"))
            {
                var outText = await p.StandardOutput.ReadToEndAsync(ct).WaitAsync(TimeSpan.FromSeconds(20), ct);
                await p.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(20), ct);
                if (!outText.Trim().StartsWith(u.Version, StringComparison.Ordinal))
                    throw new InvalidDataException(Core.Text.Lang.T("The download reports a different version", "İndirilen dosya farklı bir sürüm bildiriyor"));
            }
        }
        catch
        {
            try { File.Delete(fresh); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }

        // Swap: rename the running exe aside, put the new one in its place; undo if the second step fails.
        var old = Path.Combine(dir, name + ".old.exe");
        try { if (File.Exists(old)) File.Delete(old); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { old = Path.Combine(dir, $"{name}.old-{DateTime.UtcNow.Ticks}.exe"); }
        File.Move(exe, old);
        try { File.Move(fresh, exe); }
        catch
        {
            File.Move(old, exe);
            throw;
        }
        var s = Load();
        s.Cleanup = old;
        Save(s);
        Installed = true;
    }

    /// <summary>After an update: deletes the previous exe once its process has exited (it may still be closing).</summary>
    public static async Task CleanupAsync(string home)
    {
        var s = Load(home);
        if (s.Cleanup is not { } old) return;
        var exe = Environment.ProcessPath;
        var ok = exe is not null && old.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(old).Contains(".old", StringComparison.Ordinal)
                 && string.Equals(Path.GetDirectoryName(old), Path.GetDirectoryName(exe), StringComparison.OrdinalIgnoreCase)
                 && !string.Equals(old, exe, StringComparison.OrdinalIgnoreCase);
        for (var i = 0; ok && i < 60 && File.Exists(old); i++)
        {
            try { File.Delete(old); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { await Task.Delay(500); }
        }
        s = Load(home);
        s.Cleanup = null;
        Save(home, s);
    }

    private UpdateSettings Load() => Load(_home);
    private void Save(UpdateSettings s) => Save(_home, s);

    internal static UpdateSettings Load(string home)
    {
        try { return JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(home, "update.json")), UpdateSettingsJson.Default.UpdateSettings) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    internal static void Save(string home, UpdateSettings s)
    {
        try
        {
            Directory.CreateDirectory(home);
            File.WriteAllBytes(Path.Combine(home, "update.json"), JsonSerializer.SerializeToUtf8Bytes(s, UpdateSettingsJson.Default.UpdateSettings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5, UseCookies = false };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        var v = typeof(Updates).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Ferahla", v)); // GitHub requires a User-Agent
        return http;
    }
}

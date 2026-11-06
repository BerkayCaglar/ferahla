using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;

namespace Ferahla.Server;

/// <summary>The UI files embedded in the exe (manifest resources <c>web/…</c>), served from memory with ETags.</summary>
internal sealed class StaticWeb
{
    private sealed record Asset(byte[] Body, string ContentType, string ETag);

    private readonly Dictionary<string, Asset> _assets = new(StringComparer.OrdinalIgnoreCase);

    public StaticWeb()
    {
        var asm = typeof(StaticWeb).Assembly;
        foreach (var name in asm.GetManifestResourceNames())
        {
            var key = name.Replace('\\', '/');
            if (!key.StartsWith("web/", StringComparison.Ordinal)) continue;
            using var s = asm.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var body = ms.ToArray();
            var etag = "\"" + Convert.ToHexStringLower(SHA256.HashData(body).AsSpan(0, 8)) + "\"";
            _assets["/" + key[4..]] = new Asset(body, ContentType(key), etag);
        }
    }

    public IEnumerable<string> Paths => _assets.Keys;

    public async Task<bool> TryServe(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "/";
        if (path == "/") path = "/index.html";
        if (!_assets.TryGetValue(path, out var a)) return false;
        var res = ctx.Response;
        res.Headers.ETag = a.ETag;
        res.Headers.CacheControl = "no-cache";
        if (ctx.Request.Headers.IfNoneMatch == a.ETag) { res.StatusCode = 304; return true; }
        res.ContentType = a.ContentType;
        res.ContentLength = a.Body.Length;
        if (!HttpMethods.IsHead(ctx.Request.Method)) await res.Body.WriteAsync(a.Body);
        return true;
    }

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml; charset=utf-8",
        ".png" => "image/png",
        ".ico" => "image/x-icon",
        ".woff2" => "font/woff2",
        ".webmanifest" => "application/manifest+json; charset=utf-8",
        _ => "application/octet-stream",
    };
}

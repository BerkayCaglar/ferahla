using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Ferahla.Server;

/// <summary>
/// Any website open in the browser can send requests to a local server, and binding to 127.0.0.1 alone doesn't stop
/// DNS rebinding (see Ollama CVE-2024-28224, MCP Inspector CVE-2025-49596). Every request passes these checks:
/// <list type="bullet">
/// <item>Host must be <c>127.0.0.1:port</c> or <c>localhost:port</c> (defeats DNS rebinding).</item>
/// <item>An Origin header, if present, must be this server (defeats cross-site requests, SSE included).</item>
/// <item><c>Sec-Fetch-Site</c>, if present, must be same-origin or none.</item>
/// <item>Every <c>/api/</c> call carries the per-launch random token in <c>X-Ferahla-Token</c>.</item>
/// <item>Only GET and POST; GET never changes state; POST bodies must be <c>application/json</c>
/// (a cross-site form or a <c>text/plain</c> "simple request" can't reach a handler).</item>
/// </list>
/// </summary>
internal sealed class Security(string token, Func<int> port)
{
    public const string TokenHeader = "X-Ferahla-Token";
    private readonly byte[] _token = Encoding.ASCII.GetBytes(token);

    public static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    public async Task Invoke(HttpContext ctx, RequestDelegate next)
    {
        var h = ctx.Response.Headers;
        h["X-Content-Type-Options"] = "nosniff";
        h["Referrer-Policy"] = "no-referrer";
        h["X-Frame-Options"] = "DENY";
        h["Cross-Origin-Opener-Policy"] = "same-origin";
        h["Cross-Origin-Resource-Policy"] = "same-origin";
        h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), interest-cohort=()";
        h["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; " +
                                       "connect-src 'self'; manifest-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

        var reason = Reject(ctx.Request);
        if (reason is not null)
        {
            ctx.Response.StatusCode = reason.Value.Status;
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            await ctx.Response.WriteAsync(reason.Value.Text);
            return;
        }
        if (ctx.Request.Path.StartsWithSegments("/api")) h.CacheControl = "no-store";
        await next(ctx);
    }

    internal (int Status, string Text)? Reject(HttpRequest r)
    {
        var p = port();
        var host = r.Headers.Host.ToString();
        if (host != $"127.0.0.1:{p}" && host != $"localhost:{p}") return (403, "bad host");

        var origin = r.Headers.Origin.ToString();
        if (origin.Length > 0 && origin != $"http://127.0.0.1:{p}" && origin != $"http://localhost:{p}") return (403, "bad origin");
        var site = r.Headers["Sec-Fetch-Site"].ToString();
        if (site.Length > 0 && site is not ("same-origin" or "none")) return (403, "cross-site request");

        var method = r.Method;
        var isGet = HttpMethods.IsGet(method) || HttpMethods.IsHead(method);
        if (!isGet && !HttpMethods.IsPost(method)) return (405, "method not allowed");

        if (!r.Path.StartsWithSegments("/api"))
            return isGet ? null : (405, "method not allowed");

        if (!TokenOk(r.Headers[TokenHeader].ToString())) return (401, "missing or wrong token");
        if (!isGet)
        {
            var ct = r.ContentType ?? "";
            var semi = ct.IndexOf(';');
            var media = (semi >= 0 ? ct[..semi] : ct).Trim();
            if (!media.Equals("application/json", StringComparison.OrdinalIgnoreCase)) return (415, "application/json only");
        }
        return null;
    }

    private bool TokenOk(string given)
    {
        var g = Encoding.ASCII.GetBytes(given);
        return g.Length == _token.Length && CryptographicOperations.FixedTimeEquals(g, _token);
    }
}

using Microsoft.AspNetCore.Http;

namespace PiWebui.Web;

/// <summary>
/// Token-parsing helpers shared between the middleware and its boundary tests.
/// A token may be presented (in priority order): as a <c>Authorization: Bearer</c>
/// header, an <c>X-Auth-Token</c> header, a <c>?token=</c> query parameter, or a
/// <c>token</c> cookie (set after the first authenticated query request so that
/// static assets and the WebSocket handshake stay authenticated without repeating
/// the token in every URL).
/// </summary>
public static class TokenAuth
{
    public const string CookieName = "token";

    public static string? ExtractFrom(HttpContext ctx)
    {
        var auth = ctx.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(auth) &&
            auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var t = auth.Substring("Bearer ".Length).Trim();
            if (t.Length > 0) return t;
        }

        var header = ctx.Request.Headers["X-Auth-Token"].ToString();
        if (!string.IsNullOrEmpty(header)) return header.Trim();

        var query = ctx.Request.Query["token"].ToString();
        if (!string.IsNullOrEmpty(query)) return query.Trim();

        var cookie = ctx.Request.Cookies[CookieName];
        if (!string.IsNullOrEmpty(cookie)) return cookie.Trim();

        return null;
    }
}

/// <summary>
/// ASP.NET middleware enforcing the config token on every HTTP request and
/// WebSocket handshake. Missing or mismatched tokens are rejected with 401 before
/// any downstream handler runs. On the first authenticated <c>?token=</c> request
/// it drops a same-origin cookie so later static / WS requests (same origin)
/// carry the token automatically.
/// </summary>
public sealed class TokenAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _token;

    public TokenAuthMiddleware(RequestDelegate next, string token)
    {
        _next = next;
        _token = token;
    }

    public Task InvokeAsync(HttpContext ctx)
    {
        // Every verb — including OPTIONS (no CORS requirement in the design) — is
        // token-gated.
        var presented = TokenAuth.ExtractFrom(ctx);
        if (string.IsNullOrEmpty(presented) || !string.Equals(presented, _token, StringComparison.Ordinal))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }

        // First authenticated hit typically arrives as ?token= on the index page.
        // Persist it as a same-origin cookie so ../app.css, main.js and the WS
        // handshake (which all lack the query param) remain authenticated.
        if (ctx.Request.Query.ContainsKey("token"))
        {
            ctx.Response.Cookies.Append(TokenAuth.CookieName, _token, new CookieOptions
            {
                SameSite = SameSiteMode.Lax,
                HttpOnly = false, // readable from JS so the frontend can reuse it on the WS URL
            });
        }

        return _next(ctx);
    }
}

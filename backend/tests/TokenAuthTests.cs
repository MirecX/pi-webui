using Microsoft.AspNetCore.Http;
using PiWebui;
using PiWebui.Web;
using Xunit;

namespace PiWebui.Tests;

/// <summary>
/// Seam — the HTTP/WS auth boundary. Verifies the token gate: every request
/// (HTTP endpoint, static asset, and the WebSocket handshake) requires the config
/// token; missing/invalid is rejected with 401, valid is accepted.
/// </summary>
public class TokenAuthTests
{
    private static Task Next(HttpContext _) => Task.CompletedTask;

    private static DefaultHttpContext Ctx(
        string method = "GET",
        string path = "/ws",
        string? query = null,
        string? bearer = null,
        string? header = null,
        string? cookie = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        if (query is not null) ctx.Request.QueryString = new QueryString($"?token={Uri.EscapeDataString(query)}");
        if (bearer is not null) ctx.Request.Headers.Authorization = $"Bearer {bearer}";
        if (header is not null) ctx.Request.Headers["X-Auth-Token"] = header;
        if (cookie is not null) ctx.Request.Headers.Cookie = $"{TokenAuth.CookieName}={cookie}";
        return ctx;
    }

    private static TokenAuthMiddleware Mw(string token = "secret") => new(Next, token);

    [Fact]
    public async Task Missing_token_rejected_on_http()
    {
        var ctx = Ctx(path: "/");
        await Mw().InvokeAsync(ctx);
        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Wrong_token_rejected_on_http()
    {
        var ctx = Ctx(query: "nope");
        await Mw().InvokeAsync(ctx);
        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Valid_token_allowed_on_http()
    {
        var ctx = Ctx(query: "secret");
        await Mw().InvokeAsync(ctx);
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Missing_token_rejected_on_ws_handshake()
    {
        // WS upgrade request without any token present
        var ctx = Ctx(path: "/ws");
        await Mw().InvokeAsync(ctx);
        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Wrong_token_rejected_on_ws_handshake()
    {
        var ctx = Ctx(path: "/ws", query: "wrong");
        await Mw().InvokeAsync(ctx);
        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Valid_token_accepted_on_ws_handshake()
    {
        var ctx = Ctx(path: "/ws", query: "secret");
        await Mw().InvokeAsync(ctx);
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Valid_bearer_and_header_and_cookie_all_accepted()
    {
        var bearer = Ctx(path: "/", bearer: "secret");
        await Mw().InvokeAsync(bearer);
        Assert.Equal(StatusCodes.Status200OK, bearer.Response.StatusCode);

        var header = Ctx(path: "/", header: "secret");
        await Mw().InvokeAsync(header);
        Assert.Equal(StatusCodes.Status200OK, header.Response.StatusCode);

        var cookie = Ctx(path: "/", cookie: "secret");
        await Mw().InvokeAsync(cookie);
        Assert.Equal(StatusCodes.Status200OK, cookie.Response.StatusCode);
    }

    [Fact]
    public async Task Static_and_index_requests_are_token_gated()
    {
        Assert.Equal(StatusCodes.Status401Unauthorized,
            StatusOf(await InvokeStatusOnly(Ctx(path: "/"))));
        Assert.Equal(StatusCodes.Status401Unauthorized,
            StatusOf(await InvokeStatusOnly(Ctx(path: "/app.css"))));
        Assert.Equal(StatusCodes.Status200OK,
            StatusOf(await InvokeStatusOnly(Ctx(path: "/app.css", query: "secret"))));
    }

    [Fact]
    public async Task Query_authentication_sets_token_cookie_for_later_requests()
    {
        var ctx = Ctx(path: "/", query: "secret");
        await Mw().InvokeAsync(ctx);
        var setCookie = ctx.Response.Headers.SetCookie.ToString();
        Assert.Contains("token=secret", setCookie);
        Assert.Contains("samesite=lax", setCookie);
    }

    [Fact]
    public async Task Options_preflight_is_also_token_gated()
    {
        // No CORS requirement in the design; every verb — including OPTIONS — needs the token.
        var noToken = Ctx(method: "OPTIONS", path: "/ws");
        await Mw().InvokeAsync(noToken);
        Assert.Equal(StatusCodes.Status401Unauthorized, noToken.Response.StatusCode);

        var withToken = Ctx(method: "OPTIONS", path: "/ws", query: "secret");
        await Mw().InvokeAsync(withToken);
        Assert.Equal(StatusCodes.Status200OK, withToken.Response.StatusCode);
    }

    [Fact]
    public async Task Rotating_token_in_config_is_effective_on_restart()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"piwebui-rot-{Guid.NewGuid():N}");
        var path = Path.Combine(dir, "config.json");
        try
        {
            // first run: config written with token A
            var first = Config.Load(path);

            // restart: fresh load from a reloaded config file that now holds token B
            File.WriteAllText(path, $"{{\"token\":\"rotated\",\"port\":{Config.DefaultPort}}}");
            var second = Config.Load(path);
            var newMw = Mw(second.Token);

            Assert.NotEqual(first.Token, second.Token);
            Assert.Equal("rotated", second.Token);

            // the stale token no longer authenticates against the reloaded middleware
            var stale = Ctx(path: "/", query: first.Token);
            await newMw.InvokeAsync(stale);
            Assert.Equal(StatusCodes.Status401Unauthorized, stale.Response.StatusCode);

            // the rotated token does
            var fresh = Ctx(path: "/", query: second.Token);
            await newMw.InvokeAsync(fresh);
            Assert.Equal(StatusCodes.Status200OK, fresh.Response.StatusCode);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    // helpers -----------------------------------------------------------------

    private static async Task<DefaultHttpContext> InvokeStatusOnly(DefaultHttpContext ctx)
    {
        await Mw().InvokeAsync(ctx);
        return ctx;
    }

    private static int StatusOf(DefaultHttpContext ctx) => ctx.Response.StatusCode;
}

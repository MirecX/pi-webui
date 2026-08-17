using System.Net.WebSockets;
using System.Text.Json;
using PiWebui;
using PiWebui.Rpc;
using PiWebui.Session;
using PiWebui.Web;

// --- Configuration ---------------------------------------------------------
var explicitConfig = Environment.GetEnvironmentVariable("PIE_CONFIG");
var config = Config.Load(explicitConfig);
Console.WriteLine($"[pi-webui] listening on port {config.Port}");

// --- Session registry: N named sessions (ticket #05) -----------------------
// Each session owns its own pi --mode rpc child; sessions are created on demand
// via init (never implicitly on connect). The client factory is invoked once per
// (re)init so each session gets its own isolated child.
var cwd = Environment.GetEnvironmentVariable("PIE_CWD")
          ?? (Directory.Exists("/workspace") ? "/workspace" : Directory.GetCurrentDirectory());
var piOptions = new PiClientOptions
{
    Executable = Environment.GetEnvironmentVariable("PIE_PI") ?? "pi",
    Arguments = new[] { "--mode", "rpc" },
    WorkingDirectory = cwd,
};
var sessionsDir = Environment.GetEnvironmentVariable("PIE_SESSIONS_DIR");
await using var sessions = new SessionManager(() => new PiRpcClient(piOptions), sessionsDir);
Console.WriteLine($"[pi-webui] session registry ready (cwd: {cwd}, sessions-dir: {sessionsDir ?? "<default>"})");

// --- ASP.NET host ----------------------------------------------------------
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://{config.BindHost}:{config.Port}");
var app = builder.Build();

// Gate every HTTP request and WS handshake behind the config token (ticket #02).
app.UseMiddleware<TokenAuthMiddleware>(config.Token);

// Lifecycle + listing (ticket #05). Actions are on-demand only;
// init is the only way a session is created.
object ToDto(PiWebui.Session.Session s) => new { name = s.Name, status = s.Status.ToString().ToLowerInvariant() };

app.MapGet("/api/sessions", () => Results.Json(sessions.List().Select(ToDto)));

app.MapMethods("/api/sessions", new[] { HttpMethods.Post }, async (HttpContext ctx) =>
{
    using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
    if (!doc.RootElement.TryGetProperty("name", out var nameProp)
        || string.IsNullOrWhiteSpace(nameProp.GetString()))
        return Results.BadRequest(new { error = "name is required" });
    var s = await sessions.InitAsync(nameProp.GetString()!, ctx.RequestAborted);
    return Results.Json(ToDto(s));
});

app.MapMethods("/api/sessions/{name}/recycle", new[] { HttpMethods.Post }, async (HttpContext ctx, string name) =>
{
    await sessions.RecycleAsync(name, ctx.RequestAborted);
    var s = sessions.Get(name);
    return Results.Json(s is null ? (object)new { name, status = "deleted" } : ToDto(s));
});

app.MapMethods("/api/sessions/{name}", new[] { HttpMethods.Delete }, async (HttpContext ctx, string name) =>
{
    await sessions.DeleteAsync(name, ctx.RequestAborted);
    return Results.Ok();
});

app.UseWebSockets();
app.Map("/ws", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }
    var socket = await ctx.WebSockets.AcceptWebSocketAsync();
    // A tab attaches to ONE named session's stream; default name when unspecified.
    var name = ctx.Request.Query["session"].FirstOrDefault()
               ?? PiWebui.Session.SessionManager.DefaultSessionName;
    var bridge = new WsBridge(sessions, name, new AspNetWsClient(socket));
    await bridge.RunAsync(ctx.RequestAborted);
});

// --- Static frontend -------------------------------------------------------
var webroot = Frontend.ResolveWebroot(Environment.GetEnvironmentVariable("PIE_WEBROOT"));
if (webroot is null)
    Console.WriteLine("[pi-webui] WARNING: web/dist not found; build the frontend (make build) before serving.");
else
{
    var provider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(webroot);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = provider });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = provider });
}

// The full token is echoed only on first-run generation (see Config.Load); it is
// intentionally not re-printed on every startup.
Console.WriteLine($"[pi-webui] up.  ws=ws://localhost:{config.Port}/ws  ui=http://localhost:{config.Port}/  bind={config.BindHost}  external-opt-in={config.External}");
await app.RunAsync();

/// Helper for resolving the built-frontend directory.
internal static class Frontend
{
    public static string? ResolveWebroot(string? env)
    {
        if (env is not null && Directory.Exists(env)) return Path.GetFullPath(env);
        var cwd = Directory.GetCurrentDirectory();
        foreach (var cand in new[] { Path.Combine(cwd, "web", "dist"), Path.Combine(cwd, "..", "web", "dist") })
            if (Directory.Exists(cand)) return Path.GetFullPath(cand);
        return null;
    }
}

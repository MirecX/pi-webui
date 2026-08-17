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

// --- Auto-title generator (ticket #06) --------------------------------------
// Short, non-blocking title from a new session's first user message, at the box's default
// model endpoint (resolved from ~/.pi/agent/models.json "work" provider; documented default
// fallback). Wired into the WS bridge; applies the truncated-message fallback on any failure.
var autoTitler = new SessionAutoTitler(new TitleGenerator());

// --- ASP.NET host ----------------------------------------------------------
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://{config.BindHost}:{config.Port}");
var app = builder.Build();

// Gate every HTTP request and WS handshake behind the config token (ticket #02).
app.UseMiddleware<TokenAuthMiddleware>(config.Token);

// Lifecycle + listing (tickets #05/#06). Actions are on-demand only; init is the only way
// a session is created. The listing includes STORED (not-loaded) sessions so the browser
// can show resume-able past work (ticket #06).

// e.g. { name, status (running|recycled|stored), title (may be null) }
app.MapGet("/api/sessions", () => Results.Json(
    sessions.ListStoredSessions().Select(s => new { name = s.Name, status = s.Status, title = s.Title })));

app.MapMethods("/api/sessions", new[] { HttpMethods.Post }, async (HttpContext ctx) =>
{
    using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
    if (!doc.RootElement.TryGetProperty("name", out var nameProp)
        || string.IsNullOrWhiteSpace(nameProp.GetString()))
        return Results.BadRequest(new { error = "name is required" });
    var s = await sessions.InitAsync(nameProp.GetString()!, ctx.RequestAborted);
    // init/resume: reflect the (possibly stored) session's current status + title
    return Results.Json(new { name = s.Name, status = s.Status.ToString().ToLowerInvariant(), title = s.Title });
});

app.MapMethods("/api/sessions/{name}/recycle", new[] { HttpMethods.Post }, async (HttpContext ctx, string name) =>
{
    await sessions.RecycleAsync(name, ctx.RequestAborted);
    var s = sessions.Get(name);
    return Results.Json(s is null
        ? (object)new { name, status = "deleted" }
        : new { name, status = s.Status.ToString().ToLowerInvariant(), title = s.Title });
});

// --- fork / clone (ticket #06) — operate on the attached session's child, per rpc.md ----

// POST /api/sessions/{name}/fork  { "entryId": "..." } -> { text: <forking message> }
app.MapMethods("/api/sessions/{name}/fork", new[] { HttpMethods.Post }, async (HttpContext ctx, string name) =>
{
    var s = sessions.Get(name);
    if (s is null || !s.IsRunning)
        return Results.BadRequest(new { error = "session not running" });
    using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
    if (!doc.RootElement.TryGetProperty("entryId", out var eid)
        || eid.GetString() is not { Length: > 0 } entryId)
        return Results.BadRequest(new { error = "entryId is required" });
    var resp = await s.ForkAsync(entryId, ctx.RequestAborted);
    return Results.Json(new { text = ExtractDataString(resp, "text") });
});

// POST /api/sessions/{name}/clone -> { success: bool, session: <new stored name> }
// Clone duplicates the active branch into a NEW session file; the manager registers that
// file as a stored session so GET /api/sessions lists it and it can be resumed (ticket #06).
app.MapMethods("/api/sessions/{name}/clone", new[] { HttpMethods.Post }, async (HttpContext ctx, string name) =>
{
    try
    {
        var newName = await sessions.CloneAndRegisterAsync(name, ctx.RequestAborted);
        if (newName is null)
            return Results.BadRequest(new { error = "clone rejected or new session not located" });
        return Results.Json(new { success = true, session = newName });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// GET /api/sessions/{name}/export — download the most recently exported HTML transcript
// for the named session (ticket #08). The file path was registered when export_html ran;
// the token middleware gates this like every other HTTP route. Returns 404 until an export
// exists for the session. The exported file lives on the box (owned by the pi child).
app.MapGet("/api/sessions/{name}/export", (string name) =>
{
    var path = sessions.GetExportPath(name);
    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        return Results.NotFound(new { error = "no exported transcript available for this session" });
    try
    {
        return Results.File(path, "text/html", fileDownloadName: $"{name}-transcript.html");
    }
    catch
    {
        return Results.BadRequest(new { error = "exported transcript could not be read" });
    }
});

static string? ExtractDataString(PiWebui.Rpc.RpcResponse? resp, string prop)
{
    if (resp is null || !resp.Success || resp.Data is not { } d) return null;
    if (d.TryGetProperty(prop, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
        return v.GetString();
    return null;
}

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
    var bridge = new WsBridge(sessions, name, new AspNetWsClient(socket), autoTitler);
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

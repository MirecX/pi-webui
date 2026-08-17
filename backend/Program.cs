using System.Net.WebSockets;
using PiWebui;
using PiWebui.Rpc;
using PiWebui.Session;
using PiWebui.Web;

// --- Configuration ---------------------------------------------------------
var explicitConfig = Environment.GetEnvironmentVariable("PIE_CONFIG");
var config = Config.Load(explicitConfig);
Console.WriteLine($"[pi-webui] listening on port {config.Port}");

// --- Session: ONE default session for ticket #01 ---------------------------
var cwd = Environment.GetEnvironmentVariable("PIE_CWD")
          ?? (Directory.Exists("/workspace") ? "/workspace" : Directory.GetCurrentDirectory());
var piOptions = new PiClientOptions
{
    Executable = Environment.GetEnvironmentVariable("PIE_PI") ?? "pi",
    Arguments = new[] { "--mode", "rpc" },
    WorkingDirectory = cwd,
};
await using var session = new SessionManager(new PiRpcClient(piOptions));
session.Start();
Console.WriteLine($"[pi-webui] spawned pi --mode rpc (cwd: {cwd})");

// --- ASP.NET host ----------------------------------------------------------
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://{config.BindHost}:{config.Port}");
var app = builder.Build();

// Gate every HTTP request and WS handshake behind the config token (ticket #02).
app.UseMiddleware<TokenAuthMiddleware>(config.Token);

app.UseWebSockets();
app.Map("/ws", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }
    var socket = await ctx.WebSockets.AcceptWebSocketAsync();
    var bridge = new WsBridge(session, new AspNetWsClient(socket));
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

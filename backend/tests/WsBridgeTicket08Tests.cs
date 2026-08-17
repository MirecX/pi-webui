using System.Text.Json;
using System.Threading.Channels;
using PiWebui.Rpc;
using PiWebui.Session;
using PiWebui.Web;
using Xunit;

namespace PiWebui.Tests;

/// <summary>
/// Seam 2 — the WebSocket API boundary for ticket #08 (compaction, auto-retry, state,
/// stats/structure, export), backed by the fake pi. Verifies the WS layer routes
/// <c>compact</c>/<c>set_auto_compaction</c>/<c>set_auto_retry</c>/<c>stats</c>/
/// <c>structure</c>/<c>export_html</c> browser frames to the ATTACHED session's child
/// with the right RPC command + params, relays the results back as <c>result</c> frames,
/// keeps per-session isolation, and registers the exported path for download.
/// </summary>
public class WsBridgeTicket08Tests
{
    /// <summary>
    /// A scripted fake child that answers the ticket #08 commands like real pi:
    /// compact returns a summary, set_auto_compaction/set_auto_retry ack, stats and
    /// entries return data, export_html returns a generated path. Wire names per rpc.md.
    /// </summary>
    private static FakePiRpcClient Ticket08Client()
    {
        return new FakePiRpcClient(cmd => cmd switch
        {
            CompactCommand => new RpcResponse("c1", "compact", true, null,
                JsonDocument.Parse("{\"summary\":\"compacted\",\"estimatedTokensAfter\":100}").RootElement),
            SetAutoCompactionCommand => new RpcResponse("ac", "set_auto_compaction", true, null, null),
            SetAutoRetryCommand => new RpcResponse("ar", "set_auto_retry", true, null, null),
            GetSessionStatsCommand => new RpcResponse("st", "get_session_stats", true, null,
                JsonDocument.Parse(
                    "{\"totalMessages\":22,\"tokens\":{\"total\":105000},\"cost\":0.45,\"contextUsage\":{\"percent\":30}}")
                    .RootElement),
            ExportHtmlCommand => new RpcResponse("ex", "export_html", true, null,
                JsonDocument.Parse("{\"path\":\"/tmp/session.html\"}").RootElement),
            GetEntriesCommand => new RpcResponse("en", "get_entries", true, null,
                JsonDocument.Parse(
                    "{\"entries\":[{\"type\":\"message\",\"id\":\"e1\",\"parentId\":null,\"message\":{\"role\":\"user\",\"content\":\"hi\"}}],\"leafId\":\"e1\"}")
                    .RootElement),
            _ => null,
        });
    }

    private static async Task<(SessionManager mgr, FakePiRpcClient client, FakeWsClient ws, WsBridge bridge, string dir)> SetupAsync(
        FakePiRpcClient? client = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"piwebui08-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var c = client ?? Ticket08Client();
        var mgr = new SessionManager(() => c, dir);
        await mgr.InitAsync("a");
        var ws = new FakeWsClient();
        var bridge = new WsBridge(mgr, "a", ws);
        return (mgr, c, ws, bridge, dir);
    }

    private static async Task CleanupAsync(SessionManager? mgr, FakePiRpcClient client, string dir)
    {
        if (mgr is not null)
        {
            try { await mgr.DisposeAsync(); } catch { /* best-effort */ }
        }
        await client.DisposeAsync();
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task Compact_frame_sends_compact_to_attached_child_and_relays_summary()
    {
        var (_, client, ws, bridge, dir) = await SetupAsync();
        try
        {
            await bridge.HandleMessageAsync("{\"type\":\"compact\"}");

            Assert.Single(client.Sent.OfType<CompactCommand>());
            var frame = Assert.Single(ws.Sent);
            Assert.Contains("\"type\":\"result\"", frame);
            Assert.Contains("\"target\":\"compact\"", frame);
            Assert.Contains("\"summary\":\"compacted\"", frame);
        }
        finally { await CleanupAsync(default!, client, dir); }
    }

    [Fact]
    public async Task Set_auto_compaction_frame_forwards_enabled_flag_to_attached_child()
    {
        var (_, client, ws, bridge, dir) = await SetupAsync();
        try
        {
            await bridge.HandleMessageAsync("{\"type\":\"set_auto_compaction\",\"enabled\":true}");
            var on = Assert.Single(client.Sent.OfType<SetAutoCompactionCommand>());
            Assert.True(on.Enabled);
            // wire payload matches rpc.md exactly (param `enabled`, no invented names)
            Assert.Equal("{\"type\":\"set_auto_compaction\",\"enabled\":true}", on.ToJson());

            client.Sent.Clear();
            await bridge.HandleMessageAsync("{\"type\":\"set_auto_compaction\",\"enabled\":false}");
            var off = Assert.Single(client.Sent.OfType<SetAutoCompactionCommand>());
            Assert.False(off.Enabled);
            Assert.Equal("{\"type\":\"set_auto_compaction\",\"enabled\":false}", off.ToJson());
        }
        finally { await CleanupAsync(default!, client, dir); }
    }

    [Fact]
    public async Task Set_auto_retry_frame_forwards_enabled_flag_to_attached_child()
    {
        var (_, client, ws, bridge, dir) = await SetupAsync();
        try
        {
            await bridge.HandleMessageAsync("{\"type\":\"set_auto_retry\",\"enabled\":true}");
            var cmd = Assert.Single(client.Sent.OfType<SetAutoRetryCommand>());
            Assert.True(cmd.Enabled);
            Assert.Equal("{\"type\":\"set_auto_retry\",\"enabled\":true}", cmd.ToJson());

            client.Sent.Clear();
            await bridge.HandleMessageAsync("{\"type\":\"set_auto_retry\",\"enabled\":false}");
            Assert.False(Assert.Single(client.Sent.OfType<SetAutoRetryCommand>()).Enabled);
        }
        finally { await CleanupAsync(default!, client, dir); }
    }

    [Fact]
    public async Task Stats_frame_requests_session_stats_and_relays_to_browser()
    {
        var (_, client, ws, bridge, dir) = await SetupAsync();
        try
        {
            await bridge.HandleMessageAsync("{\"type\":\"stats\"}");

            Assert.Single(client.Sent.OfType<GetSessionStatsCommand>());
            var frame = Assert.Single(ws.Sent);
            Assert.Contains("\"type\":\"result\"", frame);
            Assert.Contains("\"target\":\"stats\"", frame);
            Assert.Contains("\"totalMessages\":22", frame);
            Assert.Contains("\"cost\":0.45", frame);
        }
        finally { await CleanupAsync(default!, client, dir); }
    }

    [Fact]
    public async Task Structure_frame_requests_entries_and_relays_to_browser()
    {
        var (_, client, ws, bridge, dir) = await SetupAsync();
        try
        {
            await bridge.HandleMessageAsync("{\"type\":\"structure\"}");

            Assert.Single(client.Sent.OfType<GetEntriesCommand>());
            var frame = Assert.Single(ws.Sent);
            Assert.Contains("\"type\":\"result\"", frame);
            Assert.Contains("\"target\":\"structure\"", frame);
            Assert.Contains("\"entries\"", frame);
            Assert.Contains("\"e1\"", frame);
        }
        finally { await CleanupAsync(default!, client, dir); }
    }

    [Fact]
    public async Task State_frame_reuses_get_state_and_relays_to_browser()
    {
        var stateClient = new FakePiRpcClient(cmd =>
            cmd is GetStateCommand
                ? new RpcResponse("gs", "get_state", true, null,
                    JsonDocument.Parse(
                        "{\"model\":{\"id\":\"m1\"},\"autoCompactionEnabled\":true,\"messageCount\":5}").RootElement)
                : null);
        var (mgr, client, ws, bridge, dir) = await SetupAsync(stateClient);
        try
        {
            // get_state is sent once by InitAsync for session-file discovery.
            Assert.Single(client.Sent.OfType<GetStateCommand>());
            await bridge.HandleMessageAsync("{\"type\":\"state\"}");

            // the `state` frame reissues get_state on the attached session's child
            Assert.Equal(2, client.Sent.OfType<GetStateCommand>().Count());
            var frame = Assert.Single(ws.Sent);
            Assert.Contains("\"type\":\"result\"", frame);
            Assert.Contains("\"target\":\"state\"", frame);
            Assert.Contains("\"autoCompactionEnabled\":true", frame);
        }
        finally { await CleanupAsync(mgr, client, dir); }
    }

    [Fact]
    public async Task Export_frame_sends_export_html_relays_path_and_registers_download()
    {
        var (mgr, client, ws, bridge, dir) = await SetupAsync();
        try
        {
            // no outputPath -> pi generates its own; the returned path is relayed + registered
            await bridge.HandleMessageAsync("{\"type\":\"export_html\"}");

            var cmd = Assert.Single(client.Sent.OfType<ExportHtmlCommand>());
            Assert.Null(cmd.OutputPath); // no invented param when the browser sends none
            var frame = Assert.Single(ws.Sent);
            Assert.Contains("\"type\":\"result\"", frame);
            Assert.Contains("\"target\":\"export_html\"", frame);
            Assert.Contains("\"/tmp/session.html\"", frame);
            // the path is registered so GET /api/sessions/a/export can serve it
            Assert.Equal("/tmp/session.html", mgr.GetExportPath("a"));

            // a browser-supplied outputPath is forwarded verbatim (rpc.md `outputPath`)
            client.Sent.Clear();
            ws.Sent.Clear();
            await bridge.HandleMessageAsync("{\"type\":\"export_html\",\"outputPath\":\"/tmp/custom.html\"}");
            var custom = Assert.Single(client.Sent.OfType<ExportHtmlCommand>());
            Assert.Equal("/tmp/custom.html", custom.OutputPath);
            Assert.Equal("{\"type\":\"export_html\",\"outputPath\":\"/tmp/custom.html\"}", custom.ToJson());
        }
        finally { await CleanupAsync(mgr, client, dir); }
    }

    [Fact]
    public async Task Ticket08_commands_are_scoped_to_attached_session()
    {
        var clientA = Ticket08Client();
        var clientB = Ticket08Client();
        var queued = new Queue<IPiRpcClient>(new IPiRpcClient[] { clientA, clientB });
        var dir = Path.Combine(Path.GetTempPath(), $"piwebui08iso-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await using var mgr = new SessionManager(() => queued.Dequeue(), dir);
            await mgr.InitAsync("a");
            await mgr.InitAsync("b");

            var ws = new FakeWsClient();
            var bridge = new WsBridge(mgr, "a", ws); // attached to a

            await bridge.HandleMessageAsync("{\"type\":\"compact\"}");
            await bridge.HandleMessageAsync("{\"type\":\"set_auto_compaction\",\"enabled\":true}");
            await bridge.HandleMessageAsync("{\"type\":\"set_auto_retry\",\"enabled\":true}");
            await bridge.HandleMessageAsync("{\"type\":\"stats\"}");
            await bridge.HandleMessageAsync("{\"type\":\"export_html\"}");

            // all five landed on a's child only — b's child untouched
            Assert.Single(clientA.Sent.OfType<CompactCommand>());
            Assert.Single(clientA.Sent.OfType<SetAutoCompactionCommand>());
            Assert.Single(clientA.Sent.OfType<SetAutoRetryCommand>());
            Assert.Single(clientA.Sent.OfType<GetSessionStatsCommand>());
            Assert.Single(clientA.Sent.OfType<ExportHtmlCommand>());
            Assert.Empty(clientB.Sent.OfType<CompactCommand>());
            Assert.Empty(clientB.Sent.OfType<SetAutoCompactionCommand>());
            Assert.Empty(clientB.Sent.OfType<SetAutoRetryCommand>());
            Assert.Empty(clientB.Sent.OfType<GetSessionStatsCommand>());
            Assert.Empty(clientB.Sent.OfType<ExportHtmlCommand>());
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            await clientA.DisposeAsync();
            await clientB.DisposeAsync();
        }
    }

    [Fact]
    public async Task Ticket08_command_to_recycled_session_reports_error()
    {
        var client = Ticket08Client();
        var dir = Path.Combine(Path.GetTempPath(), $"piwebui08rec-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await using var mgr = new SessionManager(() => client, dir);
            await mgr.InitAsync("a");
            await mgr.RecycleAsync("a");

            var ws = new FakeWsClient();
            var bridge = new WsBridge(mgr, "a", ws);

            await bridge.HandleMessageAsync("{\"type\":\"compact\"}");
            await bridge.HandleMessageAsync("{\"type\":\"stats\"}");
            await bridge.HandleMessageAsync("{\"type\":\"export_html\"}");

            // every ticket #08 command on a stopped session surfaces an error, never a crash
            Assert.Equal(3, ws.Sent.Count);
            Assert.All(ws.Sent, s => Assert.Contains("\"error\"", s));
            Assert.All(ws.Sent, s => Assert.Contains("not running", s));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            await client.DisposeAsync();
        }
    }
}

using System.Threading.Channels;
using PiWebui.Rpc;
using PiWebui.Session;
using PiWebui.Web;
using Xunit;

namespace PiWebui.Tests;

/// <summary>
/// Seam 2 — the WebSocket API boundary, backed by the fake pi. Verifies the WS layer
/// relays a named session's events to a connected client, routes prompts only to the
/// attached session's child, and handles lifecycle messages (init/recycle/delete)
/// with per-session isolation.
/// </summary>
public class WsBridgeTests
{
    private const string AgentStartRaw = "{\"type\":\"agent_start\"}";
    private const string TextDeltaRaw =
        "{\"type\":\"message_update\",\"assistantMessageEvent\":{\"type\":\"text_delta\",\"delta\":\"part1\"}}";
    private const string PromptJson = "{\"type\":\"prompt\",\"message\":\"hello from browser\"}";

    private sealed class Harness : IDisposable
    {
        public string Dir;
        public List<FakePiRpcClient> Clients = new();
        public SessionManager Manager;

        public Harness()
        {
            Dir = Path.Combine(Path.GetTempPath(), $"piwebui-ws-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Dir);
            Manager = new SessionManager(() =>
            {
                var f = new FakePiRpcClient();
                Clients.Add(f);
                return f;
            }, Dir);
        }

        public void Dispose()
        {
            if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true);
        }
    }

    [Fact]
    public async Task Relays_named_sessions_events_to_connected_client()
    {
        using var h = new Harness();
        await h.Manager.InitAsync("feature-work");
        var fake = h.Clients[0];

        var ws = new FakeWsClient();
        var bridge = new WsBridge(h.Manager, "feature-work", ws);

        // bridge subscribes to the attached session's stream at construction
        using var cts = new CancellationTokenSource();
        var fwd = Task.Run(() => bridge.ForwardLoopAsync(cts.Token));

        fake.Emit(new AgentStartEvent { Raw = AgentStartRaw });
        fake.Emit(new MessageUpdateEvent(null, null, "text_delta", "part1") { Raw = TextDeltaRaw });

        await TestWait.UntilAsync(() => ws.Sent.Count >= 2);
        Assert.Contains(ws.Sent, s => s.Contains("\"agent_start\""));
        Assert.Contains(ws.Sent, s => s.Contains("\"text_delta\"") && s.Contains("\"part1\""));

        cts.Cancel();
        try { await fwd; } catch (OperationCanceledException) { /* expected on cancel */ }
    }

    [Fact]
    public async Task Events_from_other_sessions_do_not_reach_attached_client()
    {
        using var h = new Harness();
        await h.Manager.InitAsync("a");
        await h.Manager.InitAsync("b");
        var fakeA = h.Clients[0];

        var ws = new FakeWsClient();
        var bridge = new WsBridge(h.Manager, "b", ws); // tab attached to session b

        using var cts = new CancellationTokenSource();
        var fwd = Task.Run(() => bridge.ForwardLoopAsync(cts.Token));

        // session a produces an event — the tab on b must NOT see it
        fakeA.Emit(new AgentStartEvent { Raw = AgentStartRaw });
        await Task.Delay(150);
        Assert.Empty(ws.Sent);

        cts.Cancel();
        try { await fwd; } catch (OperationCanceledException) { /* expected */ }
    }

    [Fact]
    public async Task Prompt_message_from_browser_forwards_to_attached_sessions_agent()
    {
        using var h = new Harness();
        await h.Manager.InitAsync("a");
        await h.Manager.InitAsync("b");
        var fakeA = h.Clients[0];
        var fakeB = h.Clients[1];

        var ws = new FakeWsClient();
        var bridge = new WsBridge(h.Manager, "b", ws);

        await bridge.HandleMessageAsync(PromptJson);

        // prompt went to session b's child only — not a's
        Assert.Single(fakeB.Sent.OfType<PromptCommand>());
        Assert.Equal("hello from browser", fakeB.Sent.OfType<PromptCommand>().Single().Message);
        Assert.Empty(fakeA.Sent.OfType<PromptCommand>());
    }

    [Fact]
    public async Task Prompt_to_recycled_session_reports_error()
    {
        using var h = new Harness();
        await h.Manager.InitAsync("a");
        await h.Manager.RecycleAsync("a");

        var ws = new FakeWsClient();
        var bridge = new WsBridge(h.Manager, "a", ws);

        await bridge.HandleMessageAsync(PromptJson);

        var err = Assert.Single(ws.Sent);
        Assert.Contains("\"error\"", err);
        Assert.Contains("not running", err);
    }

    [Fact]
    public async Task Rejected_prompt_sends_error_back_to_browser()
    {
        using var h = new Harness();
        // the only client gets a rejecting prompt responder
        var rejecting = new FakePiRpcClient(cmd =>
            cmd is PromptCommand ? new RpcResponse("r1", "prompt", false, "model busy", null) : null);
        var dir = h.Dir;
        await using var mgr = new SessionManager(() => rejecting, dir);
        await mgr.InitAsync("a");

        var ws = new FakeWsClient();
        var bridge = new WsBridge(mgr, "a", ws);

        await bridge.HandleMessageAsync(PromptJson);

        var err = Assert.Single(ws.Sent);
        Assert.Contains("\"error\"", err);
        Assert.Contains("prompt rejected", err);
        Assert.Contains("model busy", err);
    }

    [Fact]
    public async Task Prompt_after_disposed_child_does_not_crash_inbound_loop()
    {
        // Simulates a prompt racing a recycle: the child was disposed mid-write and
        // throws ObjectDisposedException, which must NOT tear down the WS connection.
        var disposed = new FakePiRpcClient(cmd =>
            cmd is PromptCommand
                ? throw new ObjectDisposedException("stdin")
                : new RpcResponse("ok", cmd.Type, true, null, null));
        var dir = Path.Combine(Path.GetTempPath(), $"piwebui-race-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await using var mgr = new SessionManager(() => disposed, dir);
            await mgr.InitAsync("a");

            var ws = new FakeWsClient();
            ws.EnqueueInbound(PromptJson);
            ws.CompleteInbound(); // inbound loop exits after the single message

            var bridge = new WsBridge(mgr, "a", ws);
            // must NOT throw out of the inbound loop: RunAsync completes cleanly
            await bridge.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(ws.Closed);
            // the dispose race surfaced as a normal error to the browser, not a crash
            Assert.Contains(ws.Sent, s => s.Contains("\"error\""));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Inbound_loop_dispatches_prompt_from_browser()
    {
        using var h = new Harness();
        await h.Manager.InitAsync("a");
        var fake = h.Clients[0];

        var ws = new FakeWsClient();
        ws.EnqueueInbound(PromptJson);
        ws.CompleteInbound(); // lets the inbound loop exit after one message

        var bridge = new WsBridge(h.Manager, "a", ws);
        await bridge.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(fake.Sent.OfType<PromptCommand>());
        Assert.Equal("hello from browser", fake.Sent.OfType<PromptCommand>().Single().Message);
    }

    [Fact]
    public async Task RunAsync_relays_events_and_closes_cleanly()
    {
        using var h = new Harness();
        await h.Manager.InitAsync("a");
        var fake = h.Clients[0];

        var ws = new FakeWsClient();
        var bridge = new WsBridge(h.Manager, "a", ws);

        var run = Task.Run(() => bridge.RunAsync());
        await Task.Delay(150);

        fake.Emit(new AgentStartEvent { Raw = AgentStartRaw });
        await TestWait.UntilAsync(() => ws.Sent.Count >= 1);
        Assert.Contains(ws.Sent, s => s.Contains("\"agent_start\""));

        ws.CompleteInbound(); // close inbound -> bridge tears down + closes client
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(ws.Closed);
    }

    // --- lifecycle over the WS boundary --------------------------------------

    [Fact]
    public async Task Init_message_creates_named_session_and_reports_running()
    {
        using var h = new Harness();
        var ws = new FakeWsClient();
        var bridge = new WsBridge(h.Manager, "feature-work", ws); // session not created yet

        await bridge.HandleMessageAsync("{\"type\":\"init\"}");

        var s = h.Manager.Get("feature-work");
        Assert.NotNull(s);
        Assert.True(s!.IsRunning);
        Assert.Single(h.Clients); // child spawned by init (explicit, not on connect)

        await TestWait.UntilAsync(() => ws.Sent.Count >= 1);
        var frame = ws.Sent.Single();
        Assert.Contains("\"session_event\"", frame);
        Assert.Contains("\"init\"", frame);
        Assert.Contains("\"running\"", frame);
        Assert.Contains("\"feature-work\"", frame);
    }

    [Fact]
    public async Task Attaches_to_stream_after_late_init()
    {
        using var h = new Harness();
        var ws = new FakeWsClient();
        var bridge = new WsBridge(h.Manager, "a", ws); // not created yet -> not subscribed

        var msgs = new[] { "{\"type\":\"init\"}" };
        using var cts = new CancellationTokenSource();
        var fwd = Task.Run(() => bridge.ForwardLoopAsync(cts.Token));

        await bridge.HandleMessageAsync(msgs[0]); // creates session a -> bridge attaches
        await Task.Delay(200); // give ForwardLoop a beat to pick up the subscription

        h.Clients[0].Emit(new AgentStartEvent { Raw = AgentStartRaw });
        await TestWait.UntilAsync(() => ws.Sent.Any(s => s.Contains("\"agent_start\"")));

        cts.Cancel();
        try { await fwd; } catch (OperationCanceledException) { /* expected */ }
    }

    [Fact]
    public async Task Recycle_message_stops_child_and_reports_recycled()
    {
        using var h = new Harness();
        await h.Manager.InitAsync("a");
        var child = h.Clients[0];

        var ws = new FakeWsClient();
        var bridge = new WsBridge(h.Manager, "a", ws);

        await bridge.HandleMessageAsync("{\"type\":\"recycle\"}");

        Assert.True(child.Disposed);
        Assert.False(h.Manager.Get("a")!.IsRunning);
        Assert.Equal(SessionStatus.Recycled, h.Manager.Get("a")!.Status);

        await TestWait.UntilAsync(() => ws.Sent.Count >= 1);
        var frame = ws.Sent.Single();
        Assert.Contains("\"recycled\"", frame);
        Assert.Contains("\"recycle\"", frame);
    }

    [Fact]
    public async Task Delete_message_removes_session_and_closes_attached_connection()
    {
        using var h = new Harness();
        await h.Manager.InitAsync("a");

        var ws = new FakeWsClient();
        var bridge = new WsBridge(h.Manager, "a", ws);

        await bridge.HandleMessageAsync("{\"type\":\"delete\"}");

        Assert.Null(h.Manager.Get("a"));
        Assert.True(ws.Closed); // the tab's session is gone -> connection closed
        Assert.Contains(ws.Sent, s => s.Contains("\"deleted\""));
    }

    [Fact]
    public async Task Lifecycle_message_can_target_another_session()
    {
        using var h = new Harness();
        await h.Manager.InitAsync("a");
        await h.Manager.InitAsync("b");
        var childB = h.Clients[1];

        var ws = new FakeWsClient();
        // tab is on session a, but recycles session b via the name field
        var bridge = new WsBridge(h.Manager, "a", ws);

        await bridge.HandleMessageAsync("{\"type\":\"recycle\",\"name\":\"b\"}");

        Assert.True(childB.Disposed);
        Assert.Equal(SessionStatus.Recycled, h.Manager.Get("b")!.Status);
        Assert.True(h.Manager.Get("a")!.IsRunning); // attached session untouched
        Assert.False(ws.Closed);
    }
}

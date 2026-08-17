using System.Threading.Channels;
using PiWebui.Rpc;
using PiWebui.Session;
using PiWebui.Web;
using Xunit;

namespace PiWebui.Tests;

/// <summary>
/// Seam 2 — the WebSocket API boundary, backed by the fake pi. Verifies the WS
/// layer relays RPC events to connected clients and turns client messages into
/// the right RPC calls.
/// </summary>
public class WsBridgeTests
{
    private const string AgentStartRaw = "{\"type\":\"agent_start\"}";
    private const string TextDeltaRaw =
        "{\"type\":\"message_update\",\"assistantMessageEvent\":{\"type\":\"text_delta\",\"delta\":\"part1\"}}";
    private const string PromptJson = "{\"type\":\"prompt\",\"message\":\"hello from browser\"}";

    [Fact]
    public async Task Relays_rpc_events_to_connected_client()
    {
        var fake = new FakePiRpcClient();
        await using var session = new SessionManager(fake);
        session.Start();

        var ws = new FakeWsClient();
        var bridge = new WsBridge(session, ws);

        // Subscribe before emitting so no live events are dropped (fan-out has
        // live-stream semantics: events published before a client attaches are not
        // replayed). Drive the forward loop directly for determinism.
        var stream = session.Subscribe();
        using var cts = new CancellationTokenSource();
        var fwd = Task.Run(() => bridge.ForwardLoopAsync(stream, cts.Token));

        // events produced by the (fake) pi child reach the browser verbatim
        fake.Emit(new AgentStartEvent { Raw = AgentStartRaw });
        fake.Emit(new MessageUpdateEvent(null, null, "text_delta", "part1") { Raw = TextDeltaRaw });

        await TestWait.UntilAsync(() => ws.Sent.Count >= 2);
        Assert.Contains(ws.Sent, s => s.Contains("\"agent_start\""));
        Assert.Contains(ws.Sent, s => s.Contains("\"text_delta\"") && s.Contains("\"part1\""));

        cts.Cancel();
        session.Unsubscribe(stream);
        try { await fwd; } catch (OperationCanceledException) { /* expected on cancel */ }
    }

    [Fact]
    public async Task Prompt_message_from_browser_forwards_to_agent()
    {
        var fake = new FakePiRpcClient();
        await using var session = new SessionManager(fake);
        session.Start();

        var ws = new FakeWsClient();
        var bridge = new WsBridge(session, ws);

        await bridge.HandleMessageAsync(PromptJson);

        var sent = fake.Sent.OfType<PromptCommand>().Single();
        Assert.Equal("hello from browser", sent.Message);
    }

    [Fact]
    public async Task Inbound_loop_dispatches_prompt_from_browser()
    {
        var fake = new FakePiRpcClient();
        await using var session = new SessionManager(fake);
        session.Start();

        var ws = new FakeWsClient();
        ws.EnqueueInbound(PromptJson);
        ws.CompleteInbound(); // lets the inbound loop exit after one message

        var bridge = new WsBridge(session, ws);
        await bridge.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(fake.Sent.OfType<PromptCommand>());
        Assert.Equal("hello from browser", fake.Sent.OfType<PromptCommand>().Single().Message);
    }

    [Fact]
    public async Task RunAsync_relays_events_and_closes_cleanly()
    {
        var fake = new FakePiRpcClient();
        await using var session = new SessionManager(fake);
        session.Start();

        var ws = new FakeWsClient();
        var bridge = new WsBridge(session, ws);

        // give the bridge a beat to attach its subscriber channel
        var run = Task.Run(() => bridge.RunAsync());
        await Task.Delay(150);

        fake.Emit(new AgentStartEvent { Raw = AgentStartRaw });
        await TestWait.UntilAsync(() => ws.Sent.Count >= 1);
        Assert.Contains(ws.Sent, s => s.Contains("\"agent_start\""));

        ws.CompleteInbound(); // close inbound -> bridge tears down + closes client
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(ws.Closed);
    }
}

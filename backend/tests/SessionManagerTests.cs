using System.Threading.Channels;
using PiWebui.Rpc;
using PiWebui.Session;
using Xunit;

namespace PiWebui.Tests;

public class SessionManagerTests
{
    [Fact]
    public async Task Fans_out_client_events_to_subscribers()
    {
        var fake = new FakePiRpcClient();
        await using var session = new SessionManager(fake);
        session.Start();

        var stream = session.Subscribe();
        var received = new List<RpcEvent>();
        _ = Task.Run(async () =>
        {
            await foreach (var ev in stream.Reader.ReadAllAsync())
            {
                lock (received) received.Add(ev);
            }
        });

        fake.Emit(new AgentStartEvent { Raw = @"{""type"":""agent_start""}" });
        fake.Emit(new MessageUpdateEvent(null, null, "text_delta", "hi") { Raw = @"{""type"":""message_update""}" });

        await TestWait.UntilAsync(() => { lock (received) return received.Count >= 2; });

        Assert.Contains(received, e => e.Type == "agent_start");
        Assert.Contains(received, e => e is MessageUpdateEvent m && m.Delta == "hi");
        session.Unsubscribe(stream);
    }

    [Fact]
    public async Task Prompt_forwards_to_client()
    {
        var fake = new FakePiRpcClient(cmd => cmd is PromptCommand ? null : null);
        await using var session = new SessionManager(fake);
        session.Start();

        var resp = await session.PromptAsync("Fix the bug");

        Assert.Single(fake.Sent.OfType<PromptCommand>());
        Assert.Equal("Fix the bug", fake.Sent.OfType<PromptCommand>().Single().Message);
        Assert.Null(resp); // fake returned no response
    }

    [Fact]
    public async Task Prompt_serialises_as_correct_json_line()
    {
        var cmd = new PromptCommand("hello");
        var json = cmd.ToJson("req-1");
        // framing contract: one JSON object, LF delimiter is added by the writer
        Assert.Contains("\"id\":\"req-1\"", json);
        Assert.Contains("\"type\":\"prompt\"", json);
        Assert.Contains("\"message\":\"hello\"", json);
        Assert.DoesNotContain("\n", json);
    }
}

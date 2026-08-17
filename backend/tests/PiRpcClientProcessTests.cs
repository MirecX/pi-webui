using PiWebui.Rpc;
using Xunit;

namespace PiWebui.Tests;

/// <summary>
/// Highest-value seam: verifies PiRpcClient against a SCRIPTED fake pi process
/// (not the real pi). Exercises real process spawning, JSONL framing (LF-only,
/// CR-stripping), typed event parsing, and command/response id correlation.
/// </summary>
public class PiRpcClientProcessTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    [Fact]
    public async Task Receives_start_events_and_correlates_prompt_response()
    {
        var scenario = Path.Combine(Path.GetTempPath(), $"fake-pi-{Guid.NewGuid():N}.json");
        File.WriteAllText(scenario, string.Join("\n", new[]
        {
            // startup events
            @"{""when"":""start"",""emit"":[" +
              @" {""type"":""agent_start""}," +
              @" {""type"":""message_start"",""message"":{""role"":""assistant""}}," +
              // text_delta with embedded Unicode separator exercised end-to-end
              @" {""type"":""message_update"",""message"":{""role"":""assistant""},""assistantMessageEvent"":{""type"":""text_delta"",""delta"":""Hi\u2028there""}}," +
              @" {""type"":""message_end"",""message"":{""role"":""assistant""}}," +
              @" {""type"":""agent_end"",""messages"":[],""willRetry"":false}" +
              @" ]}",
            // events emitted when a prompt command arrives
            @"{""when"":""prompt"",""emit"":[" +
              @" {""type"":""message_update"",""message"":{},""assistantMessageEvent"":{""type"":""text_delta"",""delta"":""reply!""}}," +
              @" {""type"":""agent_end"",""messages"":[],""willRetry"":false}" +
              @" ]}",
        }));

        try
        {
            var received = new List<RpcEvent>();
            await using var client = new PiRpcClient(new PiClientOptions
            {
                Executable = "node",
                Arguments = new[] { FixturePath("fake-pi.mjs") },
                WorkingDirectory = Path.GetDirectoryName(FixturePath("fake-pi.mjs"))!,
                Environment = new Dictionary<string, string> { ["FAKE_PI_SCENARIO"] = scenario },
            });
            client.EventReceived += received.Add;
            client.Start();

            // 1) startup events should be parsed in order
            await TestWait.UntilAsync(() => received.Count >= 5);

            var types = received.Select(e => e.Type).ToArray();
            Assert.Contains("agent_start", types);
            Assert.Contains("message_start", types);
            Assert.Contains("message_end", types);
            Assert.Contains("agent_end", types);

            // streaming delta parsed correctly, including embedded unicode separator
            var delta = Assert.Single(received.OfType<MessageUpdateEvent>(),
                m => m.DeltaType == "text_delta" && m.Delta is not null);
            Assert.Contains("\u2028", delta.Delta);
            Assert.Equal("Hi\u2028there", delta.Delta);

            var agentEnd = Assert.Single(received.OfType<AgentEndEvent>());
            Assert.False(agentEnd.WillRetry);

            // 2) prompt command -> correlated response id, plus reply events
            var sendTask = client.SendAsync(new PromptCommand("hello"));
            var resp = await sendTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(resp);
            Assert.True(resp!.Success);
            Assert.Equal("prompt", resp.Command);
            Assert.NotNull(resp.Id); // client assigned + child echoed it

            await TestWait.UntilAsync(() => received.OfType<MessageUpdateEvent>()
                .Any(m => m.Delta == "reply!"));

            // the raw line is preserved for faithful WS relay
            var update = received.OfType<MessageUpdateEvent>().Last(m => m.Delta == "reply!");
            Assert.Contains("\"reply!\"", update.Raw);
        }
        finally
        {
            File.Delete(scenario);
        }
    }

    [Fact]
    public async Task Response_without_id_does_not_crash_reader()
    {
        // A response lacking any pending id is dropped gracefully.
        var scenario = Path.Combine(Path.GetTempPath(), $"fake-pi-{Guid.NewGuid():N}.json");
        File.WriteAllText(scenario, string.Join("\n", new[]
        {
            // a stray response with an unknown id arrives first
            @"{""when"":""start"",""emit"":[{""type"":""response"",""id"":""nope"",""command"":""prompt"",""success"":true,""data"":{}}]}",
        }));

        try
        {
            var received = new List<RpcEvent>();
            await using var client = new PiRpcClient(new PiClientOptions
            {
                Executable = "node",
                Arguments = new[] { FixturePath("fake-pi.mjs") },
                WorkingDirectory = Path.GetDirectoryName(FixturePath("fake-pi.mjs"))!,
                Environment = new Dictionary<string, string> { ["FAKE_PI_SCENARIO"] = scenario },
            });
            client.EventReceived += received.Add;
            client.Start();

            // still able to send/await a fresh correlated command
            var resp = await client.SendAsync(new GetStateCommand()).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(resp);
            Assert.Equal("get_state", resp!.Command);
        }
        finally
        {
            File.Delete(scenario);
        }
    }

    [Fact]
    public async Task Steer_follow_up_abort_roundtrip_through_jsonl_pipe()
    {
        // The fake pi acknowledges EVERY command type generically and emits the configured
        // byType/scenario events, so steer/follow_up/abort get a correlated success response
        // and any emitted queue/agent events parse as typed events.
        var scenario = Path.Combine(Path.GetTempPath(), $"fake-pi-{Guid.NewGuid():N}.json");
        File.WriteAllText(scenario, string.Join("\n", new[]
        {
            // startup: a queue forms then the agent settles
            @"{""when"":""start"",""emit"":[{""type"":""queue_update"",""steering"":[""steer me""],""followUp"":[]},{""type"":""agent_settled""}]}",
            // steering command ack: the queue drains
            @"{""when"":""steer"",""emit"":[{""type"":""queue_update"",""steering"":[],""followUp"":[]}]}",
        }));

        try
        {
            var received = new List<RpcEvent>();
            await using var client = new PiRpcClient(new PiClientOptions
            {
                Executable = "node",
                Arguments = new[] { FixturePath("fake-pi.mjs") },
                WorkingDirectory = Path.GetDirectoryName(FixturePath("fake-pi.mjs"))!,
                Environment = new Dictionary<string, string> { ["FAKE_PI_SCENARIO"] = scenario },
            });
            client.EventReceived += received.Add;
            client.Start();

            // startup queue/agent events are parsed and surfaced
            await TestWait.UntilAsync(() => received.OfType<QueueUpdateEvent>().Any());
            Assert.Contains(received, e => e is AgentSettledEvent);
            var q = Assert.Single(received.OfType<QueueUpdateEvent>());
            Assert.NotNull(q.Steering);
            Assert.NotNull(q.FollowUp);

            // each turn-control command round-trips with a correlated success response
            var steer = await client.SendAsync(new SteerCommand("stop and pivot")).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(steer);
            Assert.True(steer!.Success);
            Assert.Equal("steer", steer.Command);

            var fu = await client.SendAsync(new FollowUpCommand("then summarize")).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(fu);
            Assert.True(fu!.Success);
            Assert.Equal("follow_up", fu.Command);

            var abort = await client.SendAsync(new AbortCommand()).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(abort);
            Assert.True(abort!.Success);
            Assert.Equal("abort", abort.Command);

            // steer command also triggered the configured queue_update event
            await TestWait.UntilAsync(() => received.OfType<QueueUpdateEvent>().Count() >= 2);
        }
        finally
        {
            File.Delete(scenario);
        }
    }
}

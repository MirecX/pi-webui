using System.Text.Json;
using System.Threading.Channels;
using PiWebui.Rpc;
using PiWebui.Session;
using Xunit;

namespace PiWebui.Tests;

/// <summary>
/// Seam 1 — the session registry, backed by the fake pi. Verifies named multi-session
/// lifecycle: init (create/resume), recycle (kill child, keep history), delete
/// (remove permanently), and that a slow session doesn't block another (per-session
/// children, no shared lock).
/// </summary>
public class SessionManagerTests
{
    private sealed class Harness : IDisposable
    {
        public string Dir;
        public List<FakePiRpcClient> Clients = new();
        public SessionManager Manager;

        public Harness()
        {
            Dir = Path.Combine(Path.GetTempPath(), $"piwebui-ses-{Guid.NewGuid():N}");
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
    public async Task Init_creates_named_session_with_its_own_child()
    {
        using var h = new Harness();
        var session = await h.Manager.InitAsync("feature-work");

        Assert.NotNull(session);
        Assert.Equal("feature-work", session.Name);
        Assert.True(session.IsRunning);
        Assert.Equal(SessionStatus.Running, session.Status);
        // exactly one isolated child was spawned for this session
        Assert.Single(h.Clients);
        Assert.True(h.Clients[0].Started);
        Assert.Same(h.Manager.Get("feature-work"), session);
    }

    [Fact]
    public async Task Multiple_named_sessions_each_get_their_own_child()
    {
        using var h = new Harness();
        await h.Manager.InitAsync("a");
        await h.Manager.InitAsync("b");

        Assert.Equal(2, h.Manager.List().Count);
        Assert.Equal(2, h.Clients.Count);
        Assert.NotSame(h.Clients[0], h.Clients[1]); // isolated children
        Assert.Equal(new[] { "a", "b" }, h.Manager.List().Select(s => s.Name).ToArray());
    }

    [Fact]
    public async Task Fans_out_client_events_to_subscribers()
    {
        using var h = new Harness();
        var session = await h.Manager.InitAsync("default");

        var stream = session.Subscribe();
        var received = new List<RpcEvent>();
        _ = Task.Run(async () =>
        {
            await foreach (var ev in stream.Reader.ReadAllAsync())
            {
                lock (received) received.Add(ev);
            }
        });

        h.Clients[0].Emit(new AgentStartEvent { Raw = @"{""type"":""agent_start""}" });
        h.Clients[0].Emit(new MessageUpdateEvent(null, null, "text_delta", "hi") { Raw = @"{""type"":""message_update""}" });

        await TestWait.UntilAsync(() => { lock (received) return received.Count >= 2; });

        Assert.Contains(received, e => e.Type == "agent_start");
        Assert.Contains(received, e => e is MessageUpdateEvent m && m.Delta == "hi");
        session.Unsubscribe(stream);
    }

    [Fact]
    public async Task Events_from_one_session_do_not_leak_to_another()
    {
        using var h = new Harness();
        var a = await h.Manager.InitAsync("a");
        var b = await h.Manager.InitAsync("b");

        var fromB = b.Subscribe();
        var receivedFromB = new List<RpcEvent>();
        _ = Task.Run(async () =>
        {
            await foreach (var ev in fromB.Reader.ReadAllAsync())
            {
                lock (receivedFromB) receivedFromB.Add(ev);
            }
        });

        // event produced by session a's child must NOT appear on session b's stream
        h.Clients[0].Emit(new AgentStartEvent { Raw = @"{""type"":""agent_start""}" });

        await Task.Delay(150);
        lock (receivedFromB) Assert.Empty(receivedFromB);
        b.Unsubscribe(fromB);
    }

    [Fact]
    public async Task Prompt_forwards_to_client()
    {
        using var h = new Harness();
        var session = await h.Manager.InitAsync("default");

        var resp = await session.PromptAsync("Fix the bug");

        Assert.Single(h.Clients[0].Sent.OfType<PromptCommand>());
        Assert.Equal("Fix the bug", h.Clients[0].Sent.OfType<PromptCommand>().Single().Message);
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

    [Fact]
    public async Task Switch_session_command_serialises_session_path()
    {
        var cmd = new SwitchSessionCommand("/tmp/history.jsonl");
        var json = cmd.ToJson("r2");
        Assert.Contains("\"type\":\"switch_session\"", json);
        Assert.Contains("\"sessionPath\":\"/tmp/history.jsonl\"", json);
    }

    [Fact]
    public async Task Steer_follow_up_abort_serialise_as_correct_json_lines()
    {
        var steer = new SteerCommand("stop and pivot");
        var steerJson = steer.ToJson();
        Assert.Contains("\"type\":\"steer\"", steerJson);
        Assert.Contains("\"message\":\"stop and pivot\"", steerJson);

        var fu = new FollowUpCommand("then summarize");
        var fuJson = fu.ToJson();
        Assert.Contains("\"type\":\"follow_up\"", fuJson);
        Assert.Contains("\"message\":\"then summarize\"", fuJson);

        var abort = new AbortCommand().ToJson();
        Assert.Contains("\"type\":\"abort\"", abort);
    }

    [Fact]
    public async Task Turn_control_methods_forward_to_client()
    {
        using var h = new Harness();
        var session = await h.Manager.InitAsync("default");
        var client = h.Clients[0];

        await session.SteerAsync("steer me");
        await session.FollowUpAsync("follow me");
        await session.AbortAsync();

        var steer = Assert.Single(client.Sent.OfType<SteerCommand>());
        Assert.Equal("steer me", steer.Message);
        var fu = Assert.Single(client.Sent.OfType<FollowUpCommand>());
        Assert.Equal("follow me", fu.Message);
        Assert.Single(client.Sent.OfType<AbortCommand>());
    }

    [Fact]
    public async Task Model_and_thinking_commands_serialise_as_correct_json_lines()
    {
        // set_model: type + provider + modelId (exact rpc.md wire names)
        var setModel = new SetModelCommand("anthropic", "claude-3-5-sonnet").ToJson("r1");
        Assert.Contains("\"type\":\"set_model\"", setModel);
        Assert.Contains("\"provider\":\"anthropic\"", setModel);
        Assert.Contains("\"modelId\":\"claude-3-5-sonnet\"", setModel);

        // set_thinking_level: type + level
        var setLevel = new SetThinkingLevelCommand("high").ToJson("r2");
        Assert.Contains("\"type\":\"set_thinking_level\"", setLevel);
        Assert.Contains("\"level\":\"high\"", setLevel);

        // list commands carry their own types
        Assert.Contains("\"type\":\"get_available_models\"", new GetAvailableModelsCommand().ToJson());
        Assert.Contains("\"type\":\"get_available_thinking_levels\"", new GetAvailableThinkingLevelsCommand().ToJson());
    }

    [Fact]
    public async Task Model_and_thinking_methods_forward_to_client()
    {
        using var h = new Harness();
        var session = await h.Manager.InitAsync("default");
        var client = h.Clients[0];

        await session.GetAvailableModelsAsync();
        await session.SetModelAsync("anthropic", "claude-3-5-sonnet");
        await session.GetStateAsync();
        await session.GetAvailableThinkingLevelsAsync();
        await session.SetThinkingLevelAsync("high");

        var setModel = Assert.Single(client.Sent.OfType<SetModelCommand>());
        Assert.Equal("anthropic", setModel.Provider);
        Assert.Equal("claude-3-5-sonnet", setModel.ModelId);
        Assert.Single(client.Sent.OfType<GetAvailableModelsCommand>());
        // get_state is sent here plus once by InitAsync for session-file discovery.
        Assert.Equal(2, client.Sent.OfType<GetStateCommand>().Count());
        Assert.Single(client.Sent.OfType<GetAvailableThinkingLevelsCommand>());
        Assert.Equal("high", Assert.Single(client.Sent.OfType<SetThinkingLevelCommand>()).Level);
    }

    [Fact]
    public async Task Model_and_thinking_commands_on_recycled_session_throw_not_running()
    {
        using var h = new Harness();
        await h.Manager.InitAsync("a");
        await h.Manager.RecycleAsync("a");
        var session = h.Manager.Get("a")!;

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.GetAvailableModelsAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SetModelAsync("a", "b"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.GetAvailableThinkingLevelsAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SetThinkingLevelAsync("high"));
    }

    [Fact]
    public async Task Turn_control_on_recycled_session_throws_not_running()
    {
        using var h = new Harness();
        var session = await h.Manager.InitAsync("a");
        await h.Manager.RecycleAsync("a");

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SteerAsync("x"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.FollowUpAsync("y"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.AbortAsync());
    }

    [Fact]
    public async Task Turn_control_after_disposed_child_surfaces_clean_not_running_error()
    {
        var disposed = new FakePiRpcClient(cmd =>
            cmd is SteerCommand
                ? throw new ObjectDisposedException("stdin")
                : new RpcResponse("ok", cmd.Type, true, null, null));
        var dir = Path.Combine(Path.GetTempPath(), $"piwebui-steerrace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await using var mgr = new SessionManager(() => disposed, dir);
            var session = await mgr.InitAsync("a");

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => session.SteerAsync("x"));
            Assert.Contains("recycled mid-steer", ex.Message);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Recycle_stops_child_but_preserves_session_and_history()
    {
        using var h = new Harness();
        var session = await h.Manager.InitAsync("a");

        // brand-new session tracks a managed history file path
        var history = Path.Combine(h.Dir, "a.jsonl");
        Assert.Equal(history, session.HistoryFilePath);
        File.WriteAllText(history, "{}\n"); // simulate history produced by the child

        var child = h.Clients[0];
        await h.Manager.RecycleAsync("a");

        Assert.True(child.Disposed);            // child was stopped
        Assert.False(session.IsRunning);        // no live child
        Assert.Equal(SessionStatus.Recycled, session.Status);
        Assert.NotNull(h.Manager.Get("a"));     // still in the registry (resumable)
        Assert.True(File.Exists(history));      // history file preserved
        Assert.Equal("a", h.Manager.Get("a")!.Name);
    }

    [Fact]
    public async Task Init_after_recycle_resumes_with_fresh_child_and_switch_session()
    {
        using var h = new Harness();
        var session = await h.Manager.InitAsync("a");
        var child1 = h.Clients[0];

        var history = Path.Combine(h.Dir, "a.jsonl");
        File.WriteAllText(history, "{}\n");     // preserved history from first run
        await h.Manager.RecycleAsync("a");
        Assert.True(child1.Disposed);

        var resumed = await h.Manager.InitAsync("a");

        Assert.Same(session, resumed);
        Assert.Equal(2, h.Clients.Count);       // a fresh child was spawned
        var child2 = h.Clients[1];
        Assert.NotSame(child1, child2);
        Assert.True(child2.Started);
        Assert.True(resumed.IsRunning);
        // the fresh child was pointed back at the preserved history file
        var sw = Assert.Single(child2.Sent.OfType<SwitchSessionCommand>());
        Assert.Equal(history, sw.SessionPath);
    }

    [Fact]
    public async Task Concurrent_init_on_same_new_name_spawns_only_one_child()
    {
        using var h = new Harness();

        // Two simultaneous inits on a brand-new name must serialize so exactly ONE child
        // is spawned and both callers observe the same single session.
        var t1 = h.Manager.InitAsync("a");
        var t2 = h.Manager.InitAsync("a");
        var s1 = await t1;
        var s2 = await t2;

        Assert.Same(s1, s2);
        Assert.Single(h.Clients);       // exactly one child, never two
        Assert.True(s1.IsRunning);
        Assert.Same(h.Manager.Get("a"), s1);
    }

    [Fact]
    public async Task Init_is_idempotent_for_a_running_session()
    {
        using var h = new Harness();
        await h.Manager.InitAsync("a");
        var first = h.Clients[0];
        var again = await h.Manager.InitAsync("a");

        // no extra child spawned, same session returned
        Assert.Single(h.Clients);
        Assert.False(first.Disposed);
        Assert.Equal("a", again.Name);
    }

    [Fact]
    public async Task Init_failure_rolls_back_registration_and_child()
    {
        var clients = new List<FakePiRpcClient>();
        var dir = Path.Combine(Path.GetTempPath(), $"piwebui-fail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await using var mgr = new SessionManager(() =>
            {
                // 1st spawn fails init (child explodes); a retry gets a healthy client.
                var n = clients.Count;
                var c = new FakePiRpcClient(n == 0
                    ? cmd => throw new InvalidOperationException("child exploded")
                    : cmd => new RpcResponse("ok", cmd.Type, true, null, null));
                clients.Add(c);
                return c;
            }, dir);

            await Assert.ThrowsAsync<InvalidOperationException>(() => mgr.InitAsync("a"));

            // a failed init is never reported as running and never lingers in the registry
            Assert.Null(mgr.Get("a"));
            Assert.Empty(mgr.List());
            Assert.True(clients[0].Disposed); // the failed child was cleaned up

            // a retry can create a fresh session from scratch
            var again = await mgr.InitAsync("a");
            Assert.True(again.IsRunning);
            Assert.Same(again, mgr.Get("a"));
            Assert.Equal(2, clients.Count); // fresh child, no stale state
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Resume_reuses_discovered_history_path_from_get_state()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"piwebui-disc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var discovered = Path.Combine(dir, "discovered", "session.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(discovered)!);
        var clients = new List<FakePiRpcClient>();
        try
        {
            // get_state reports a child-owned sessionFile (discovered, NOT the managed path)
            Func<RpcCommand, RpcResponse?> responder = cmd =>
                cmd is GetStateCommand
                    ? new RpcResponse("gs", "get_state", true, null,
                        JsonDocument.Parse($"{{\"sessionFile\":\"{discovered.Replace("\\", "/")}\"}}").RootElement)
                    : null;

            await using var mgr = new SessionManager(() =>
            {
                var c = new FakePiRpcClient(responder);
                clients.Add(c);
                return c;
            }, dir);

            var session = await mgr.InitAsync("a");
            // history path came from get_state (discovered), not the managed a.jsonl
            Assert.Equal(discovered, session.HistoryFilePath);
            var child1 = clients[0];

            File.WriteAllText(discovered, "{}\n"); // child produced history on that path
            await mgr.RecycleAsync("a");
            Assert.True(child1.Disposed);

            var resumed = await mgr.InitAsync("a"); // resume via the DISCOVERED path
            Assert.Same(session, resumed);
            Assert.Equal(2, clients.Count);
            var child2 = clients[1];
            var sw = Assert.Single(child2.Sent.OfType<SwitchSessionCommand>());
            Assert.Equal(discovered, sw.SessionPath);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Delete_removes_session_permanently_and_deletes_history()
    {
        using var h = new Harness();
        await h.Manager.InitAsync("a");
        var history = Path.Combine(h.Dir, "a.jsonl");
        File.WriteAllText(history, "{}\n");

        Assert.True(h.Clients[0].Started);
        await h.Manager.DeleteAsync("a");

        Assert.Null(h.Manager.Get("a"));
        Assert.DoesNotContain(h.Manager.List(), s => s.Name == "a");
        Assert.True(h.Clients[0].Disposed);      // child stopped
        Assert.False(File.Exists(history));      // history permanently removed
    }

    [Fact]
    public async Task Slow_session_does_not_block_another()
    {
        // two isolated children: session a is blocked (never completes), session b
        // answers immediately. Interacting with b must not wait on a.
        var slowTcs = new TaskCompletionSource<RpcResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fastTcs = new TaskCompletionSource<RpcResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fakeA = new FakePiRpcClient((cmd, ct) => cmd is PromptCommand ? slowTcs.Task : Task.FromResult<RpcResponse?>(null));
        var fakeB = new FakePiRpcClient((cmd, ct) => cmd is PromptCommand ? fastTcs.Task : Task.FromResult<RpcResponse?>(null));
        var queued = new Queue<IPiRpcClient>(new IPiRpcClient[] { fakeA, fakeB });

        var dir = Path.Combine(Path.GetTempPath(), $"piwebui-slow-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await using var mgr = new SessionManager(() => queued.Dequeue(), dir);
            await mgr.InitAsync("a");
            await mgr.InitAsync("b");

            var a = mgr.Get("a")!;
            var b = mgr.Get("b")!;

            var slowTask = a.PromptAsync("slow agent");   // a is blocked
            var fastTask = b.PromptAsync("fast agent");

            fastTcs.SetResult(new RpcResponse("f1", "prompt", true, null, null));
            var fastResp = await fastTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.NotNull(fastResp);
            Assert.True(fastResp!.Success);
            Assert.False(slowTask.IsCompleted); // a still pending — b was not blocked by it

            slowTcs.SetResult(new RpcResponse("s1", "prompt", true, null, null));
            Assert.NotNull(await slowTask.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}

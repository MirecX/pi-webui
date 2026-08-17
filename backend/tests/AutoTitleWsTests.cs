using System.Text.Json;
using PiWebui.Rpc;
using PiWebui.Session;
using PiWebui.Web;
using Xunit;

namespace PiWebui.Tests;

/// <summary>
/// Ticket #06 — auto-title at the WS boundary, backed by a stub title generator, plus the
/// resume / fork / clone frames reaching the attached session. Verifies a stub generator's
/// title is applied to the session and surfaced to the browser, a failing stub yields the
/// truncated-message fallback, and title generation NEVER delays the first turn. Fork/clone/
/// resume frames are covered here at the WS seam.
/// </summary>
public class AutoTitleWsTests
{
    private sealed class Harness : IDisposable
    {
        public string Dir;
        public List<FakePiRpcClient> Clients = new();
        public SessionManager Manager;

        public Harness()
        {
            Dir = Path.Combine(Path.GetTempPath(), $"piwebui-title-{Guid.NewGuid():N}");
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

    /// <summary>Scriptable ITitleGenerator for the non-blocking/fallback seams.</summary>
    private sealed class StubTitleGenerator : ITitleGenerator
    {
        private readonly Func<string, CancellationToken, Task<string?>> _impl;
        public StubTitleGenerator(string? result) : this((_, _) => Task.FromResult(result)) { }
        public StubTitleGenerator(Func<string, CancellationToken, Task<string?>> impl) => _impl = impl;
        public Task<string?> GenerateTitleAsync(string firstMessage, CancellationToken ct = default)
            => _impl(firstMessage, ct);
    }

    [Fact]
    public async Task Stub_generator_produces_title_applied_to_session_and_surfaces_to_browser()
    {
        using var h = new Harness();
        await h.Manager.InitAsync("a");
        var stub = new StubTitleGenerator("Casual greeting");
        var ws = new FakeWsClient();
        var bridge = new WsBridge(h.Manager, "a", ws, new SessionAutoTitler(stub));

        await bridge.HandleMessageAsync("{\"type\":\"prompt\",\"message\":\"Yo!\"}");

        // the stub's title lands on the session and reaches the browser as a title event
        await TestWait.UntilAsync(() => h.Manager.Get("a")!.Title == "Casual greeting");
        var frame = Assert.Single(ws.Sent, s => s.Contains("\"session_event\"") && s.Contains("\"title\""));
        Assert.Contains("Casual greeting", frame);
        // exactly once — a second prompt does not re-title
        await bridge.HandleMessageAsync("{\"type\":\"prompt\",\"message\":\"second\"}");
        Assert.Single(ws.Sent, s => s.Contains("\"session_event\"") && s.Contains("\"title\""));
    }

    [Fact]
    public async Task Failing_generator_yields_fallback_title()
    {
        using var h = new Harness();
        await h.Manager.InitAsync("a");
        var stub = new StubTitleGenerator((_, _) => Task.FromException<string?>(new InvalidOperationException("model unreachable")));
        var ws = new FakeWsClient();
        var bridge = new WsBridge(h.Manager, "a", ws, new SessionAutoTitler(stub));

        await bridge.HandleMessageAsync("{\"type\":\"prompt\",\"message\":\"Yo! How are you?\"}");

        // fallback = truncated first message + timestamp; never a crash
        await TestWait.UntilAsync(() => h.Manager.Get("a")!.Title is not null);
        var title = h.Manager.Get("a")!.Title!;
        Assert.StartsWith("Yo! How are you?", title);
        Assert.Contains("·", title);
    }

    [Fact]
    public async Task Null_generator_result_yields_fallback_title()
    {
        using var h = new Harness();
        await h.Manager.InitAsync("a");
        var stub = new StubTitleGenerator((string?)null); // generator returns nothing
        var ws = new FakeWsClient();
        var bridge = new WsBridge(h.Manager, "a", ws, new SessionAutoTitler(stub));

        await bridge.HandleMessageAsync("{\"type\":\"prompt\",\"message\":\"long message that is way more than forty characters long to force a truncation\"}");

        await TestWait.UntilAsync(() => h.Manager.Get("a")!.Title is not null);
        var title = h.Manager.Get("a")!.Title!;
        Assert.Contains("…", title); // truncated
        Assert.Contains("·", title); // timestamped
    }

    [Fact]
    public async Task Title_generation_never_delays_the_first_turn()
    {
        using var h = new Harness();
        await h.Manager.InitAsync("a");
        // a promise that never completes: title stays pending, but the prompt must NOT wait
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stub = new StubTitleGenerator((_, _) => pending.Task);
        var ws = new FakeWsClient();
        var bridge = new WsBridge(h.Manager, "a", ws, new SessionAutoTitler(stub));

        // HandleMessageAsync must complete (prompt accepted) without awaiting the title.
        var handle = bridge.HandleMessageAsync("{\"type\":\"prompt\",\"message\":\"fix the bug\"}");
        var completed = await Task.WhenAny(handle, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(handle, completed); // returned without blocking

        // the prompt was delivered to the child immediately, title still pending
        Assert.Single(h.Clients[0].Sent.OfType<PromptCommand>());
        Assert.Null(h.Manager.Get("a")!.Title);

        // even once the title resolves, the first-turn handling already returned
        pending.SetResult("Fix the bug");
        await Task.Delay(100);
        Assert.Equal("Fix the bug", h.Manager.Get("a")!.Title);
    }

    // --- resume / fork / clone over the WS boundary --------------------------

    [Fact]
    public async Task Get_fork_messages_fork_and_clone_frames_reach_attached_child()
    {
        using var h = new Harness();
        await h.Manager.InitAsync("a");
        var fake = h.Clients[0];
        var ws = new FakeWsClient();
        var bridge = new WsBridge(h.Manager, "a", ws);

        await bridge.HandleMessageAsync("{\"type\":\"get_fork_messages\"}");
        await bridge.HandleMessageAsync("{\"type\":\"fork\",\"entryId\":\"entry-7\"}");
        await bridge.HandleMessageAsync("{\"type\":\"clone\"}");

        var fork = Assert.Single(fake.Sent.OfType<ForkCommand>());
        Assert.Equal("entry-7", fork.EntryId);
        Assert.Single(fake.Sent.OfType<GetForkMessagesCommand>());
        Assert.Single(fake.Sent.OfType<CloneCommand>());
    }

    [Fact]
    public async Task Fork_result_relays_forking_message_text_to_browser()
    {
        var client = new FakePiRpcClient(cmd =>
        {
            if (cmd is GetForkMessagesCommand)
                return new RpcResponse("g1", "get_fork_messages", true, null,
                    JsonDocument.Parse("{\"messages\":[{\"entryId\":\"e1\",\"text\":\"first prompt\"}]}").RootElement);
            if (cmd is ForkCommand)
                return new RpcResponse("f1", "fork", true, null,
                    JsonDocument.Parse("{\"text\":\"first prompt\"}").RootElement);
            return null;
        });
        var dir = Path.Combine(Path.GetTempPath(), $"piwebui-forkws-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await using var mgr = new SessionManager(() => client, dir);
            await mgr.InitAsync("a");
            var ws = new FakeWsClient();
            var bridge = new WsBridge(mgr, "a", ws);

            await bridge.HandleMessageAsync("{\"type\":\"fork\",\"entryId\":\"e1\"}");

            var frame = Assert.Single(ws.Sent);
            Assert.Contains("\"type\":\"result\"", frame);
            Assert.Contains("\"target\":\"fork\"", frame);
            Assert.Contains("first prompt", frame); // forking message text surfaced
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Init_message_resumes_a_stored_session_via_switch_session()
    {
        using var h = new Harness();
        var history = Path.Combine(h.Dir, "old-work.jsonl");
        File.WriteAllText(history, "{}\n"); // stored, not loaded

        var ws = new FakeWsClient();
        var bridge = new WsBridge(h.Manager, "old-work", ws);
        await bridge.HandleMessageAsync("{\"type\":\"init\"}");

        var s = h.Manager.Get("old-work");
        Assert.NotNull(s);
        Assert.True(s!.IsRunning);
        // resume pointed the fresh child at the stored history file
        var sw = Assert.Single(h.Clients[0].Sent.OfType<SwitchSessionCommand>());
        Assert.Equal(history, sw.SessionPath);
    }
}

using System.Text.Json;
using PiWebui.Rpc;
using PiWebui.Session;
using PiWebui.Web;
using Xunit;

namespace PiWebui.Tests;

/// <summary>
/// Ticket #06 (code review) — behaviour gaps found in review:
///  1. clone duplicates into a NEW session file but the manager never discovered/registered
///     it, so the new branch was NOT listable/openable. Now CloneAndRegisterAsync discovers
///     the freshly created file (get_state after the clone re-binds the child) and registers
///     it as a STORED session.
///  2. fork is in-place (no new Session); a fork/clone of a recycled session must surface a
///     clean error rather than crash.
///  3. session titles were heap-only; they are now persisted (sidecar) so they survive a
///     server restart.
/// </summary>
public class CodeReviewTicket06Tests
{
    private sealed class Harness : IDisposable
    {
        public string Dir;
        public List<FakePiRpcClient> Clients = new();
        public SessionManager Manager;

        public Harness()
        {
            Dir = Path.Combine(Path.GetTempPath(), $"piwebui-review-{Guid.NewGuid():N}");
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

    private static void CleanupDir(string dir)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task Clone_yields_a_registered_listable_stored_session_and_is_resumable()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"piwebui-clonereg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var original = Path.Combine(dir, "orig.jsonl");
        var clone = Path.Combine(dir, "clone.jsonl");
        var cloned = false;
        var clients = new List<FakePiRpcClient>();
        // Stateful fake pi: before clone, get_state reports the ORIGINAL file; after clone the
        // child is re-bound to the NEW clone file (exactly what pi does per rpc.md clone).
        Func<RpcCommand, RpcResponse?> responder = cmd =>
        {
            if (cmd is CloneCommand)
            {
                cloned = true;
                return new RpcResponse("c1", "clone", true, null,
                    JsonDocument.Parse("{\"cancelled\":false}").RootElement);
            }
            if (cmd is GetStateCommand)
                return new RpcResponse("gs", "get_state", true, null,
                    JsonDocument.Parse($"{{\"sessionFile\":\"{(cloned ? clone : original).Replace("\\", "/")}\"}}").RootElement);
            return null;
        };
        try
        {
            await using (var mgr = new SessionManager(() =>
            {
                var c = new FakePiRpcClient(responder);
                clients.Add(c);
                return c;
            }, dir))
            {
                var session = await mgr.InitAsync("a");
                Assert.Equal(original, session.HistoryFilePath);

                var newName = await mgr.CloneAndRegisterAsync("a");
                Assert.Equal("clone", newName);

                // the clone is now LISTABLE as a stored session
                var summary = Assert.Single(mgr.ListStoredSessions(), s => s.Name == "clone");
                Assert.Equal("stored", summary.Status);

                // and RESUNABLE by name (a fresh child switch_sessions to the clone's file)
                File.WriteAllText(clone, "{}\n");
                var resumed = await mgr.InitAsync("clone");
                Assert.Equal(clone, resumed.HistoryFilePath);
                var sw = Assert.Single(clients[^1].Sent.OfType<SwitchSessionCommand>());
                Assert.Equal(clone, sw.SessionPath);
            }
        }
        finally
        {
            CleanupDir(dir);
        }
    }

    [Fact]
    public async Task Clone_over_ws_registers_stored_clone_and_surfaces_result()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"piwebui-clonews-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var original = Path.Combine(dir, "orig.jsonl");
        var clone = Path.Combine(dir, "clone.jsonl");
        var cloned = false;
        var client = new FakePiRpcClient(cmd =>
        {
            if (cmd is CloneCommand)
            {
                cloned = true;
                return new RpcResponse("c1", "clone", true, null,
                    JsonDocument.Parse("{\"cancelled\":false}").RootElement);
            }
            if (cmd is GetStateCommand)
                return new RpcResponse("gs", "get_state", true, null,
                    JsonDocument.Parse($"{{\"sessionFile\":\"{(cloned ? clone : original).Replace("\\", "/")}\"}}").RootElement);
            return null;
        });
        try
        {
            await using var mgr = new SessionManager(() => client, dir);
            await mgr.InitAsync("a");
            var ws = new FakeWsClient();
            var bridge = new WsBridge(mgr, "a", ws);

            await bridge.HandleMessageAsync("{\"type\":\"clone\"}");

            var frame = Assert.Single(ws.Sent, s => s.Contains("\"type\":\"result\""));
            Assert.Contains("\"target\":\"clone\"", frame);
            Assert.Contains("clone", frame);
            Assert.Single(mgr.ListStoredSessions(), s => s.Name == "clone" && s.Status == "stored");
        }
        finally
        {
            CleanupDir(dir);
        }
    }

    [Fact]
    public async Task Title_set_in_one_run_is_present_for_stored_session_after_reload()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"piwebui-titleper-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // run 1 — set a title, which must be persisted to the sidecar
            await using (var mgr = new SessionManager(() => new FakePiRpcClient(), dir))
            {
                await mgr.InitAsync("a");
                File.WriteAllText(Path.Combine(dir, "a.jsonl"), "{}\n"); // becomes a stored session later
                mgr.SetTitle("a", "Casual greeting");
                Assert.Equal("Casual greeting", mgr.Get("a")!.Title);
                Assert.True(File.Exists(Path.Combine(dir, "titles.json"))); // sidecar written
            }

            // run 2 — a FRESH manager over the same dir simulates a server restart
            await using (var mgr2 = new SessionManager(() => new FakePiRpcClient(), dir))
            {
                var summary = Assert.Single(mgr2.ListStoredSessions(), s => s.Name == "a");
                Assert.Equal("stored", summary.Status);
                Assert.Equal("Casual greeting", summary.Title); // title survived the restart
            }
        }
        finally
        {
            CleanupDir(dir);
        }
    }

    [Fact]
    public async Task Fork_and_clone_on_recycled_session_surface_clean_error_over_ws()
    {
        using var h = new Harness();
        await h.Manager.InitAsync("a");
        await h.Manager.RecycleAsync("a"); // loaded but NOT running

        var ws = new FakeWsClient();
        var bridge = new WsBridge(h.Manager, "a", ws);

        // neither may crash; both surface a clean not-running error
        await bridge.HandleMessageAsync("{\"type\":\"fork\",\"entryId\":\"e1\"}");
        await bridge.HandleMessageAsync("{\"type\":\"clone\"}");

        Assert.Equal(2, ws.Sent.Count);
        Assert.All(ws.Sent, f => Assert.Contains("\"type\":\"error\"", f));
    }
}

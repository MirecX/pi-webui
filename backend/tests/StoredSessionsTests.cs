using PiWebui.Rpc;
using PiWebui.Session;
using Xunit;

namespace PiWebui.Tests;

/// <summary>
/// Ticket #06 — the session browser registry: stored (not-loaded) sessions are listable and
/// resumable. A stored session is a .jsonl history file under the sessions dir with no live
/// loaded <see cref="Session"/>; it is listed as "stored" and resumes (via switch_session)
/// when re-initialised by name.
/// </summary>
public class StoredSessionsTests
{
    private sealed class Harness : IDisposable
    {
        public string Dir;
        public List<FakePiRpcClient> Clients = new();
        public SessionManager Manager;

        public Harness()
        {
            Dir = Path.Combine(Path.GetTempPath(), $"piwebui-stored-{Guid.NewGuid():N}");
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
    public void ListStoredSessions_includes_loaded_running_recycled_and_not_loaded_files()
    {
        using var h = new Harness();

        // a stored-but-not-loaded history file under the sessions dir
        var storedPath = Path.Combine(h.Dir, "old-work.jsonl");
        File.WriteAllText(storedPath, "{}\n");

        var summaries = h.Manager.ListStoredSessions();

        var stored = Assert.Single(summaries, s => s.Name == "old-work");
        Assert.Equal("stored", stored.Status);
        Assert.Null(stored.Title);
    }

    [Fact]
    public async Task ListStoredSessions_includes_running_and_recycled_loaded_sessions_with_title()
    {
        using var h = new Harness();
        var running = await h.Manager.InitAsync("a");
        running.Title = "Casual greeting";

        var recycled = await h.Manager.InitAsync("b");
        await h.Manager.RecycleAsync("b");

        var summaries = h.Manager.ListStoredSessions();
        var a = Assert.Single(summaries, s => s.Name == "a");
        Assert.Equal("running", a.Status);
        Assert.Equal("Casual greeting", a.Title);
        var b = Assert.Single(summaries, s => s.Name == "b");
        Assert.Equal("recycled", b.Status);
    }

    [Fact]
    public async Task Resume_a_stored_session_spawns_fresh_child_and_switch_sessions_to_the_file()
    {
        using var h = new Harness();
        var historyPath = Path.Combine(h.Dir, "old-work.jsonl");
        File.WriteAllText(historyPath, "{}\n"); // non-empty preserved history

        // init on the stored name: NOT loaded yet -> creates + resumes from the stored file
        var resumed = await h.Manager.InitAsync("old-work");

        Assert.True(resumed.IsRunning);
        Assert.Equal(SessionStatus.Running, resumed.Status);
        Assert.Equal(historyPath, resumed.HistoryFilePath);
        var child = Assert.Single(h.Clients);
        // pointing the fresh child at the stored history preserves the branch
        var sw = Assert.Single(child.Sent.OfType<SwitchSessionCommand>());
        Assert.Equal(historyPath, sw.SessionPath);

        // now it is loaded and no longer listed twice (not "stored")
        var summary = Assert.Single(h.Manager.ListStoredSessions(), s => s.Name == "old-work");
        Assert.Equal("running", summary.Status);
    }
}

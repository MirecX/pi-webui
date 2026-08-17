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

    [Fact]
    public async Task Shared_pi_sessions_are_listed_most_recent_first_and_resumable()
    {
        using var h = new Harness();
        var shared = Path.Combine(Path.GetTempPath(), $"piwebui-shared-{Guid.NewGuid():N}");
        try
        {
            // Simulate the standard pi sessions dir structure: a cwd-slug subdir with files.
            var slug = Path.Combine(shared, "--home-user-myproj--");
            Directory.CreateDirectory(slug);
            var oldPath = Path.Combine(slug, "2026-08-17T10-00-00-000Z_old.jsonl");
            var newPath = Path.Combine(slug, "2026-08-17T12-00-00-000Z_new.jsonl");
            // a real session file: JSONL whose first entry is a user message -> derived title
            File.WriteAllText(oldPath,
                "{\"type\":\"user\",\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"Old cardboard session\"}]}\n");
            File.WriteAllText(newPath,
                "{\"type\":\"user\",\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"Refactor the auth module to use tokens\"}]}\n");
            File.SetLastWriteTimeUtc(oldPath, DateTime.UtcNow.AddHours(-2));
            File.SetLastWriteTimeUtc(newPath, DateTime.UtcNow);

            var clients = new List<FakePiRpcClient>();
            await using var manager = new SessionManager(
                () => { var f = new FakePiRpcClient(); clients.Add(f); return f; }, h.Dir, shared);

            var list = manager.ListStoredSessions().ToList();

            // both TUI sessions are browsable as "stored"; the displayed title is derived
            // on the fly from the first user message (not the ugly timestamp_uuid stem)
            var newest = Assert.Single(list, s => s.Name == "2026-08-17T12-00-00-000Z_new" && s.Status == "stored");
            Assert.Equal("Refactor the auth module to use tokens", newest.Title);
            var oldest = Assert.Single(list, s => s.Name == "2026-08-17T10-00-00-000Z_old" && s.Status == "stored");
            Assert.Equal("Old cardboard session", oldest.Title);
            // most recent on top
            Assert.Equal("2026-08-17T12-00-00-000Z_new", list.First().Name);

            // resumable by name -> fresh child switch_sessions to the shared file
            var resumed = await manager.InitAsync("2026-08-17T12-00-00-000Z_new");
            Assert.True(resumed.IsRunning);
            Assert.Equal(newPath, resumed.HistoryFilePath);
            var child = Assert.Single(clients);
            var sw = Assert.Single(child.Sent.OfType<SwitchSessionCommand>());
            Assert.Equal(newPath, sw.SessionPath);
        }
        finally
        {
            if (Directory.Exists(shared)) Directory.Delete(shared, recursive: true);
        }
    }
}

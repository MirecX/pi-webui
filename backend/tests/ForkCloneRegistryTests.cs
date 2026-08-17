using System.Text.Json;
using PiWebui.Rpc;
using PiWebui.Session;
using Xunit;

namespace PiWebui.Tests;

/// <summary>
/// Ticket #06 — fork / clone at the session-manager seam, backed by the fake pi.
/// Verifies each branch command forwards to the attached session's child with the exact
/// rpc.md wire shape, that fork returns the forking message text, and that operations on a
/// recycled session surface a clean not-running error.
/// </summary>
public class ForkCloneRegistryTests
{
    private sealed class Harness : IDisposable
    {
        public string Dir;
        public List<FakePiRpcClient> Clients = new();
        public SessionManager Manager;

        public Harness()
        {
            Dir = Path.Combine(Path.GetTempPath(), $"piwebui-fork-{Guid.NewGuid():N}");
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
    public async Task Fork_forwards_command_with_entryId_and_returns_forking_message()
    {
        var client = new FakePiRpcClient(cmd =>
            cmd is ForkCommand
                ? new RpcResponse("f1", "fork", true, null,
                    JsonDocument.Parse("{\"text\":\"The original prompt text...\"}").RootElement)
                : null);
        var dir = Path.Combine(Path.GetTempPath(), $"piwebui-forkresp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await using var mgr = new SessionManager(() => client, dir);
            var session = await mgr.InitAsync("a");

            var resp = await session.ForkAsync("abc123");

            // forwarded to the child with the exact rpc.md wire field
            var cmd = Assert.Single(client.Sent.OfType<ForkCommand>());
            Assert.Equal("abc123", cmd.EntryId);
            Assert.Contains("\"type\":\"fork\"", cmd.ToJson());
            Assert.Contains("\"entryId\":\"abc123\"", cmd.ToJson());

            // fork returns the forking message text per rpc.md
            Assert.NotNull(resp);
            Assert.True(resp!.Success);
            Assert.Equal("The original prompt text...", resp.Data!.Value.GetProperty("text").GetString());
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Clone_forwards_clone_command()
    {
        using var h = new Harness();
        var session = await h.Manager.InitAsync("a");

        await session.CloneAsync();

        var cmd = Assert.Single(h.Clients[0].Sent.OfType<CloneCommand>());
        Assert.Contains("\"type\":\"clone\"", cmd.ToJson());
    }

    [Fact]
    public async Task Get_fork_messages_forwards_command_and_returns_list()
    {
        var client = new FakePiRpcClient(cmd =>
            cmd is GetForkMessagesCommand
                ? new RpcResponse("g1", "get_fork_messages", true, null,
                    JsonDocument.Parse(
                        "{\"messages\":[{\"entryId\":\"abc\",\"text\":\"first\"},{\"entryId\":\"def\",\"text\":\"second\"}]}").RootElement)
                : null);
        var dir = Path.Combine(Path.GetTempPath(), $"piwebui-forkmsg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await using var mgr = new SessionManager(() => client, dir);
            var session = await mgr.InitAsync("a");

            var resp = await session.GetForkMessagesAsync();

            Assert.Single(client.Sent.OfType<GetForkMessagesCommand>());
            var messages = resp!.Data!.Value.GetProperty("messages");
            Assert.Equal(2, messages.GetArrayLength());
            Assert.Equal("abc", messages[0].GetProperty("entryId").GetString());
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Fork_clone_on_recycled_session_throws_not_running()
    {
        using var h = new Harness();
        var session = await h.Manager.InitAsync("a");
        await h.Manager.RecycleAsync("a");

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ForkAsync("x"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CloneAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.GetForkMessagesAsync());
    }
}

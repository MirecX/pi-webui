using System.Threading.Channels;
using PiWebui.Rpc;

namespace PiWebui.Session;

public enum SessionStatus
{
    /// <summary>A live pi child is running for this session.</summary>
    Running,

    /// <summary>The child was recycled; history is preserved and the session is resumable.</summary>
    Recycled,
}

/// <summary>
/// ONE named session (ticket #05). Owns a single <see cref="IPiRpcClient"/> (a
/// <c>pi --mode rpc</c> child), fans its events out to subscribers, and forwards
/// prompt commands to its own child. Sessions do NOT share a client or a lock, so
/// a slow agent in one session never blocks another.
///
/// Lifecycle is owned by <see cref="SessionManager"/>: it attaches a fresh child on
/// init/resume and detaches (recycles) it on shutdown/delete. The session itself
/// only manages the current client and the live event fan-out.
/// </summary>
public sealed class Session : IAsyncDisposable
{
    private readonly FanOut<RpcEvent> _events = new();
    private IPiRpcClient? _client;

    public Session(string name) => Name = name;

    /// <summary>Stable name used to address this session.</summary>
    public string Name { get; }

    /// <summary>
    /// Absolute path of this session's history file. Preserved across recycle so a
    /// fresh child can resume it via <c>switch_session</c>; removed on delete.
    /// </summary>
    public string? HistoryFilePath { get; set; }

    /// <summary>True while a live pi child is running for this session.</summary>
    public bool IsRunning => _client is not null;

    public SessionStatus Status => IsRunning ? SessionStatus.Running : SessionStatus.Recycled;

    /// <summary>The live child, or null while recycled/stopped.</summary>
    public IPiRpcClient? Client => _client;

    /// <summary>Subscribe to this session's live event stream (not replayable).</summary>
    public Channel<RpcEvent> Subscribe() => _events.Subscribe();

    public void Unsubscribe(Channel<RpcEvent> ch) => _events.Unsubscribe(ch);

    /// <summary>Bind a live child to this session and begin relaying its events.</summary>
    internal void AttachClient(IPiRpcClient client)
    {
        _client = client;
        client.EventReceived += OnEvent;
    }

    /// <summary>Unbind the current child (used on recycle) without dispatching events.</summary>
    internal void DetachClient()
    {
        var c = _client;
        if (c is null) return;
        c.EventReceived -= OnEvent;
        _client = null;
    }

    private void OnEvent(RpcEvent ev) => _events.Publish(ev);

    /// <summary>
    /// Send a prompt to this session's own child and await its acceptance response.
    /// When <paramref name="streamingBehavior"/> is set (e.g. "steer") the prompt is
    /// queued for delivery to an already-running agent instead of being rejected.
    /// </summary>
    public Task<RpcResponse?> PromptAsync(string message, string? streamingBehavior = null, CancellationToken ct = default)
        => SendCommand(() => new PromptCommand(message, streamingBehavior), "prompt", ct);

    /// <summary>Queue a steering message (delivered before the agent's next LLM call).</summary>
    public Task<RpcResponse?> SteerAsync(string message, CancellationToken ct = default)
        => SendCommand(() => new SteerCommand(message), "steer", ct);

    /// <summary>Queue a follow-up message (delivered after the agent settles).</summary>
    public Task<RpcResponse?> FollowUpAsync(string message, CancellationToken ct = default)
        => SendCommand(() => new FollowUpCommand(message), "follow_up", ct);

    /// <summary>Abort the currently in-flight turn.</summary>
    public Task<RpcResponse?> AbortAsync(CancellationToken ct = default)
        => SendCommand(() => new AbortCommand(), "abort", ct);

    /// <summary>
    /// Send a single command to this session's own child and await its correlated
    /// response. Shared by all turn-control methods so their error handling stays
    /// identical to <c>prompt</c> (genuine errors surfaced, expected teardown quiet).
    /// </summary>
    private async Task<RpcResponse?> SendCommand(Func<RpcCommand> build, string action, CancellationToken ct)
    {
        var client = _client
            ?? throw new InvalidOperationException($"session '{Name}' is not running; initialize it first");
        try
        {
            return await client.SendAsync(build(), ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The child was disposed mid-write (a recycle raced this command). Surface it
            // as an ordinary not-running error instead of letting it tear down the WS loop.
            throw new InvalidOperationException(
                $"session '{Name}' was recycled mid-{action}; re-initialize it to continue");
        }
    }

    /// <summary>Stop the child (recycle). Preserves history; the session stays resumable.</summary>
    public async ValueTask RecycleAsync()
    {
        var c = _client;
        DetachClient();
        if (c is not null)
        {
            try { await c.DisposeAsync().ConfigureAwait(false); }
            catch { /* best-effort child shutdown */ }
        }
    }

    public async ValueTask DisposeAsync() => await RecycleAsync().ConfigureAwait(false);
}

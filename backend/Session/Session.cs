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
    /// Auto/manual display title (ticket #06). Populated from the session's first user
    /// message by the title generator; <c>null</c> until titled. Survives recycle so a
    /// resumed session keeps its scannable label.
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// True while this fresh session has not yet been auto-titled from its first user
    /// message. Set <c>true</c> only for brand-new (no-history) sessions; cleared once a
    /// title is requested so the generation fires exactly once. Sessions resumed from a
    /// preserved history (already titled / not first-turn) do not re-title.
    /// </summary>
    internal bool AutoTitlePending { get; set; }

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

    /// <summary>List all configured models on this session's own child (ticket #04).</summary>
    public Task<RpcResponse?> GetAvailableModelsAsync(CancellationToken ct = default)
        => SendCommand(() => new GetAvailableModelsCommand(), "get_available_models", ct);

    /// <summary>Switch this session to a specific model (per rpc.md: provider + modelId).</summary>
    public Task<RpcResponse?> SetModelAsync(string provider, string modelId, CancellationToken ct = default)
        => SendCommand(() => new SetModelCommand(provider, modelId), "set_model", ct);

    /// <summary>
    /// Fetch this session's current state (rpc.md <c>get_state</c>), which exposes the
    /// actual current <c>model</c> and <c>thinkingLevel</c> so the UI can reflect the
    /// attached session's real selection across reconnect/tab-switch (ticket #04).
    /// </summary>
    public Task<RpcResponse?> GetStateAsync(CancellationToken ct = default)
        => SendCommand(() => new GetStateCommand(), "get_state", ct);

    /// <summary>Fork the session from a previous user message (rpc.md <c>fork</c>; data.text = the forking message).</summary>
    public Task<RpcResponse?> ForkAsync(string entryId, CancellationToken ct = default)
        => SendCommand(() => new ForkCommand(entryId), "fork", ct);

    /// <summary>Clone the active branch into a new session at the current position (rpc.md <c>clone</c>).</summary>
    public Task<RpcResponse?> CloneAsync(CancellationToken ct = default)
        => SendCommand(() => new CloneCommand(), "clone", ct);

    /// <summary>List the user messages available for forking (rpc.md <c>get_fork_messages</c>).</summary>
    public Task<RpcResponse?> GetForkMessagesAsync(CancellationToken ct = default)
        => SendCommand(() => new GetForkMessagesCommand(), "get_fork_messages", ct);

    /// <summary>List the thinking levels supported by this session's current model.</summary>
    public Task<RpcResponse?> GetAvailableThinkingLevelsAsync(CancellationToken ct = default)
        => SendCommand(() => new GetAvailableThinkingLevelsCommand(), "get_available_thinking_levels", ct);

    /// <summary>Set this session's thinking/reasoning level.</summary>
    public Task<RpcResponse?> SetThinkingLevelAsync(string level, CancellationToken ct = default)
        => SendCommand(() => new SetThinkingLevelCommand(level), "set_thinking_level", ct);

    // --- Compaction / retry / state / export (ticket #08) --- per rpc.md ---

    /// <summary>Manually compact this session's conversation context (rpc.md <c>compact</c>).</summary>
    public Task<RpcResponse?> CompactAsync(CancellationToken ct = default)
        => SendCommand(() => new CompactCommand(), "compact", ct);

    /// <summary>Toggle automatic compaction (rpc.md <c>set_auto_compaction</c>, param <c>enabled</c>).</summary>
    public Task<RpcResponse?> SetAutoCompactionAsync(bool enabled, CancellationToken ct = default)
        => SendCommand(() => new SetAutoCompactionCommand(enabled), "set_auto_compaction", ct);

    /// <summary>Toggle automatic retry on transient errors (rpc.md <c>set_auto_retry</c>, param <c>enabled</c>).</summary>
    public Task<RpcResponse?> SetAutoRetryAsync(bool enabled, CancellationToken ct = default)
        => SendCommand(() => new SetAutoRetryCommand(enabled), "set_auto_retry", ct);

    /// <summary>Fetch token/cost/context usage stats (rpc.md <c>get_session_stats</c>).</summary>
    public Task<RpcResponse?> GetSessionStatsAsync(CancellationToken ct = default)
        => SendCommand(() => new GetSessionStatsCommand(), "get_session_stats", ct);

    /// <summary>Export this session to an HTML transcript (rpc.md <c>export_html</c>; data.path = generated file).</summary>
    public Task<RpcResponse?> ExportHtmlAsync(string? outputPath = null, CancellationToken ct = default)
        => SendCommand(() => new ExportHtmlCommand(outputPath), "export_html", ct);

    /// <summary>Get this session as a tree of entries for the structure panel (rpc.md <c>get_tree</c>).</summary>
    public Task<RpcResponse?> GetTreeAsync(CancellationToken ct = default)
        => SendCommand(() => new GetTreeCommand(), "get_tree", ct);

    /// <summary>Get all messages in this session for history replay (rpc.md <c>get_messages</c>).</summary>
    public Task<RpcResponse?> GetMessagesAsync(CancellationToken ct = default)
        => SendCommand(() => new GetMessagesCommand(), "get_messages", ct);

    /// <summary>
    /// Send a HITL dialog answer back to this session's child as an
    /// <c>extension_ui_response</c> (ticket #07, rpc.md). Fire-and-forget: pi sends no
    /// correlated response, so this writes only and never awaits. <c>value</c> answers
    /// select/input/editor, <c>confirmed</c> answers confirm, <c>cancelled</c> dismisses
    /// any dialog.
    /// </summary>
    public Task RespondHitlAsync(string requestId, string? value = null, bool? confirmed = null, bool cancelled = false, CancellationToken ct = default)
        // Routes through SendCommandCore so the guard + ObjectDisposed ladder stays identical to
        // every other command (they can't drift). Fire-and-forget: pi sends no correlated response,
        // so this writes only and never awaits.
        => SendCommandCore(() => new ExtensionUiResponseCommand(requestId, value, confirmed, cancelled), "hitl_response", fireAndForget: true, ct);

    /// <summary>
    /// Send a single command to this session's own child and await its correlated
    /// response. Shared by all turn-control methods so their error handling stays
    /// identical to <c>prompt</c> (genuine errors surfaced, expected teardown quiet).
    /// </summary>
    private Task<RpcResponse?> SendCommand(Func<RpcCommand> build, string action, CancellationToken ct)
        => SendCommandCore(build, action, fireAndForget: false, ct);

    /// <summary>
    /// Shared command core for every outbound command: the not-running guard, the write, and
    /// the ObjectDisposed → InvalidOperation teardown ladder. Correlated sends (via
    /// <see cref="SendAsync"/>) and fire-and-forget sends (via <see cref="SendFireAndForgetAsync"/>,
    /// e.g. HITL <c>extension_ui_response</c>) route through here so the two ladders don't drift.
    /// </summary>
    private async Task<RpcResponse?> SendCommandCore(Func<RpcCommand> build, string action, bool fireAndForget, CancellationToken ct)
    {
        var client = _client
            ?? throw new InvalidOperationException($"session '{Name}' is not running; initialize it first");
        try
        {
            if (fireAndForget)
            {
                await client.SendFireAndForgetAsync(build(), ct).ConfigureAwait(false);
                return null;
            }
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

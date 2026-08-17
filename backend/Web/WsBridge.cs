using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using PiWebui.Rpc;
using PiWebui.Session;

namespace PiWebui.Web;

/// <summary>
/// Bridges a WebSocket client to ONE named session (ticket #05). Forwards that
/// session's RPC events to the browser and turns client messages into commands on
/// the session. Supports lifecycle control messages (init/recycle/delete) so the UI
/// can create, stop, and remove sessions and list them over the same channel.
///
/// A tab attaches to a session via <c>/ws?session=&lt;name&gt;</c>. Prompt commands
/// are always scoped to the attached session; lifecycle messages may carry an
/// optional <c>name</c> to act on another session (for the sidebar).
///
/// Live-stream semantics are preserved: events published before a client attaches
/// are not replayed (the per-session <see cref="FanOut{T}"/> is not replayable).
/// </summary>
public sealed class WsBridge
{
    private readonly SessionManager _sessions;
    private readonly string _sessionName;
    private readonly IWsClient _client;

    private readonly object _subLock = new();
    private Channel<RpcEvent>? _sub;

    /// <summary>
    /// Live agent state observed from the attached session's event stream. Drives the
    /// inbound turn-control guards (see <see cref="TrackAgentState"/>).
    /// </summary>
    private bool _agentRunning;
    private bool _hasSettled;

    /// <summary>True once the attached agent has started and not yet settled.</summary>
    internal bool AgentRunning => _agentRunning;

    /// <summary>True after the attached agent has emitted at least one terminal settle.</summary>
    internal bool HasSettled => _hasSettled;

    public WsBridge(SessionManager sessions, string sessionName, IWsClient client)
    {
        _sessions = sessions;
        _sessionName = sessionName;
        _client = client;
        EnsureSubscribed();
    }

    /// <summary>Run both the forward and inbound loops until either ends.</summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var forward = Task.Run(() => ForwardLoopAsync(linked.Token), CancellationToken.None);
        var inbound = Task.Run(() => InboundLoopAsync(linked.Token), CancellationToken.None);

        await Task.WhenAny(forward, inbound);
        linked.Cancel();

        try { await Task.WhenAll(forward, inbound); }
        catch (OperationCanceledException) { /* expected on shutdown */ }
        catch { /* best-effort */ }

        lock (_subLock)
        {
            if (_sub is not null)
            {
                var s = _sessions.Get(_sessionName);
                s?.Unsubscribe(_sub);
                _sub = null;
            }
        }
        await _client.CloseAsync();
    }

    /// <summary>
    /// Forward every event on the attached session's stream to the browser as raw
    /// JSON text. If the session has not been initialized yet, waits (idle) until an
    /// init creates it, then attaches. Not replayable.
    /// </summary>
    public async Task ForwardLoopAsync(CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            Channel<RpcEvent>? ch;
            lock (_subLock) ch = _sub;
            if (ch is null)
            {
                // attach is asynchronous (session may be created by a later init);
                // poll until it exists or we're cancelled.
                try { await Task.Delay(50, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                EnsureSubscribed();
                continue;
            }

            await foreach (var ev in ch.Reader.ReadAllAsync(ct))
            {
                TrackAgentState(ev);
                await _client.SendAsync(ev.Raw, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Handle one inbound JSON message from the browser.</summary>
    public async Task HandleMessageAsync(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("type", out var typeProp)) return;
        var type = typeProp.GetString();
        if (type is null) return;

        switch (type)
        {
            case "prompt":
                // prompts are always scoped to the attached session
                if (root.TryGetProperty("message", out var msgProp) && msgProp.GetString() is { } message)
                {
                    string? frameSb = null;
                    if (root.TryGetProperty("streamingBehavior", out var sbProp) && sbProp.ValueKind == JsonValueKind.String)
                        frameSb = sbProp.GetString();
                    await HandlePromptAsync(message, frameSb).ConfigureAwait(false);
                }
                break;

            case "steer":
            case "follow_up":
                if (root.TryGetProperty("message", out var tpProp) && tpProp.GetString() is { } tMsg)
                {
                    // rpc.md: steer is valid only while the agent is running. Rather than
                    // forward a steer pi would silently reject, surface a clear error when
                    // we know the attached agent is idle. follow_up is valid regardless.
                    if (type == "steer" && !_agentRunning && _hasSettled)
                    {
                        await SendErrorAsync("agent is not running; steer is only valid while the agent is running").ConfigureAwait(false);
                        break;
                    }
                    var send = type == "steer"
                        ? (Func<PiWebui.Session.Session, Task<RpcResponse?>>)(s => s.SteerAsync(tMsg))
                        : s => s.FollowUpAsync(tMsg);
                    await DispatchTurnAsync(type, send).ConfigureAwait(false);
                }
                break;

            case "abort":
                await DispatchTurnAsync("abort", s => s.AbortAsync()).ConfigureAwait(false);
                break;

            // --- model + thinking switch (ticket #04) -------------------------
            // All scoped to the ATTACHED session; results relayed back to the browser
            // as a `result` frame carrying the RPC response `data` (model/level lists
            // and confirmation of the applied selection).
            case "models":
                await DispatchModelCommandAsync("models", s => s.GetAvailableModelsAsync()).ConfigureAwait(false);
                break;
            case "set_model":
                if (root.TryGetProperty("provider", out var provProp) && provProp.GetString() is { } provider
                    && root.TryGetProperty("modelId", out var midProp) && midProp.GetString() is { } modelId)
                {
                    await DispatchModelCommandAsync("set_model", s => s.SetModelAsync(provider, modelId)).ConfigureAwait(false);
                }
                break;
            case "thinking_levels":
                await DispatchModelCommandAsync("thinking_levels", s => s.GetAvailableThinkingLevelsAsync()).ConfigureAwait(false);
                break;
            case "set_thinking_level":
                if (root.TryGetProperty("level", out var lvlProp) && lvlProp.GetString() is { } level)
                {
                    await DispatchModelCommandAsync("set_thinking_level", s => s.SetThinkingLevelAsync(level)).ConfigureAwait(false);
                }
                break;

            case "init":
            case "recycle":
            case "delete":
                var name = root.TryGetProperty("name", out var n) && n.GetString() is { } nn
                    ? nn : _sessionName;
                await HandleLifecycleAsync(type, name).ConfigureAwait(false);
                break;

            default:
                break; // unknown control types are ignored
        }
    }

    private async Task HandlePromptAsync(string message, string? frameStreamingBehavior = null)
    {
        // rpc.md: a prompt sent while the agent is streaming REQUIRES a streamingBehavior
        // or it is rejected. If the frame didn't specify one but the attached agent is
        // running, default to "steer" so the composer's question queues for delivery before
        // the next LLM call. When the agent is idle no streamingBehavior is needed.
        var streamingBehavior = frameStreamingBehavior ?? (_agentRunning ? "steer" : null);
        await DispatchTurnAsync("prompt", s => s.PromptAsync(message, streamingBehavior)).ConfigureAwait(false);
    }

    /// <summary>
    /// Track the attached agent's live running state from relayed events so inbound
    /// turn-controls can guard against invalid states (e.g. steering while idle).
    /// </summary>
    private void TrackAgentState(RpcEvent ev)
    {
        switch (ev.Type)
        {
            case "agent_start":
                _agentRunning = true;
                _hasSettled = false;
                break;
            case "agent_settled":
                _agentRunning = false;
                _hasSettled = true;
                break;
            // agent_end: a low-level run finished but the agent may still retry, compact,
            // or continue with queued follow-ups, so it stays "running" until settled.
        }
    }

    /// <summary>
    /// Run one turn-control command (prompt/steer/follow_up/abort) against the attached
    /// session's child. Shared error handling keeps every turn command consistent:
    /// genuine failures surface to the browser; expected teardown stays quiet.
    /// </summary>
    private async Task DispatchTurnAsync(string action, Func<PiWebui.Session.Session, Task<RpcResponse?>> send)
    {
        var s = _sessions.Get(_sessionName);
        if (s is null || !s.IsRunning)
        {
            await SendErrorAsync($"session '{_sessionName}' is not running; initialize it first").ConfigureAwait(false);
            return;
        }

        try
        {
            var resp = await send(s).ConfigureAwait(false);
            // A correlated, non-success response means the command was rejected
            // server-side; surface it so a rejection isn't silent.
            if (resp is { Success: false })
                await SendErrorAsync($"{action} rejected: {resp.Error ?? "unknown error"}").ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            await SendErrorAsync(ex.Message).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A pending command was cancelled. If the session is STILL live this is a
            // genuine per-command failure (e.g. the child was killed mid-command) and the
            // browser must hear about it rather than have it silently dropped. If the
            // session is no longer running the command was cancelled by an explicit
            // recycle/delete teardown, which the lifecycle event already covers.
            if (s.IsRunning)
                await SendErrorAsync($"{action} cancelled; the session stopped before it could respond").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Any other genuine per-command failure: surface it instead of dropping it.
            await SendErrorAsync($"{action} failed: {ex.Message}").ConfigureAwait(false);
        }
    }

    private async Task HandleLifecycleAsync(string type, string name)
    {
        switch (type)
        {
            case "init":
                await _sessions.InitAsync(name).ConfigureAwait(false);
                EnsureSubscribed(); // our attached session may now exist
                break;
            case "recycle":
                await _sessions.RecycleAsync(name).ConfigureAwait(false);
                break;
            case "delete":
                await _sessions.DeleteAsync(name).ConfigureAwait(false);
                break;
        }
        await SendSessionEventAsync(type, name).ConfigureAwait(false);
        if (type == "delete" && name == _sessionName)
        {
            // We deleted the session we're attached to. Deliver the "deleted"
            // session_event FIRST on the still-open channel so the browser learns the
            // session is gone and stops reconnecting, THEN close the tab connection.
            // Closing first would drop the frame on the real transport (sends after
            // close are lost) and leave the frontend looping on the dead session.
            await _client.CloseAsync().ConfigureAwait(false);
        }
    }

    private async Task SendSessionEventAsync(string action, string name)
    {
        try
        {
            var s = _sessions.Get(name);
            string status = s?.Status switch
            {
                SessionStatus.Running => "running",
                _ => s is null ? "deleted" : "recycled",
            };
            await _client.SendAsync(JsonSerializer.Serialize(new
            {
                type = "session_event",
                action,
                session = new { name, status },
            }), default).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort: the client may already be closed (e.g. deleting the session
            // we're attached to closed the connection first). Never throw out of the
            // inbound handler.
        }
    }

    /// <summary>
    /// Run one model/thinking command (models/set_model/thinking_levels/set_thinking_level)
    /// against the attached session's child. Shares the turn-control error convention:
    /// genuine failures surface to the browser; expected teardown stays quiet. On success
    /// the RPC response <c>data</c> is relayed back to the browser as a <c>result</c> frame
    /// so it can populate the available lists and reflect the applied selection.
    /// </summary>
    private async Task DispatchModelCommandAsync(string action, Func<PiWebui.Session.Session, Task<RpcResponse?>> send)
    {
        var s = _sessions.Get(_sessionName);
        if (s is null || !s.IsRunning)
        {
            await SendErrorAsync($"session '{_sessionName}' is not running; initialize it first").ConfigureAwait(false);
            return;
        }

        try
        {
            var resp = await send(s).ConfigureAwait(false);
            if (resp is { Success: false })
            {
                await SendErrorAsync($"{action} rejected: {resp.Error ?? "unknown error"}").ConfigureAwait(false);
                return;
            }
            await SendResultAsync(action, resp?.DataJson).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            await SendErrorAsync(ex.Message).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (s.IsRunning)
                await SendErrorAsync($"{action} cancelled; the session stopped before it could respond").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await SendErrorAsync($"{action} failed: {ex.Message}").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Push a <c>result</c> frame to the browser: <c>{ "type": "result", "target": action, "data": &lt;raw rpc data&gt; }</c>.
    /// The raw RPC response <c>data</c> JSON object is embedded verbatim so the browser receives
    /// the exact model/level lists and applied-selection confirmation pi returned.
    /// </summary>
    private async Task SendResultAsync(string action, string? dataJson)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("type", "result");
            w.WriteString("target", action);
            if (dataJson is not null)
            {
                w.WritePropertyName("data");
                w.WriteRawValue(dataJson, skipInputValidation: true);
            }
            w.WriteEndObject();
        }
        var json = Encoding.UTF8.GetString(ms.ToArray());
        await _client.SendAsync(json, default).ConfigureAwait(false);
    }

    private async Task SendErrorAsync(string message) =>
        await _client.SendAsync(JsonSerializer.Serialize(new { type = "error", message }), default).ConfigureAwait(false);

    private void EnsureSubscribed()
    {
        lock (_subLock)
        {
            if (_sub is not null) return;
            var s = _sessions.Get(_sessionName);
            if (s is null) return;
            _sub = s.Subscribe();
        }
    }

    private async Task InboundLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string? msg;
            try { msg = await _client.ReceiveAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            if (msg is null) break; // client closed
            try { await HandleMessageAsync(msg).ConfigureAwait(false); }
            catch (JsonException) { /* ignore malformed client frames */ }
            catch (OperationCanceledException) { break; } // transport/loop teardown
            catch (Exception ex)
            {
                // A genuine per-command failure escaped the command handlers: surface it to
                // the browser instead of silently dropping it (best-effort send). Only the
                // transport-failure teardown above should exit the loop.
                try { await SendErrorAsync($"command failed: {ex.Message}").ConfigureAwait(false); }
                catch { /* best-effort */ }
            }
        }
    }
}

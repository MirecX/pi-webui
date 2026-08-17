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
    private readonly SessionAutoTitler? _titler;

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

    /// <param name="titler">
    /// Optional auto-titler (ticket #06). When <c>null</c> auto-title is disabled — the
    /// server wires the real <see cref="TitleGenerator"/>; tests inject a stub or omit it.
    /// Kept as a seam so existing tests that send prompts are unaffected (no extra frames).
    /// </param>
    public WsBridge(SessionManager sessions, string sessionName, IWsClient client, SessionAutoTitler? titler = null)
    {
        _sessions = sessions;
        _sessionName = sessionName;
        _client = client;
        _titler = titler;
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

            // --- compaction / retry / state / export (ticket #08) ---------------
            // All scoped to the ATTACHED session's child; results relayed back to the
            // browser as a `result` frame carrying the RPC response `data` (compaction
            // summary, auto-toggle acks, stats object, exported path, state, structure).
            case "compact":
                await DispatchModelCommandAsync("compact", s => s.CompactAsync()).ConfigureAwait(false);
                break;
            case "set_auto_compaction":
                await DispatchModelCommandAsync("set_auto_compaction",
                    s => s.SetAutoCompactionAsync(root.TryGetProperty("enabled", out var ac) && ac.ValueKind == JsonValueKind.True))
                    .ConfigureAwait(false);
                break;
            case "set_auto_retry":
                await DispatchModelCommandAsync("set_auto_retry",
                    s => s.SetAutoRetryAsync(root.TryGetProperty("enabled", out var ar) && ar.ValueKind == JsonValueKind.True))
                    .ConfigureAwait(false);
                break;
            case "stats":
                await DispatchModelCommandAsync("stats", s => s.GetSessionStatsAsync()).ConfigureAwait(false);
                break;
            case "structure":
                // Session structure as a real tree (rpc.md get_tree) for the session panel,
                // so the hierarchy is genuinely visible rather than a flat entry list.
                await DispatchModelCommandAsync("structure", s => s.GetTreeAsync()).ConfigureAwait(false);
                break;
            case "history":
                // All messages for history replay on browser (re)attach (rpc.md get_messages).
                await DispatchModelCommandAsync("history", s => s.GetMessagesAsync()).ConfigureAwait(false);
                break;
            case "state":
                // Fetch the attached session's ACTUAL current selection (model + thinkingLevel +
                // autoCompactionEnabled + stats), feeding BOTH the model/thinking pickers and the
                // session panel from one round trip (dedupes the former get_state case).
                await DispatchModelCommandAsync("state", s => s.GetStateAsync()).ConfigureAwait(false);
                break;
            case "export_html":
                {   // route + register the generated path so the browser can download it
                    string? outputPath = null;
                    if (root.TryGetProperty("outputPath", out var opProp) && opProp.ValueKind == JsonValueKind.String)
                        outputPath = opProp.GetString();
                    await HandleExportAsync(outputPath).ConfigureAwait(false);
                }
                break;

            // --- session browser: fork / clone / get_fork_messages (ticket #06) ----
            // All scoped to the ATTACHED session's child; results relayed as `result` frames
            // (fork returns the forking message text; clone/get_fork_messages return data).
            case "get_fork_messages":
                await DispatchModelCommandAsync("get_fork_messages", s => s.GetForkMessagesAsync()).ConfigureAwait(false);
                break;
            case "fork":
                if (root.TryGetProperty("entryId", out var eidProp) && eidProp.GetString() is { } entryId)
                {
                    await DispatchModelCommandAsync("fork", s => s.ForkAsync(entryId)).ConfigureAwait(false);
                }
                break;
            case "clone":
                // Clone forks the active branch into a NEW session (rpc.md clone); register the
                // newly created file as a stored session so it becomes listable + resumable.
                await HandleCloneAsync().ConfigureAwait(false);
                break;

            // --- HITL dialogs (ticket #07) ------------------------------------
            // The browser answers an extension_ui_request (select/confirm/input/editor)
            // with a `hitl_response` frame; the answer is relayed back to the ATTACHED
            // session's child as an extension_ui_response. Fire-and-forget: no `result`
            // frame, but the shared not-running guard + error ladder still apply.
            case "hitl_response":
                if (root.TryGetProperty("id", out var ridProp) && ridProp.GetString() is { } requestId)
                {
                    string? value = null;
                    bool? confirmed = null;
                    bool cancelled = false;
                    if (root.TryGetProperty("value", out var vProp) && vProp.ValueKind == JsonValueKind.String)
                        value = vProp.GetString();
                    if (root.TryGetProperty("confirmed", out var cfProp))
                    {
                        if (cfProp.ValueKind == JsonValueKind.True) confirmed = true;
                        else if (cfProp.ValueKind == JsonValueKind.False) confirmed = false;
                    }
                    if (root.TryGetProperty("cancelled", out var cancProp) && cancProp.ValueKind == JsonValueKind.True)
                        cancelled = true;
                    await DispatchHitlResponseAsync(requestId, value, confirmed, cancelled).ConfigureAwait(false);
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

        // Auto-title a fresh, untitled session from its FIRST user message (ticket #06).
        // Fire-and-forget: title generation is a separate, non-blocking completion at the
        // box's default model endpoint, so it NEVER delays the agent's first turn. Gated so
        // it runs once per fresh session and only when a titler is wired (not in old tests).
        if (_titler is not null)
        {
            var s = _sessions.Get(_sessionName);
            if (s is { AutoTitlePending: true })
            {
                s.AutoTitlePending = false; // fire exactly once
                _ = AutoTitleAsync(s, message);
            }
        }

        await DispatchTurnAsync("prompt", s => s.PromptAsync(message, streamingBehavior)).ConfigureAwait(false);
    }

    /// <summary>
    /// Generate a title for <paramref name="s"/> from its first user message and surface it
    /// to the browser as a <c>session_event</c> (action "title"). Never throws out; on any
    /// failure the fallback (truncated first message + timestamp) is applied instead.
    /// </summary>
    private async Task AutoTitleAsync(PiWebui.Session.Session s, string firstMessage)
    {
        string title;
        try
        {
            title = await _titler!.GenerateAsync(firstMessage).ConfigureAwait(false);
        }
        catch
        {
            title = SessionAutoTitler.Fallback(firstMessage);
        }
        _sessions.SetTitle(s.Name, title);
        try
        {
            await _client.SendAsync(JsonSerializer.Serialize(new
            {
                type = "session_event",
                action = "title",
                session = new { name = s.Name, status = s.Status.ToString().ToLowerInvariant(), title },
            }), default).ConfigureAwait(false);
        }
        catch
        {
            // best-effort: the client may already be closed
        }
    }

    /// <summary>
    /// Clone the attached session's active branch into a NEW session and register the clone as
    /// a stored (listable + resumable) session (ticket #06). Surfaces the new session's name to
    /// the browser as a <c>result</c> frame (target "clone") so the UI refreshes its list; the
    /// shared dispatch guard for the not-running case keeps the error ladder consistent.
    /// </summary>
    private async Task HandleCloneAsync()
    {
        var s = _sessions.Get(_sessionName);
        if (s is null || !s.IsRunning)
        {
            await SendErrorAsync($"session '{_sessionName}' is not running; initialize it first").ConfigureAwait(false);
            return;
        }

        try
        {
            var newName = await _sessions.CloneAndRegisterAsync(_sessionName).ConfigureAwait(false);
            if (newName is null)
            {
                await SendErrorAsync("clone rejected or the new session could not be located").ConfigureAwait(false);
                return;
            }
            await SendResultAsync("clone", JsonSerializer.Serialize(new { cloned = true, session = newName }))
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            await SendErrorAsync(ex.Message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await SendErrorAsync($"clone failed: {ex.Message}").ConfigureAwait(false);
        }
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
    /// session's child. Delegate to the shared dispatcher; turn commands relay nothing on
    /// success (a rejection surfaces as an error only).
    /// </summary>
    private Task DispatchTurnAsync(string action, Func<PiWebui.Session.Session, Task<RpcResponse?>> send)
        => DispatchAsync(action, send, onSuccess: null);

    /// <summary>
    /// Relay a browser's HITL answer back to the attached session's child as an
    /// <c>extension_ui_response</c> (rpc.md, ticket #07). Uses the shared dispatcher so
    /// the not-running guard and error ladder stay consistent; fire-and-forget so no
    /// <c>result</c> frame is relayed.
    /// </summary>
    private Task DispatchHitlResponseAsync(string requestId, string? value, bool? confirmed, bool cancelled)
        => DispatchAsync("hitl_response",
            // RespondHitlAsync is fire-and-forget (no RpcResponse); wrap to the shared
            // dispatcher's send delegate shape with a null result (no result frame).
            async s => { await s.RespondHitlAsync(requestId, value, confirmed, cancelled).ConfigureAwait(false); return null; },
            onSuccess: null);

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
    /// Shared dispatch core for EVERY inbound command against the attached session's
    /// child (turn-controls, model/thinking commands, get_state). ONE guard + ONE error
    /// ladder keeps all commands consistent: the attached session must be running;
    /// genuine failures surface to the browser; expected teardown stays quiet. Only the
    /// success handling differs, parameterised via <paramref name="onSuccess"/> so e.g.
    /// model/thinking commands relay their <c>result</c> frame while turn commands stay
    /// silent.
    /// </summary>
    private async Task DispatchAsync(
        string action,
        Func<PiWebui.Session.Session, Task<RpcResponse?>> send,
        Func<string, RpcResponse?, Task>? onSuccess = null)
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
            {
                await SendErrorAsync($"{action} rejected: {resp.Error ?? "unknown error"}").ConfigureAwait(false);
                return;
            }
            if (onSuccess is not null)
                await onSuccess(action, resp).ConfigureAwait(false);
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

    /// <summary>
    /// Run one model/thinking/state command (models/set_model/thinking_levels/set_thinking_level/get_state)
    /// against the attached session's child. Delegate to the shared dispatcher with a
    /// success handler that relays the RPC response <c>data</c> back to the browser as a
    /// <c>result</c> frame so it can populate the available lists and reflect the applied
    /// selection.
    /// </summary>
    private Task DispatchModelCommandAsync(string action, Func<PiWebui.Session.Session, Task<RpcResponse?>> send)
        => DispatchAsync(action, send, (a, resp) => SendResultAsync(a, resp?.DataJson));

    /// <summary>
    /// Export the attached session's transcript as an HTML file (rpc.md <c>export_html</c>;
    /// data.path = generated file). On success the generated path is registered with the
    /// manager so the token-gated <c>GET /api/sessions/{name}/export</c> endpoint can serve
    /// it as a download, and the result frame (carrying the path) is relayed to the browser.
    /// </summary>
    private Task HandleExportAsync(string? outputPath)
        => DispatchAsync("export_html",
            s => s.ExportHtmlAsync(outputPath),
            (action, resp) =>
            {
                if (resp?.Data is { } d
                    && d.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String)
                    _sessions.RegisterExport(_sessionName, p.GetString()!);
                return SendResultAsync(action, resp?.DataJson);
            });


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

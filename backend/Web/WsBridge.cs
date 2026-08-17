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
                await _client.SendAsync(ev.Raw, ct).ConfigureAwait(false);
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
                    await HandlePromptAsync(message).ConfigureAwait(false);
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

    private async Task HandlePromptAsync(string message)
    {
        var s = _sessions.Get(_sessionName);
        if (s is null || !s.IsRunning)
        {
            await SendErrorAsync($"session '{_sessionName}' is not running; initialize it first").ConfigureAwait(false);
            return;
        }

        try
        {
            var resp = await s.PromptAsync(message).ConfigureAwait(false);
            // A correlated, non-success response means the prompt was rejected
            // server-side; surface it so a rejection isn't silent.
            if (resp is { Success: false })
                await SendErrorAsync($"prompt rejected: {resp.Error ?? "unknown error"}").ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            await SendErrorAsync(ex.Message).ConfigureAwait(false);
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
                if (name == _sessionName)
                {
                    // we deleted the session we're attached to -> close the tab connection
                    await _client.CloseAsync().ConfigureAwait(false);
                }
                break;
        }
        await SendSessionEventAsync(type, name).ConfigureAwait(false);
    }

    private async Task SendSessionEventAsync(string action, string name)
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
        }
    }
}

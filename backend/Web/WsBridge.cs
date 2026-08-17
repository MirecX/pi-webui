using System.Text.Json;
using System.Threading.Channels;
using PiWebui.Rpc;
using PiWebui.Session;

namespace PiWebui.Web;

/// <summary>
/// Bridges a WebSocket client to one session: forwards RPC events to the browser
/// and turns client messages (e.g. <c>{"type":"prompt","message":"..."}</c>) into
/// commands on the session. Tested at this boundary against the fake pi.
/// </summary>
public sealed class WsBridge
{
    private readonly SessionManager _session;
    private readonly IWsClient _client;

    public WsBridge(SessionManager session, IWsClient client)
    {
        _session = session;
        _client = client;
    }

    /// <summary>Run both the forward and inbound loops until either ends.</summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        var sub = _session.Subscribe();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var forward = Task.Run(() => ForwardLoopAsync(sub, linked.Token), CancellationToken.None);
        var inbound = Task.Run(() => InboundLoopAsync(linked.Token), CancellationToken.None);

        await Task.WhenAny(forward, inbound);
        linked.Cancel();

        try { await Task.WhenAll(forward, inbound); }
        catch (OperationCanceledException) { /* expected on shutdown */ }
        catch { /* best-effort */ }

        _session.Unsubscribe(sub);
        await _client.CloseAsync();
    }

    /// <summary>Forward every event on the stream to the browser as raw JSON text.</summary>
    public async Task ForwardLoopAsync(Channel<RpcEvent> stream, CancellationToken ct = default)
    {
        await foreach (var ev in stream.Reader.ReadAllAsync(ct))
            await _client.SendAsync(ev.Raw, ct);
    }

    /// <summary>Handle one inbound JSON message from the browser.</summary>
    public async Task HandleMessageAsync(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("type", out var typeProp)) return;
        var type = typeProp.GetString();

        if (type == "prompt" && root.TryGetProperty("message", out var msgProp))
        {
            var message = msgProp.GetString();
            if (message is not null)
            {
                var resp = await _session.PromptAsync(message);
                // A correlated, non-success response means the prompt was rejected
                // server-side. Surface it to the browser so a rejection isn't silent.
                if (resp is not null && !resp.Success)
                    await _client.SendAsync(JsonSerializer.Serialize(new
                    {
                        type = "error",
                        message = $"prompt rejected: {resp.Error ?? "unknown error"}",
                    }), default);
            }
        }
        // ticket #01 only supports prompt; abort/steer/etc. arrive in later tickets.
    }

    private async Task InboundLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string? msg;
            try { msg = await _client.ReceiveAsync(ct); }
            catch (OperationCanceledException) { break; }
            if (msg is null) break; // client closed
            try { await HandleMessageAsync(msg); }
            catch (JsonException) { /* ignore malformed client frames */ }
        }
    }
}

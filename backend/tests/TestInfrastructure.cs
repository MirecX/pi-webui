using System.Threading.Channels;
using PiWebui.Rpc;
using PiWebui.Web;

namespace PiWebui.Tests;

/// <summary>In-process fake implementing IPiRpcClient; the injection seam for session/WS tests.</summary>
internal sealed class FakePiRpcClient : IPiRpcClient
{
    public event Action<RpcEvent>? EventReceived;
    public List<RpcCommand> Sent { get; } = new();
    public bool Started { get; private set; }
    public bool Disposed { get; private set; }
    private readonly Func<RpcCommand, CancellationToken, Task<RpcResponse?>>? _responder;

    public FakePiRpcClient(Func<RpcCommand, RpcResponse?>? responder = null)
        : this(responder is null ? null : (cmd, _) => Task.FromResult(responder(cmd))) { }

    /// <summary>Async responder for slow/blocked simulations (per-session isolation tests).</summary>
    public FakePiRpcClient(Func<RpcCommand, CancellationToken, Task<RpcResponse?>>? responder)
        => _responder = responder;

    public void Start() => Started = true;

    public void Emit(RpcEvent ev) => EventReceived?.Invoke(ev);

    public Task<RpcResponse?> SendAsync(RpcCommand command, CancellationToken ct = default)
    {
        Sent.Add(command);
        return _responder is null
            ? Task.FromResult<RpcResponse?>(null)
            : _responder(command, ct);
    }

    public async ValueTask DisposeAsync()
    {
        Disposed = true;
        await Task.CompletedTask;
    }
}

/// <summary>Fake WebSocket client: records sent frames, queues inbound frames.</summary>
internal sealed class FakeWsClient : IWsClient
{
    public List<string> Sent { get; } = new();
    private readonly Channel<string> _inbound = Channel.CreateUnbounded<string>();
    public bool Closed { get; private set; }

    public void EnqueueInbound(string json) => _inbound.Writer.TryWrite(json);
    public void CompleteInbound() => _inbound.Writer.TryComplete();

    public Task SendAsync(string text, CancellationToken ct = default)
    {
        lock (Sent) Sent.Add(text);
        return Task.CompletedTask;
    }

    public async Task<string?> ReceiveAsync(CancellationToken ct = default)
    {
        if (await _inbound.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            return await _inbound.Reader.ReadAsync(ct);
        return null;
    }

    public Task CloseAsync()
    {
        Closed = true;
        _inbound.Writer.TryComplete();
        return Task.CompletedTask;
    }
}

internal static class TestWait
{
    /// <summary>Poll <paramref name="predicate"/> until true or timeout (default 5s).</summary>
    public static async Task UntilAsync(Func<bool> predicate, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!predicate())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException("Condition not met within timeout.");
            await Task.Delay(20);
        }
    }
}

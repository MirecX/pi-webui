namespace PiWebui.Rpc;

/// <summary>
/// Thin seam over the pi RPC child so the session manager (and, transitively,
/// tests) can talk to either the real spawned process or a scripted fake.
/// </summary>
public interface IPiRpcClient : IAsyncDisposable
{
    /// <summary>Raised for each parsed non-response event from the child.</summary>
    event Action<RpcEvent>? EventReceived;

    /// <summary>Start the child and begin reading its stdout event stream.</summary>
    void Start();

    /// <summary>Send a command and await its correlated response (null if none).</summary>
    Task<RpcResponse?> SendAsync(RpcCommand command, CancellationToken ct = default);
}

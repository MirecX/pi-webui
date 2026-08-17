using System.Threading.Channels;
using PiWebui.Rpc;

namespace PiWebui.Session;

/// <summary>
/// Ticket #01: manages ONE default session. Owns an <see cref="IPiRpcClient"/>,
/// fans its events out to subscribers, and forwards prompt commands to the child.
/// Tested against a scripted fake pi via <see cref="IPiRpcClient"/>.
/// </summary>
public sealed class SessionManager : IAsyncDisposable
{
    private readonly IPiRpcClient _client;
    private readonly FanOut<RpcEvent> _events = new();

    public SessionManager(IPiRpcClient client) => _client = client;

    /// <summary>Start the child and begin relaying its events.</summary>
    public void Start()
    {
        _client.EventReceived += OnEvent;
        _client.Start();
    }

    private void OnEvent(RpcEvent ev) => _events.Publish(ev);

    /// <summary>Subscribe to this session's live event stream.</summary>
    public Channel<RpcEvent> Subscribe() => _events.Subscribe();

    public void Unsubscribe(Channel<RpcEvent> ch) => _events.Unsubscribe(ch);

    /// <summary>Send a prompt to the agent and await its acceptance response.</summary>
    public Task<RpcResponse?> PromptAsync(string message, CancellationToken ct = default)
        => _client.SendAsync(new PromptCommand(message), ct);

    public async ValueTask DisposeAsync()
    {
        _client.EventReceived -= OnEvent;
        await _client.DisposeAsync();
    }
}

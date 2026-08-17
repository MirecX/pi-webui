using System.Net.WebSockets;
using System.Text;

namespace PiWebui.Web;

/// <summary>ASP.NET Core adapter implementing <see cref="IWsClient"/> over a real WebSocket.</summary>
public sealed class AspNetWsClient : IWsClient
{
    private readonly WebSocket _socket;

    public AspNetWsClient(WebSocket socket) => _socket = socket;

    public async Task SendAsync(string text, CancellationToken ct = default)
    {
        if (_socket.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(text);
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    public async Task<string?> ReceiveAsync(CancellationToken ct = default)
    {
        if (_socket.State != WebSocketState.Open) return null;
        var buffer = new byte[16 * 1024];
        var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                return null;
            }
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public async Task CloseAsync()
    {
        if (_socket.State == WebSocketState.Open)
            await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
    }
}

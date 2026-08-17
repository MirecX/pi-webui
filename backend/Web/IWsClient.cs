using System.Threading.Channels;

namespace PiWebui.Web;

/// <summary>Transport abstraction over a single WebSocket connection (or a test fake).</summary>
public interface IWsClient
{
    /// <summary>Send one text frame to the browser.</summary>
    Task SendAsync(string text, CancellationToken ct = default);

    /// <summary>Receive the next text frame, or null when the connection is closed.</summary>
    Task<string?> ReceiveAsync(CancellationToken ct = default);

    /// <summary>Close the connection (best-effort).</summary>
    Task CloseAsync();
}

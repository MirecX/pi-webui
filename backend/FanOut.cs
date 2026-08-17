using System.Threading.Channels;

namespace PiWebui;

/// <summary>
/// Simple publish/subscribe fan-out over <see cref="Channel{T}"/> readers. Each
/// subscriber gets an unbounded channel that receives a copy of every published
/// item. Used to broadcast RPC events to one or more WebSocket subscribers.
/// </summary>
public sealed class FanOut<T>
{
    private readonly object _lock = new();
    private readonly HashSet<Channel<T>> _subs = new();

    public Channel<T> Subscribe()
    {
        var ch = Channel.CreateUnbounded<T>(new UnboundedChannelOptions { SingleReader = true });
        lock (_lock) _subs.Add(ch);
        return ch;
    }

    public void Unsubscribe(Channel<T> ch)
    {
        lock (_lock)
        {
            _subs.Remove(ch);
            if (!ch.Reader.Completion.IsCompleted)
                ch.Writer.TryComplete();
        }
    }

    public void Publish(T item)
    {
        lock (_lock)
        {
            foreach (var ch in _subs)
                ch.Writer.TryWrite(item);
        }
    }
}

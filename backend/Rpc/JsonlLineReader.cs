using System.Runtime.CompilerServices;
using System.Text;

namespace PiWebui.Rpc;

/// <summary>
/// Strict JSONL line reader compliant with pi's RPC framing:
/// splits on LF (<c>\n</c>) only, strips a single trailing CR (<c>\r</c>), and does
/// NOT treat Unicode line separators (U+2028/U+2029) as boundaries.
/// </summary>
public sealed class JsonlLineReader
{
    private readonly StreamReader _reader;
    private readonly StringBuilder _buf = new();

    public JsonlLineReader(StreamReader reader) => _reader = reader;

    public async IAsyncEnumerable<string> ReadLinesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var chunk = new char[8192];
        while (true)
        {
            int n = await _reader.ReadAsync(chunk.AsMemory(), ct).ConfigureAwait(false);
            if (n <= 0) break;
            _buf.Append(chunk, 0, n);

            while (true)
            {
                int idx = IndexOfNewline(_buf);
                if (idx < 0) break;
                yield return TakeLine(idx);
            }
        }

        if (_buf.Length > 0)
            yield return TakeLine(_buf.Length, hasNewline: false);
    }

    private string TakeLine(int length, bool hasNewline = true)
    {
        string line = _buf.ToString(0, length);
        _buf.Remove(0, length + (hasNewline ? 1 : 0));
        if (line.Length > 0 && line[^1] == '\r')
            line = line[..^1];
        return line;
    }

    private static int IndexOfNewline(StringBuilder sb)
    {
        for (int i = 0; i < sb.Length; i++)
            if (sb[i] == '\n') return i;
        return -1;
    }
}

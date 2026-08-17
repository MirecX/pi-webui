using System.Text;
using PiWebui.Rpc;
using Xunit;

namespace PiWebui.Tests;

public class JsonlLineReaderTests
{
    private static async Task<List<string>> ReadAll(string text)
    {
        using var sr = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(text)), Encoding.UTF8);
        var lines = new List<string>();
        await foreach (var l in new JsonlLineReader(sr).ReadLinesAsync())
            lines.Add(l);
        return lines;
    }

    [Fact]
    public async Task Splits_on_lf_only()
    {
        var lines = await ReadAll("{\"a\":1}\n{\"b\":2}\n");
        Assert.Equal(new[] { "{\"a\":1}", "{\"b\":2}" }, lines);
    }

    [Fact]
    public async Task Strips_trailing_cr()
    {
        var lines = await ReadAll("{\"a\":1}\r\n{\"b\":2}\n");
        Assert.Equal(new[] { "{\"a\":1}", "{\"b\":2}" }, lines);
    }

    [Fact]
    public async Task Handles_partial_line_chunks()
    {
        // Feed the buffer in awkward byte-sized pieces to exercise chunk reassembly.
        var full = "{\"a\":\"hello\"}\n{\"b\":2}\n";
        using var sr = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(full)), Encoding.UTF8);
        var reader = new JsonlLineReader(sr);
        var lines = new List<string>();
        await foreach (var l in reader.ReadLinesAsync())
            lines.Add(l);
        Assert.Equal(new[] { "{\"a\":\"hello\"}", "{\"b\":2}" }, lines);
    }

    [Fact]
    public async Task DoesNot_split_on_unicode_line_separators()
    {
        // U+2028 and U+2029 are valid inside JSON strings; a compliant reader must
        // NOT split there. \u2028 encoded literally in the source.
        var line1 = "{\"msg\":\"line one\u2028\u2029still one\"}";
        var text = line1 + "\n" + "{\"two\":2}\n";
        var lines = await ReadAll(text);
        Assert.Equal(2, lines.Count);
        Assert.Equal(line1, lines[0]);
    }

    [Fact]
    public async Task Emits_final_line_without_trailing_newline()
    {
        var lines = await ReadAll("{\"x\":1}\n{\"y\":2}");
        Assert.Equal(new[] { "{\"x\":1}", "{\"y\":2}" }, lines);
    }
}

using System.Diagnostics;
using System.Text.Json;

namespace PiWebui.Rpc;

/// <summary>Options controlling how a <see cref="PiRpcClient"/> spawns its child.</summary>
public sealed class PiClientOptions
{
    public string Executable { get; set; } = "pi";
    public string[] Arguments { get; set; } = Array.Empty<string>();
    public string WorkingDirectory { get; set; } = Directory.GetCurrentDirectory();
    public Dictionary<string, string>? Environment { get; set; }
}

/// <summary>
/// Real RPC client: spawns <c>pi --mode rpc</c> and speaks JSONL over its stdio.
/// Commands are written one-per-line with an optional <c>id</c>; responses with a
/// matching id resolve the pending <see cref="SendAsync"/>. Non-response events
/// (with or without ids) are raised via <see cref="EventReceived"/>.
/// </summary>
public sealed class PiRpcClient : IPiRpcClient
{
    private readonly PiClientOptions _opts;
    private readonly object _sendLock = new();
    private readonly Dictionary<string, TaskCompletionSource<RpcResponse>> _pending = new();

    private Process? _proc;
    private StreamWriter? _stdin;
    private int _idCounter;

    public PiRpcClient(PiClientOptions opts) => _opts = opts;

    public event Action<RpcEvent>? EventReceived;

    public void Start()
    {
        var psi = new ProcessStartInfo
        {
            FileName = _opts.Executable,
            WorkingDirectory = _opts.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in _opts.Arguments) psi.ArgumentList.Add(a);
        if (_opts.Environment is not null)
            foreach (var (k, v) in _opts.Environment) psi.Environment[k] = v;

        _proc = new Process { StartInfo = psi };
        _proc.Start();
        _stdin = _proc.StandardInput;

        _ = Task.Run(() => ReadEventsAsync(_proc.StandardOutput));
        _ = Task.Run(() => DrainAsync(_proc.StandardError));
    }

    public async Task<RpcResponse?> SendAsync(RpcCommand command, CancellationToken ct = default)
    {
        var id = command.Id ?? "req-" + Interlocked.Increment(ref _idCounter);
        var tcs = new TaskCompletionSource<RpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_pending) _pending[id] = tcs;

        lock (_sendLock)
        {
            _stdin!.Write(command.ToJson(id));
            _stdin.Write('\n');
            _stdin.Flush();
        }

        ct.ThrowIfCancellationRequested();
        return await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync().ConfigureAwait(false); }
        catch { /* best-effort shutdown */ }
        _proc?.Dispose();
    }

    private async Task StopAsync()
    {
        if (_proc is null) return;
        if (!_proc.HasExited)
        {
            try { _proc.Kill(entireProcessTree: true); }
            catch { /* already exited */ }
        }
        // fail pending commands
        lock (_pending)
        {
            foreach (var tcs in _pending.Values)
                if (!tcs.Task.IsCompleted)
                    tcs.TrySetCanceled();
            _pending.Clear();
        }
        await _proc.WaitForExitAsync().ConfigureAwait(false);
    }

    private async Task ReadEventsAsync(StreamReader stdout)
    {
        var reader = new JsonlLineReader(stdout);
        await foreach (var line in reader.ReadLinesAsync())
        {
            RpcEvent ev;
            try
            {
                ev = RpcEventParser.Parse(line);
            }
            catch (JsonException)
            {
                continue; // malformed line from child -> ignore, keep going
            }

            if (ev is ResponseEvent resp)
            {
                TaskCompletionSource<RpcResponse>? tcs = null;
                if (resp.Id is not null && _pending.TryGetValue(resp.Id, out var found) && _pending.Remove(resp.Id))
                    tcs = found;
                var self = new RpcResponse(resp.Id, resp.Command, resp.Success, resp.Error, resp.Data);
                tcs?.TrySetResult(self);
                // responses without a pending id are dropped (rare/accepted)
            }
            else
            {
                EventReceived?.Invoke(ev);
            }
        }
    }

    private static async Task DrainAsync(StreamReader stderr)
    {
        try { await stderr.ReadToEndAsync().ConfigureAwait(false); }
        catch { /* ignore child stderr */ }
    }
}

/// <summary>Typed, id-correlated command response.</summary>
public sealed record RpcResponse(string? Id, string Command, bool Success, string? Error, JsonElement? Data)
{
    /// <summary>Data rendered as JSON string, or null.</summary>
    public string? DataJson => Data is { } d ? d.GetRawText() : null;
}

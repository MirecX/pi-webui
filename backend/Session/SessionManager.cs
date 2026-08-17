using System.Collections.Concurrent;
using PiWebui.Rpc;

namespace PiWebui.Session;

/// <summary>
/// Registry of N named sessions (ticket #05). Each session owns its own
/// <see cref="IPiRpcClient"/> child via the injected factory, so sessions run
/// concurrently without serializing each other (no shared lock on the agent loop).
///
/// Lifecycle:
///  - <see cref="InitAsync"/> — create a named session (spawn a fresh child), or
///    RESUME a recycled/stored session by spawning a fresh child pointed at its
///    preserved history file (<c>switch_session</c>).
///  - <see cref="RecycleAsync"/> — stop the child but KEEP the history file, so the
///    session stays resumable.
///  - <see cref="DeleteAsync"/> — stop the child and remove the session/history
///    permanently.
///
/// Sessions are NEVER created implicitly on attach/list; only by an explicit init.
/// </summary>
public sealed class SessionManager : IAsyncDisposable
{
    /// <summary>Session name used when a client connects without specifying one.</summary>
    public const string DefaultSessionName = "default";

    private readonly ConcurrentDictionary<string, Session> _sessions =
        new(StringComparer.Ordinal);

    private readonly Func<IPiRpcClient> _clientFactory;
    private readonly string _sessionsDir;

    public SessionManager(Func<IPiRpcClient> clientFactory, string? sessionsDir = null)
    {
        _clientFactory = clientFactory;
        _sessionsDir = sessionsDir ?? DefaultSessionsDir();
    }

    private static string DefaultSessionsDir() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) ?? ".",
            ".pi", "agent", "extensions", "pi-webui", "sessions");

    public IReadOnlyList<Session> List() => _sessions.Values.OrderBy(s => s.Name).ToList();

    public Session? Get(string name) => _sessions.TryGetValue(name, out var s) ? s : null;

    /// <summary>
    /// Create the named session (or resume it if it was recycled). Idempotent for an
    /// already-running session.
    /// </summary>
    public async Task<Session> InitAsync(string name, CancellationToken ct = default)
    {
        var existing = Get(name);
        if (existing is not null)
        {
            if (existing.IsRunning) return existing; // already running
            await ResumeAsync(existing, ct).ConfigureAwait(false);
            return existing;
        }

        // Register under a lock to avoid double-create racing on the same name.
        var session = new Session(name);
        var actual = _sessions.GetOrAdd(name, session);
        if (!ReferenceEquals(actual, session))
        {
            // lost the create race: an equivalent session already exists
            if (actual.IsRunning) return actual;
            await ResumeAsync(actual, ct).ConfigureAwait(false);
            return actual;
        }

        await InitializeAsync(actual, ct).ConfigureAwait(false);
        return actual;
    }

    /// <summary>Stop the child but keep the history file; the session stays resumable.</summary>
    public async Task RecycleAsync(string name, CancellationToken ct = default)
    {
        var s = Get(name);
        if (s is null) return;
        await s.RecycleAsync().ConfigureAwait(false);
    }

    /// <summary>Stop the child and permanently remove the session and its history file.</summary>
    public async Task DeleteAsync(string name, CancellationToken ct = default)
    {
        if (!_sessions.TryRemove(name, out var s))
            return;
        await s.RecycleAsync().ConfigureAwait(false);
        TryDeleteHistory(s.HistoryFilePath);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _sessions.Values)
            await s.DisposeAsync().ConfigureAwait(false);
        _sessions.Clear();
    }

    // --- internals -----------------------------------------------------------

    /// <summary>Brand-new session: spawn a fresh child and learn its history file path.</summary>
    private async Task InitializeAsync(Session s, CancellationToken ct)
    {
        var managedPath = HistoryPathFor(s.Name);
        var client = _clientFactory();
        s.AttachClient(client);
        client.Start();

        if (IsNonEmptyFile(managedPath))
        {
            // A stored session file already exists (e.g. across a server restart):
            // point the fresh child at it so history is resumed.
            s.HistoryFilePath = managedPath;
            await client.SendAsync(new SwitchSessionCommand(managedPath), ct).ConfigureAwait(false);
        }
        else
        {
            // Brand-new: discover the child's session file so recycle/delete can
            // preserve/remove the right path; fall back to the managed path.
            var resp = await client.SendAsync(new GetStateCommand(), ct).ConfigureAwait(false);
            s.HistoryFilePath = ExtractSessionFile(resp) ?? managedPath;
        }
    }

    /// <summary>Resume a recycled session: spawn a fresh child pointed at its history file.</summary>
    private async Task ResumeAsync(Session s, CancellationToken ct)
    {
        var client = _clientFactory();
        s.AttachClient(client);
        client.Start();

        var history = s.HistoryFilePath;
        if (history is not null && IsNonEmptyFile(history))
        {
            await client.SendAsync(new SwitchSessionCommand(history), ct).ConfigureAwait(false);
        }
        else
        {
            // no preserved file (shouldn't normally happen for a recycled session)
            var resp = await client.SendAsync(new GetStateCommand(), ct).ConfigureAwait(false);
            s.HistoryFilePath = ExtractSessionFile(resp) ?? history;
        }
    }

    private string HistoryPathFor(string name) =>
        Path.Combine(_sessionsDir, Sanitize(name) + ".jsonl");

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
    }

    private static bool IsNonEmptyFile(string path) =>
        File.Exists(path) && new FileInfo(path).Length > 0;

    private static string? ExtractSessionFile(RpcResponse? resp)
    {
        if (resp is null || !resp.Success || resp.Data is not { } data) return null;
        if (data.TryGetProperty("sessionFile", out var file) && file.ValueKind == System.Text.Json.JsonValueKind.String)
            return file.GetString();
        return null;
    }

    private static void TryDeleteHistory(string? path)
    {
        if (path is null || !File.Exists(path)) return;
        try { File.Delete(path); }
        catch { /* best-effort removal */ }
    }
}

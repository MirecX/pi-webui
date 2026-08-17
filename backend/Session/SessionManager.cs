using System.Collections.Concurrent;
using System.Text.Json;
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

    /// <summary>
    /// Per-name single-flight gates so two simultaneous <see cref="InitAsync"/> calls on
    /// the same brand-new name cannot both spawn a child: exactly one child is ever
    /// created per session, and concurrent init on the same name is serialized.
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _initGates =
        new(StringComparer.Ordinal);

    private readonly Func<IPiRpcClient> _clientFactory;
    private readonly string _sessionsDir;
    private readonly string _sharedSessionsDir;

    /// <summary>
    /// Registered stored (not-loaded) sessions whose history files live OUTSIDE the sessions
    /// dir — e.g. a pi <c>clone</c> duplicates the active branch into a brand-new session
    /// file in pi's own session dir (ticket #06). name -> absolute history file. These are
    /// surfaced as "stored" and resumed by name on init. Kept so a clone is listable and
    /// resumable even though its file is not under <see cref="_sessionsDir"/>.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _storedFiles =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Persisted session titles (name -> title), loaded from a sidecar JSON on startup and
    /// written on every title change so titles survive a server restart (ticket #06).
    /// Stored-session summaries surface the persisted title so "browse at a glance" works
    /// after a restart instead of showing <c>title: null</c>.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _persistedTitles =
        new(StringComparer.Ordinal);

    /// <summary>
    /// On-the-fly display titles derived from a stored/shared session file's first user
    /// message (the auto-title "safe fallback" style), keyed by absolute file path. Kept so
    /// we don't re-parse every .jsonl on every list call. Stems stay the resume identity;
    /// this is display only.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _derivedTitles =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Most recent exported HTML transcript path per session (name -> absolute path) (ticket #08).
    /// Populated when <c>export_html</c> succeeds so the browser can download the generated file
    /// via the token-gated <c>GET /api/sessions/{name}/export</c> endpoint.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _exports =
        new(StringComparer.Ordinal);

    private readonly string _titlesPath;

    public SessionManager(Func<IPiRpcClient> clientFactory, string? sessionsDir = null, string? sharedSessionsDir = null)
    {
        _clientFactory = clientFactory;
        _sessionsDir = sessionsDir ?? DefaultSessionsDir();
        // The shared standard pi sessions dir (~/.pi/agent/sessions) — the SAME store the
        // TUI / any pi uses. We scan it so sessions created outside the webui are browsable
        // and resumable here (cross-TUI/web continuity). Injectable for tests.
        _sharedSessionsDir = sharedSessionsDir ?? DefaultSharedSessionsDir();
        _titlesPath = Path.Combine(_sessionsDir, "titles.json");
        LoadPersistedTitles();
    }

    private static string DefaultSessionsDir() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) ?? ".",
            ".pi", "agent", "extensions", "pi-webui", "sessions");

    private static string DefaultSharedSessionsDir() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) ?? ".",
            ".pi", "agent", "sessions");

    public IReadOnlyList<Session> List() => _sessions.Values.OrderBy(s => s.Name).ToList();

    public Session? Get(string name) => _sessions.TryGetValue(name, out var s) ? s : null;

    /// <summary>
    /// List every session the browser can open, including STORED ones not currently loaded:
    /// loaded sessions first (running/recycled), then session files under the sessions dir
    /// that have no live <see cref="Session"/> (status "stored"). Any status other than
    /// "running" is resumable by re-initialising the same name (HistoryPathFor round-trips to
    /// the existing file, which <see cref="StartChildAsync"/> resumes via switch_session).
    /// </summary>
    public IReadOnlyList<SessionSummary> ListStoredSessions()
    {
        var entries = new List<(string Name, string Status, string? Title, DateTime LastWriteUtc)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Loaded sessions (running / recycled), ordered most-recent-first by their file mtime.
        foreach (var s in _sessions.Values)
        {
            var last = !string.IsNullOrEmpty(s.HistoryFilePath) && IsNonEmptyFile(s.HistoryFilePath!)
                ? File.GetLastWriteTimeUtc(s.HistoryFilePath!)
                : DateTime.UtcNow;
            entries.Add((s.Name, s.Status == SessionStatus.Running ? "running" : "recycled", s.Title, last));
            seen.Add(s.Name);
        }

        // History files currently backing a loaded session — don't also surface them as
        // duplicate "stored" entries when scanning the shared dir.
        var loadedPaths = new HashSet<string>(
            _sessions.Values.Select(s => s.HistoryFilePath ?? "").Where(p => p.Length > 0),
            StringComparer.Ordinal);

        // Register + surface an external .jsonl as a stored session. Registering the path in
        // _storedFiles lets InitAsync resume it by name via switch_session.
        void AsStored(string state, string fullPath)
        {
            if (loadedPaths.Contains(fullPath) || !seen.Add(state)) return;
            if (Get(state) is null && !_storedFiles.ContainsKey(state)) _storedFiles[state] = fullPath;
            var last = IsNonEmptyFile(fullPath) ? File.GetLastWriteTimeUtc(fullPath) : DateTime.MinValue;
            // On-the-fly display title (first user message) so a TUI session reads nicely in
            // the browser instead of a raw timestamp_uuid stem; a persisted title wins.
            var title = TitleFor(state) ?? DerivedTitle(fullPath);
            entries.Add((state, "stored", title, last));
        }

        // Webui-managed history files under the sessions dir.
        foreach (var f in StoredFiles()) AsStored(Path.GetFileNameWithoutExtension(f), f);

        // Registered out-of-dir stored sessions (pi clones).
        foreach (var (name, path) in _storedFiles) AsStored(name, path);

        // Shared standard pi sessions dir (~/.pi/agent/sessions), incl. its cwd-slug
        // subdirectories, so sessions created in the TUI (or any pi) are browsable +
        // resumable from the web — cross-TUI/web continuity.
        foreach (var f in SharedSessionsFiles()) AsStored(Path.GetFileNameWithoutExtension(f), f);

        // Most recent on top.
        return entries
            .OrderByDescending(e => e.LastWriteUtc)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .Select(e => new SessionSummary(e.Name, e.Status, e.Title))
            .ToList();
    }

    /// <summary>The persisted title for a session name, or null when none.</summary>
    private string? TitleFor(string name) =>
        _persistedTitles.TryGetValue(name, out var t) ? t : null;

    /// <summary>
    /// Set a session's title on the heap (if loaded) AND persist it to the sidecar so it
    /// survives a restart. Stored (not-loaded) sessions carry the persisted title in their
    /// summary (ticket #06).
    /// </summary>
    public void SetTitle(string name, string? title)
    {
        var s = Get(name);
        if (s is not null) s.Title = title;
        if (string.IsNullOrWhiteSpace(title)) _persistedTitles.TryRemove(name, out _);
        else _persistedTitles[name] = title;
        PersistTitles();
    }

    private void PersistTitles()
    {
        try
        {
            Directory.CreateDirectory(_sessionsDir);
            File.WriteAllText(_titlesPath, JsonSerializer.Serialize(_persistedTitles) + "\n");
        }
        catch { /* best-effort persistence; titles simply don't survive a restart */ }
    }

    private void LoadPersistedTitles()
    {
        if (!File.Exists(_titlesPath)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_titlesPath));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(p.Value.GetString()))
                    _persistedTitles[p.Name] = p.Value.GetString()!;
            }
        }
        catch { /* unreadable sidecar is ignored; titles simply start empty */ }
    }

    /// <summary>Restore a persisted title onto a freshly loaded session (startup resume).</summary>
    private void RestorePersistedTitle(Session s)
    {
        if (s.Title is null && TitleFor(s.Name) is { } t) s.Title = t;
    }

    /// <summary>
    /// Clone the named (running) session's active branch into a NEW session and register the
    /// newly created session file as a STORED session (rpc.md <c>clone</c> duplicates the
    /// active branch into a new session). After a successful clone, pi re-binds the attached
    /// child to the new session's file, so the new path is discovered reliably from a
    /// subsequent <c>get_state</c> rather than guessed by scanning directories.
    /// Returns the new session's name (the clone file's stem), or null when the clone was
    /// rejected/cancelled or the new file could not be located.
    /// </summary>
    public async Task<string?> CloneAndRegisterAsync(string name, CancellationToken ct = default)
    {
        var s = Get(name);
        if (s is null || !s.IsRunning)
            throw new InvalidOperationException($"session '{name}' is not running; initialize it first");

        var resp = await s.CloneAsync(ct).ConfigureAwait(false);
        if (resp is null || !resp.Success) return null; // rejected/cancelled -> nothing to register

        var state = await s.GetStateAsync(ct).ConfigureAwait(false);
        var file = ExtractSessionFile(state);
        if (string.IsNullOrWhiteSpace(file)) return null; // cannot locate the clone file

        var cloneName = Path.GetFileNameWithoutExtension(file);
        if (string.IsNullOrWhiteSpace(cloneName)) return null;

        // Register as a STORED (not-loaded) session so ListStoredSessions/GET /api/sessions
        // includes it and the UI can resume it by name. The attached session keeps its own
        // original identity/file; the clone is surfaced as a separate resumable stored branch.
        if (Get(cloneName) is null && !_storedFiles.ContainsKey(cloneName))
            _storedFiles[cloneName] = file;
        return cloneName;
    }

    /// <summary>The managed session files under <see cref="_sessionsDir"/> (incl. recycled, not-loaded).</summary>
    private IEnumerable<string> StoredFiles()
    {
        if (!Directory.Exists(_sessionsDir)) yield break;
        foreach (var f in Directory.EnumerateFiles(_sessionsDir, "*.jsonl", SearchOption.TopDirectoryOnly))
            yield return f;
    }

    /// <summary>
    /// Session .jsonl files under the shared standard pi sessions dir (~/.pi/agent/sessions),
    /// including its cwd-slug subdirectories. Same store the TUI uses, so a session started
    /// in the terminal can be picked up here.
    /// </summary>
    private IEnumerable<string> SharedSessionsFiles()
    {
        if (!Directory.Exists(_sharedSessionsDir)) yield break;
        foreach (var f in Directory.EnumerateFiles(_sharedSessionsDir, "*.jsonl", SearchOption.AllDirectories))
            yield return f;
    }

    /// <summary>
    /// Create the named session (or resume it if it was recycled). Idempotent for an
    /// already-running session.
    /// </summary>
    public async Task<Session> InitAsync(string name, CancellationToken ct = default)
    {
        // Serialize create/resume for this name so concurrent init on the same name never
        // spawns more than one child. Different names run in parallel (per-session gates).
        var gate = _initGates.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = Get(name);
            if (existing is not null)
            {
                if (existing.IsRunning) return existing; // already running
                await ResumeAsync(existing, ct).ConfigureAwait(false);
                return existing;
            }

            // Register; the gate above guarantees this name has no in-flight creator, so
            // GetOrAdd always wins and only one child is ever spawned.
            var session = new Session(name);
            var actual = _sessions.GetOrAdd(name, session);
            if (!ReferenceEquals(actual, session))
            {
                if (actual.IsRunning) return actual;
                await ResumeAsync(actual, ct).ConfigureAwait(false);
                return actual;
            }

            await InitializeAsync(actual, ct).ConfigureAwait(false);
            return actual;
        }
        finally
        {
            gate.Release();
        }
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
        _exports.TryRemove(name, out _);
    }

    /// <summary>Record the most recent exported transcript path for a session (ticket #08).</summary>
    public void RegisterExport(string name, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) _exports.TryRemove(name, out _);
        else _exports[name] = path;
    }

    /// <summary>The most recent exported transcript path for a session, or null when none yet.</summary>
    public string? GetExportPath(string name) =>
        _exports.TryGetValue(name, out var p) ? p : null;

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _sessions.Values)
            await s.DisposeAsync().ConfigureAwait(false);
        _sessions.Clear();
    }

    // --- internals -----------------------------------------------------------

    /// <summary>
    /// Brand-new session: spawn a fresh child and learn its history file path. On any
    /// failure the half-initialized registration from <see cref="InitAsync"/> is
    /// rolled back so a failed init is never reported as running and a retry can
    /// create a fresh session instead.
    /// </summary>
    private async Task InitializeAsync(Session s, CancellationToken ct)
    {
        // A brand-new name may correspond to a previously registered stored session (a pi
        // clone) whose file lives outside the sessions dir: resume from that exact file.
        var candidate = _storedFiles.TryGetValue(s.Name, out var stored) ? stored : HistoryPathFor(s.Name);
        RestorePersistedTitle(s);
        try
        {
            await StartChildAsync(s, candidate, ct).ConfigureAwait(false);
        }
        catch
        {
            // Remove the registration added in InitAsync (the failed child was already
            // detached/disposed by StartChildAsync). A subsequent init starts fresh.
            _sessions.TryRemove(s.Name, out _);
            throw;
        }
    }

    /// <summary>Resume a recycled session: spawn a fresh child pointed at its history file.</summary>
    private async Task ResumeAsync(Session s, CancellationToken ct)
    {
        RestorePersistedTitle(s);
        await StartChildAsync(s, s.HistoryFilePath, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Shared init/resume core: spawn a fresh child for the session and point it at
    /// (or discover) the history file. If <paramref name="candidatePath"/> resolves to
    /// a non-empty file the fresh child resumes it via <c>switch_session</c>; otherwise
    /// the child's own <c>get_state</c> sessionFile is discovered (falling back to the
    /// candidate path). On any failure the freshly attached child is detached and
    /// disposed so a failed start/command never leaves a half-attached running session.
    /// </summary>
    private async Task StartChildAsync(Session s, string? candidatePath, CancellationToken ct)
    {
        var client = _clientFactory();
        s.AttachClient(client);
        try
        {
            client.Start();

            // A fresh session with no preserved history is auto-titled from its first user
            // message (ticket #06); a session resumed from history keeps its identity and is
            // not re-titled. Records the flag for the WS bridge to fire on the first prompt.
            s.AutoTitlePending = candidatePath is null || !IsNonEmptyFile(candidatePath);

            if (candidatePath is not null && IsNonEmptyFile(candidatePath))
            {
                // A preserved history file exists (e.g. across a server restart, or a
                // previously recycled session): point the fresh child at it so history
                // is resumed.
                s.HistoryFilePath = candidatePath;
                await client.SendAsync(new SwitchSessionCommand(candidatePath), ct).ConfigureAwait(false);
            }
            else
            {
                // No preserved file: discover the child's session file so recycle/delete
                // can preserve/remove the right path; fall back to the candidate path.
                var resp = await client.SendAsync(new GetStateCommand(), ct).ConfigureAwait(false);
                s.HistoryFilePath = ExtractSessionFile(resp) ?? candidatePath;
            }
        }
        catch
        {
            // Detach + dispose the freshly attached child (best-effort) so a failed
            // start/command doesn't leave a dead child bound to this session.
            await s.RecycleAsync().ConfigureAwait(false);
            throw;
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

    /// <summary>
    /// A display title derived from a stored/shared session's first user message (the
    /// auto-title "safe fallback" style), read straight from the .jsonl without loading pi.
    /// Cached per file. Returns null when nothing usable is extracted.
    /// </summary>
    private string? DerivedTitle(string fullPath)
    {
        if (_derivedTitles.TryGetValue(fullPath, out var cached)) return string.IsNullOrEmpty(cached) ? null : cached;
        var derived = FirstUserMessage(fullPath) ?? "";
        _derivedTitles[fullPath] = derived;
        return string.IsNullOrEmpty(derived) ? null : derived;
    }

    /// <summary>The truncated text of a session file's first user message, or null.</summary>
    private static string? FirstUserMessage(string file)
    {
        try
        {
            foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var isUser =
                    (root.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String && r.GetString() == "user")
                    || (root.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.String && ty.GetString() == "user");
                if (!isUser) continue;
                if (root.TryGetProperty("content", out var content))
                {
                    var text = FirstText(content);
                    if (!string.IsNullOrWhiteSpace(text)) return Truncate(text.Trim(), 60);
                }
            }
        }
        catch { /* unparseable / partial file -> fall back to no derived title */ }
        return null;
    }

    /// <summary>First plain-text block of a session content value (string or block array).</summary>
    private static string FirstText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var blk in content.EnumerateArray())
            {
                if (blk.ValueKind != JsonValueKind.Object) continue;
                if (blk.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "text"
                    && blk.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String)
                    return txt.GetString() ?? "";
            }
        }
        return "";
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";


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

/// <summary>
/// A session as shown in the browser list (ticket #06). <see cref="Status"/> is one of
/// "running", "recycled" (loaded but stopped, history preserved), or "stored" (a history
/// file exists under the sessions dir but no session is loaded). Any status other than
/// "running" can be resumed by re-initialising the same name.
/// </summary>
public sealed record SessionSummary(string Name, string Status, string? Title);

using System.Text;
using System.Text.Json;

namespace PiWebui.Session;

/// <summary>
/// Seam for generating a short session title from the first user message (ticket #06).
/// The real implementation calls the box's default model endpoint; tests inject a stub.
/// </summary>
public interface ITitleGenerator
{
    /// <summary>
    /// Generate a short title (e.g. "Casual greeting") for a session from its first user
    /// message. Return <c>null</c> (or throw) on any failure/timeout — the caller applies
    /// the safe truncated-first-message fallback. Must never be awaited by the first turn.
    /// </summary>
    Task<string?> GenerateTitleAsync(string firstMessage, CancellationToken ct = default);
}

/// <summary>
/// Orchestrates title generation with a safe fallback. Non-blocking by contract: callers
/// fire <see cref="GenerateAsync"/> and forget — it never delays the agent's first turn.
/// </summary>
public sealed class SessionAutoTitler
{
    private readonly ITitleGenerator _generator;

    public SessionAutoTitler(ITitleGenerator generator) => _generator = generator;

    /// <summary>
    /// Generate a title from <paramref name="firstMessage"/>, applying the fallback when the
    /// generator fails, times out, or returns nothing. Never throws.
    /// </summary>
    public async Task<string> GenerateAsync(string firstMessage, CancellationToken ct = default)
    {
        try
        {
            var title = await _generator.GenerateTitleAsync(firstMessage, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(title)) return title.Trim();
        }
        catch
        {
            // generator failure/timeout -> fall through to the safe fallback
        }
        return Fallback(firstMessage);
    }

    /// <summary>
    /// Safe fallback title: the truncated first message + a timestamp, so the session list is
    /// scannable even when the model is unreachable, pending, or slow.
    /// </summary>
    public static string Fallback(string firstMessage)
    {
        const int Max = 40;
        var msg = (firstMessage ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        var truncated = msg.Length <= Max ? msg : msg[..Max] + "…";
        if (string.IsNullOrWhiteSpace(truncated)) return "Untitled session";
        return $"{truncated} · {DateTime.Now:yyyy-MM-dd HH:mm}";
    }
}

/// <summary>
/// A resolved default-model endpoint: the <c>work</c> provider from pi's models.json
/// (<c>api</c>/<c>baseUrl</c>/<c>apiKey</c> + its first model). Encapsulates the 4 values
/// that used to travel as a tuple so the endpoint travels as one value (nullable = none).
/// </summary>
public sealed record TitleEndpoint(string Api, string BaseUrl, string ApiKey, string Model);

/// <summary>
/// Real title generator: builds a short, no-tools completion at the box's default model
/// endpoint. The endpoint is resolved from pi's <c>~/.pi/agent/models.json</c> "work"
/// provider (<c>api</c>/<c>baseUrl</c>/<c>apiKey</c>/first model). When no endpoint can be
/// resolved (e.g. a box with no reachable/configured model) it returns <c>null</c> so the
/// caller applies the truncated-first-message fallback — it NO LONGER invents a localhost
/// default that would silently misdirect the request to a non-existent local Ollama.
/// </summary>
public sealed class TitleGenerator : ITitleGenerator
{
    private static readonly HttpClient Http = new();
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private readonly string _modelsPath;
    private readonly bool _skipConfig;
    private readonly TimeSpan _timeout;

    public TitleGenerator(string? modelsPath = null, TimeSpan? timeout = null)
    {
        _modelsPath = modelsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) ?? ".",
            ".pi", "agent", "models.json");
        _timeout = timeout ?? DefaultTimeout;
        _skipConfig = false;
    }

    /// <summary>
    /// Test seam: constructs a generator that skips config resolution (simulating a box with
    /// no resolvable model endpoint) so <see cref="ResolveEndpoint"/> always returns null and
    /// the truncated-message fallback is exercised end-to-end.
    /// </summary>
    public TitleGenerator(bool skipConfig, TimeSpan? timeout = null)
    {
        _modelsPath = string.Empty;
        _timeout = timeout ?? DefaultTimeout;
        _skipConfig = skipConfig;
    }

    public async Task<string?> GenerateTitleAsync(string firstMessage, CancellationToken ct = default)
    {
        var endpoint = ResolveEndpoint();
        if (endpoint is null) return null; // no endpoint -> caller applies the truncated fallback

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(_timeout);
        try
        {
            return endpoint.Api == "anthropic-messages"
                ? await CallAnthropicAsync(endpoint, firstMessage, linked.Token).ConfigureAwait(false)
                : await CallOpenAIAsync(endpoint, firstMessage, linked.Token).ConfigureAwait(false);
        }
        catch
        {
            return null; // any failure/timeout -> caller uses the safe fallback
        }
    }

    /// <summary>
    /// Resolve the box's actual default model endpoint from pi's models.json ("work"
    /// provider), or <c>null</c> when none can be resolved. The container server runs as
    /// <c>yolo</c>, so both the process-home path and the yolo home path are checked.
    /// </summary>
    public TitleEndpoint? ResolveEndpoint()
    {
        if (_skipConfig) return null;
        foreach (var path in CandidateModelsPaths())
        {
            if (!File.Exists(path)) continue;
            var ep = TryResolveWorkProvider(path);
            if (ep is not null) return ep;
        }
        return null;
    }

    /// <summary>
    /// The models.json candidates: the configured path first, plus the container's <c>yolo</c>
    /// home when the configured path is the process-user default (the server runs as
    /// <c>yolo</c>, not necessarily the process user — so its config must be checked too).
    /// </summary>
    private IEnumerable<string> CandidateModelsPaths()
    {
        yield return _modelsPath;
        const string yoloHome = "/home/yolo/.pi/agent/models.json";
        var userDefault = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) ?? ".",
            ".pi", "agent", "models.json");
        if (string.Equals(Path.GetFullPath(_modelsPath), Path.GetFullPath(userDefault), StringComparison.Ordinal)
            && !string.Equals(Path.GetFullPath(yoloHome), Path.GetFullPath(userDefault), StringComparison.Ordinal))
            yield return yoloHome;
    }

    /// <summary>Parse one models.json for a resolvable "work" provider endpoint.</summary>
    private static TitleEndpoint? TryResolveWorkProvider(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (!root.TryGetProperty("providers", out var providers)
                || providers.ValueKind != JsonValueKind.Object
                || !providers.TryGetProperty("work", out var work)
                || work.ValueKind != JsonValueKind.Object)
                return null;

            string api = "openai-completions";
            if (work.TryGetProperty("api", out var apiProp) && apiProp.ValueKind == JsonValueKind.String)
                api = apiProp.GetString()!;

            string baseUrl = string.Empty;
            if (work.TryGetProperty("baseUrl", out var baseProp) && baseProp.ValueKind == JsonValueKind.String)
                baseUrl = baseProp.GetString()!;

            string apiKey = string.Empty;
            if (work.TryGetProperty("apiKey", out var keyProp) && keyProp.ValueKind == JsonValueKind.String)
                apiKey = keyProp.GetString()!;

            string model = ResolveFirstModel(work);
            return !string.IsNullOrWhiteSpace(baseUrl) && !string.IsNullOrWhiteSpace(model)
                ? new TitleEndpoint(api, baseUrl, apiKey, model)
                : null;
        }
        catch
        {
            // unreadable config -> no endpoint -> truncated fallback
            return null;
        }
    }

    private static string ResolveFirstModel(JsonElement provider)
    {
        if (provider.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in models.EnumerateArray())
            {
                if (m.ValueKind == JsonValueKind.Object
                    && m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(id.GetString()))
                    return id.GetString()!;
            }
        }
        return string.Empty;
    }

    private static async Task<string?> CallOpenAIAsync(TitleEndpoint ep, string message, CancellationToken ct)
    {
        var url = ep.BaseUrl.TrimEnd('/') + "/chat/completions";
        var body = JsonSerializer.Serialize(new
        {
            model = ep.Model,
            temperature = 0,
            max_tokens = 24,
            messages = new object[]
            {
                new { role = "system", content = "Reply with only a short title (at most 6 words) that summarises the user's message. No quotes, no leading/trailing punctuation." },
                new { role = "user", content = message },
            },
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {ep.ApiKey}");
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var root = doc.RootElement;
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object
            && msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            return content.GetString();
        return null;
    }

    private static async Task<string?> CallAnthropicAsync(TitleEndpoint ep, string message, CancellationToken ct)
    {
        var url = ep.BaseUrl.TrimEnd('/') + "/v1/messages";
        var body = JsonSerializer.Serialize(new
        {
            model = ep.Model,
            max_tokens = 24,
            temperature = 0,
            system = "Reply with only a short title (at most 6 words) that summarises the user's message. No quotes, no leading/trailing punctuation.",
            messages = new[] { new { role = "user", content = message } },
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.TryAddWithoutValidation("x-api-key", ep.ApiKey);
        req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var root = doc.RootElement;
        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.Object
                    && block.TryGetProperty("type", out var t) && t.GetString() == "text"
                    && block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    return text.GetString();
            }
        }
        return null;
    }
}

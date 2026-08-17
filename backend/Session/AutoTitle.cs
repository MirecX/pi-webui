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
/// Real title generator: builds a short, no-tools completion at the box's default model
/// endpoint. The endpoint is resolved from pi's <c>~/.pi/agent/models.json</c> "work"
/// provider (<c>baseUrl</c>/<c>apiKey</c>/first model); it falls back to a documented
/// default (a lightweight OpenAI-compatible local endpoint) and returns <c>null</c> on any
/// failure so the caller applies the truncated-first-message fallback.
/// </summary>
public sealed class TitleGenerator : ITitleGenerator
{
    private static readonly HttpClient Http = new();
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private readonly string _modelsPath;
    private readonly bool _forceDefault;
    private readonly TimeSpan _timeout;

    // "documented default": matches the pi models.md minimal example (Ollama-style OpenAI
    // compatible local endpoint). Only used when no work provider / config can be resolved.
    private const string DocDefaultApi = "openai-completions";
    private const string DocDefaultBaseUrl = "http://localhost:11434/v1";
    private const string DocDefaultApiKey = "ollama";
    private const string DocDefaultModel = "llama3.1:8b";

    public TitleGenerator(string? modelsPath = null, TimeSpan? timeout = null)
    {
        _modelsPath = modelsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) ?? ".",
            ".pi", "agent", "models.json");
        _timeout = timeout ?? DefaultTimeout;
        _forceDefault = false;
    }

    /// <summary>
    /// Models.json <c>work</c>-provider endpoint resolved from the box config. Constructing
    /// with this flag skips config resolution and always uses the documented default (tests).
    /// </summary>
    public TitleGenerator(bool forceDefault, TimeSpan? timeout = null)
    {
        _modelsPath = string.Empty;
        _timeout = timeout ?? DefaultTimeout;
        _forceDefault = forceDefault;
    }

    public async Task<string?> GenerateTitleAsync(string firstMessage, CancellationToken ct = default)
    {
        var (api, baseUrl, apiKey, model) = ResolveEndpoint();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(_timeout);
        try
        {
            return api == "anthropic-messages"
                ? await CallAnthropicAsync(baseUrl, model, apiKey, firstMessage, linked.Token).ConfigureAwait(false)
                : await CallOpenAIAsync(baseUrl, model, apiKey, firstMessage, linked.Token).ConfigureAwait(false);
        }
        catch
        {
            return null; // any failure/timeout -> caller uses the safe fallback
        }
    }

    /// <summary>Resolve the box default model endpoint from models.json ("work" provider).</summary>
    public (string Api, string BaseUrl, string ApiKey, string Model) ResolveEndpoint()
    {
        if (!_forceDefault && File.Exists(_modelsPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(_modelsPath));
                var root = doc.RootElement;
                if (root.TryGetProperty("providers", out var providers)
                    && providers.ValueKind == JsonValueKind.Object
                    && providers.TryGetProperty("work", out var work)
                    && work.ValueKind == JsonValueKind.Object)
                {
                    string api = DocDefaultApi;
                    if (work.TryGetProperty("api", out var apiProp) && apiProp.ValueKind == JsonValueKind.String)
                        api = apiProp.GetString()!;

                    string baseUrl = string.Empty;
                    if (work.TryGetProperty("baseUrl", out var baseProp) && baseProp.ValueKind == JsonValueKind.String)
                        baseUrl = baseProp.GetString()!;

                    string apiKey = string.Empty;
                    if (work.TryGetProperty("apiKey", out var keyProp) && keyProp.ValueKind == JsonValueKind.String)
                        apiKey = keyProp.GetString()!;

                    string model = ResolveFirstModel(work);

                    if (!string.IsNullOrWhiteSpace(baseUrl) && !string.IsNullOrWhiteSpace(model))
                        return (api, baseUrl, apiKey, model);
                }
            }
            catch
            {
                // unreadable config -> documented default below
            }
        }
        return (DocDefaultApi, DocDefaultBaseUrl, DocDefaultApiKey, DocDefaultModel);
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

    private static async Task<string?> CallOpenAIAsync(string baseUrl, string model, string apiKey, string message, CancellationToken ct)
    {
        var url = baseUrl.TrimEnd('/') + "/chat/completions";
        var body = JsonSerializer.Serialize(new
        {
            model,
            temperature = 0,
            max_tokens = 24,
            messages = new object[]
            {
                new { role = "system", content = "Reply with only a short title (at most 6 words) that summarises the user's message. No quotes, no leading/trailing punctuation." },
                new { role = "user", content = message },
            },
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
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

    private static async Task<string?> CallAnthropicAsync(string baseUrl, string model, string apiKey, string message, CancellationToken ct)
    {
        var url = baseUrl.TrimEnd('/') + "/v1/messages";
        var body = JsonSerializer.Serialize(new
        {
            model,
            max_tokens = 24,
            temperature = 0,
            system = "Reply with only a short title (at most 6 words) that summarises the user's message. No quotes, no leading/trailing punctuation.",
            messages = new[] { new { role = "user", content = message } },
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.TryAddWithoutValidation("x-api-key", apiKey);
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

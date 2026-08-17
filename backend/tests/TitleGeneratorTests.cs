using System.Text.Json;
using PiWebui.Session;
using Xunit;

namespace PiWebui.Tests;

/// <summary>
/// Ticket #06 (code review) — auto-title endpoint resolution. The real generator must
/// resolve the box's ACTUAL default model endpoint from pi's models.json ("work" provider)
/// and return <c>null</c> (→ truncated-message fallback) ONLY when no endpoint resolves. It
/// must NOT invent a localhost-only default that silently misdirects auto-titles on a box
/// with no local model.
/// </summary>
public class TitleGeneratorTests
{
    private static string WriteModelsJson(string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"piwebui-models-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "models.json");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void ResolveEndpoint_returns_work_provider_endpoint_when_models_json_present()
    {
        var path = WriteModelsJson(JsonSerializer.Serialize(new
        {
            providers = new Dictionary<string, object>
            {
                ["work"] = new
                {
                    api = "anthropic-messages",
                    baseUrl = "https://api.anthropic.com",
                    apiKey = "sk-test",
                    models = new object[]
                    {
                        new { id = "claude-sonnet-4-5" },
                        new { id = "claude-opus-4-5" },
                    },
                },
            },
        }));
        try
        {
            var gen = new TitleGenerator(path);
            var ep = gen.ResolveEndpoint();

            Assert.NotNull(ep);
            Assert.Equal("anthropic-messages", ep!.Api);
            Assert.Equal("https://api.anthropic.com", ep.BaseUrl);
            Assert.Equal("sk-test", ep.ApiKey);
            Assert.Equal("claude-sonnet-4-5", ep.Model); // FIRST model of the provider
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(path)!))
                Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void ResolveEndpoint_returns_null_when_no_models_json_exists()
    {
        var path = Path.Combine(Path.GetTempPath(), $"piwebui-missing-{Guid.NewGuid():N}", "models.json");
        try
        {
            var gen = new TitleGenerator(path);
            Assert.Null(gen.ResolveEndpoint()); // NO invented localhost default
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(path)!))
                Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void ResolveEndpoint_returns_null_when_work_provider_has_no_resolvable_baseUrl_or_model()
    {
        var path = WriteModelsJson("{\"providers\":{\"work\":{\"apiKey\":\"x\"}}}");
        try
        {
            var gen = new TitleGenerator(path);
            Assert.Null(gen.ResolveEndpoint());
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(path)!))
                Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void ResolveEndpoint_returns_null_when_config_skipped()
    {
        // test seam: simulates a box with no resolvable endpoint
        Assert.Null(new TitleGenerator(skipConfig: true).ResolveEndpoint());
    }

    [Fact]
    public async Task No_endpoint_yields_truncated_message_fallback_without_crash()
    {
        // absent models.json -> no endpoint -> GenerateAsync applies the safe fallback
        var gen = new TitleGenerator(Path.Combine(Path.GetTempPath(), $"piwebui-none-{Guid.NewGuid():N}", "models.json"));
        var titler = new SessionAutoTitler(gen);

        var title = await titler.GenerateAsync("Yo! How are you today?");

        Assert.StartsWith("Yo! How are you today?", title);
        Assert.Contains("·", title);
    }
}

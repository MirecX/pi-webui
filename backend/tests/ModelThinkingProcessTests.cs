using PiWebui.Rpc;
using Xunit;

namespace PiWebui.Tests;

/// <summary>
/// End-to-end framing seam for the model/thinking commands (ticket #04), backed by the
/// scripted fake pi. Verifies get_available_models / get_available_thinking_levels
/// round-trip their scripted data through the JSONL pipe with id-correlation, and that
/// set_model / set_thinking_level are acknowledged with the correct command names.
/// </summary>
public class ModelThinkingProcessTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    [Fact]
    public async Task Model_and_thinking_commands_roundtrip_through_jsonl_pipe()
    {
        // The fake pi acknowledges EVERY command type generically and emits scripted
        // correlated responses (with data) for the available-lists, so these commands are
        // exercised with real framing + id correlation.
        var scenario = Path.Combine(Path.GetTempPath(), $"fake-pi-model-{Guid.NewGuid():N}.json");
        File.WriteAllText(scenario, string.Join("\n", new[]
        {
            @"{""when"":""get_available_models"",""emit"":[{""type"":""response"",""id"":""r-m"",""command"":""get_available_models"",""success"":true,""data"":{""models"":[{""id"":""claude-3-5-sonnet"",""provider"":""anthropic"",""name"":""Claude""}]}}]}",
            @"{""when"":""get_available_thinking_levels"",""emit"":[{""type"":""response"",""id"":""r-t"",""command"":""get_available_thinking_levels"",""success"":true,""data"":{""levels"":[""off"",""medium"",""high""]}}]}",
        }));

        try
        {
            await using var client = new PiRpcClient(new PiClientOptions
            {
                Executable = "node",
                Arguments = new[] { FixturePath("fake-pi.mjs") },
                WorkingDirectory = Path.GetDirectoryName(FixturePath("fake-pi.mjs"))!,
                Environment = new Dictionary<string, string> { ["FAKE_PI_SCENARIO"] = scenario },
            });
            client.Start();

            // available models list round-trips with its scripted data
            var models = await client.SendAsync(
                new GetAvailableModelsCommand { Id = "r-m" }).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(models);
            Assert.True(models!.Success);
            Assert.Equal("get_available_models", models.Command);
            Assert.NotNull(models.Data);
            Assert.True(models.Data!.Value.TryGetProperty("models", out var ml));
            Assert.Contains("claude-3-5-sonnet", ml.GetRawText());

            // set_model / set_thinking_level are acknowledged with correct command names
            var setModel = await client.SendAsync(
                new SetModelCommand("anthropic", "claude-3-5-sonnet") { Id = "r-sm" }).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(setModel);
            Assert.True(setModel!.Success);
            Assert.Equal("set_model", setModel.Command);

            var setLevel = await client.SendAsync(
                new SetThinkingLevelCommand("high") { Id = "r-sl" }).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(setLevel);
            Assert.True(setLevel!.Success);
            Assert.Equal("set_thinking_level", setLevel.Command);

            // available thinking levels list round-trips with its scripted data
            var levels = await client.SendAsync(
                new GetAvailableThinkingLevelsCommand { Id = "r-t" }).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(levels);
            Assert.True(levels!.Success);
            Assert.NotNull(levels.Data);
            Assert.True(levels.Data!.Value.TryGetProperty("levels", out var ll));
            Assert.Contains("\"high\"", ll.GetRawText());
        }
        finally
        {
            File.Delete(scenario);
        }
    }
}

using Xunit;

namespace PiWebui.Tests;

public class ConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"piwebui-cfg-{Guid.NewGuid():N}");
    private string PathFor(string name) => Path.Combine(_dir, name);

    public ConfigTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Generates_and_persists_token_on_first_run()
    {
        var path = PathFor("config.json");
        var cfg = Config.Load(path);

        Assert.Equal(Config.DefaultPort, cfg.Port);
        Assert.Equal(64, cfg.Token.Length); // 32 random bytes hex
        Assert.True(File.Exists(path));

        var onDisk = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        Assert.Equal(cfg.Token, onDisk.GetProperty("token").GetString());
    }

    [Fact]
    public void Reuses_existing_token_on_reload()
    {
        var path = PathFor("config.json");
        var first = Config.Load(path);
        var second = Config.Load(path);
        Assert.Equal(first.Token, second.Token);
    }

    [Fact]
    public void Reads_token_and_port_from_existing_file()
    {
        var path = PathFor("config.json");
        File.WriteAllText(path, "{\"token\":\"abc123\",\"port\":9999}\n");
        var cfg = Config.Load(path);
        Assert.Equal("abc123", cfg.Token);
        Assert.Equal(9999, cfg.Port);
    }

    [Fact]
    public void Generates_token_when_existing_file_has_none()
    {
        var path = PathFor("config.json");
        File.WriteAllText(path, "{\"port\":8000}\n"); // no token
        var cfg = Config.Load(path);
        Assert.Equal(8000, cfg.Port);
        Assert.Equal(64, cfg.Token.Length);
    }

    [Fact]
    public void Default_binds_localhost_only()
    {
        var cfg = new Config("t", Config.DefaultPort);
        Assert.False(cfg.External);
        Assert.Equal("127.0.0.1", cfg.BindHost);
    }

    [Fact]
    public void External_opt_in_binds_all_interfaces()
    {
        var cfg = new Config("t", Config.DefaultPort, External: true);
        Assert.True(cfg.External);
        Assert.Equal("0.0.0.0", cfg.BindHost);
    }

    [Fact]
    public void Reads_and_persists_external_flag()
    {
        var path = PathFor("config.json");
        var cfg = Config.Load(path); // first run: default localhost
        Assert.False(cfg.External);

        // user opts in by editing the file
        File.WriteAllText(path, "{\"token\":\"abc\",\"port\":8456,\"external\":true}\n");
        var reloaded = Config.Load(path);
        Assert.True(reloaded.External);
        Assert.Equal("0.0.0.0", reloaded.BindHost);
    }
}

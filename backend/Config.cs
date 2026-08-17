using System.Text;
using System.Text.Json;

namespace PiWebui;

/// <summary>
/// Service configuration. Container-agnostic, stored as JSON at
/// ~/.pi/agent/extensions/pi-webui/config.json (falling back to ./config.json).
/// Fields: { token, port }.
/// The token is auto-generated + printed once on first run. Auth enforcement is
/// ticket #02; here we only store/generate it.
/// </summary>
public sealed record Config(string Token, int Port)
{
    public const int DefaultPort = 8456;

    /// <summary>Primary config path under the user's home (extension dir).</summary>
    public static string HomeConfigPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) ?? ".",
            ".pi", "agent", "extensions", "pi-webui", "config.json");

    /// <summary>Repo-fallback config path (cwd).</summary>
    public static string RepoConfigPath => Path.Combine(Directory.GetCurrentDirectory(), "config.json");

    public static Config Load(string? explicitPath = null)
    {
        var path = explicitPath ?? ResolvePath();
        var dir = Path.GetDirectoryName(path);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        Config cfg;
        if (File.Exists(path))
        {
            cfg = ReadFile(path);
        }
        else
        {
            cfg = new Config(GenerateToken(), DefaultPort);
            Save(path, cfg);
            Console.WriteLine($"[pi-webui] wrote new config to {path} (token printed below once)");
            Console.WriteLine($"[pi-webui] token: {cfg.Token}");
        }
        return cfg;
    }

    /// <summary>
    /// Priority: primary home path if it exists, else repo ./config.json if it
    /// exists, else the primary home path (which will be created).
    /// </summary>
    private static string ResolvePath()
    {
        var home = HomeConfigPath;
        if (File.Exists(home)) return home;
        var repo = RepoConfigPath;
        if (File.Exists(repo)) return repo;
        return home;
    }

    private static Config ReadFile(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var token = GetString(root, "token");
        var port = GetInt(root, "port") ?? DefaultPort;
        if (token is null)
        {
            // config exists but has no token yet -> generate + persist
            var c = new Config(GenerateToken(), port);
            Save(path, c);
            Console.WriteLine($"[pi-webui] generated token for existing config {path} (printed once)");
            Console.WriteLine($"[pi-webui] token: {c.Token}");
            return c;
        }
        return new Config(token, port);
    }

    private static void Save(string path, Config cfg)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir is not null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path,
            JsonSerializer.Serialize(new { token = cfg.Token, port = cfg.Port },
                new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    private static string GenerateToken()
    {
        var bytes = new byte[32];
        Random.Shared.NextBytes(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? GetInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
}

using System.Text;
using System.Text.Json;
using static PiWebui.JsonElementUtils;

namespace PiWebui;

/// <summary>
/// Service configuration. Container-agnostic, stored as JSON at
/// ~/.pi/agent/extensions/pi-webui/config.json (falling back to ./config.json).
/// Fields: { token, port, external }.
/// The token is auto-generated + printed once on first run. It is enforced on
/// every HTTP request and WebSocket handshake by TokenAuthMiddleware (see
/// backend/Web/TokenAuth.cs); this record only stores/generates the token.
/// </summary>
public sealed record Config(string Token, int Port, bool External = false)
{
    public const int DefaultPort = 8456;

    /// <summary>
    /// Host the listener binds. Safe by default: 127.0.0.1 (localhost) unless the
    /// config explicitly opts into the external interface (0.0.0.0) so the
    /// container's external port mapping (HOST_PORT + 10000) becomes reachable.
    /// </summary>
    public string BindHost => External ? "0.0.0.0" : "127.0.0.1";

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
            cfg = GenerateAndPersist(path, DefaultPort, external: false,
                $"[pi-webui] wrote new config to {path} (token printed below once)");
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
        var external = GetBool(root, "external") ?? false;
        if (token is null)
        {
            // config exists but has no token yet -> generate + persist
            return GenerateAndPersist(path, port, external,
                $"[pi-webui] generated token for existing config {path} (printed once)");
        }
        return new Config(token, port, external);
    }

    /// <summary>Generate, persist, and print a fresh token (shared first-run path).</summary>
    private static Config GenerateAndPersist(string path, int port, bool external, string firstLine)
    {
        var cfg = new Config(GenerateToken(), port, external);
        Save(path, cfg);
        Console.WriteLine(firstLine);
        Console.WriteLine($"[pi-webui] token: {cfg.Token}");
        return cfg;
    }

    private static void Save(string path, Config cfg)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir is not null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path,
            JsonSerializer.Serialize(new { token = cfg.Token, port = cfg.Port, external = cfg.External },
                new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    private static string GenerateToken()
    {
        var bytes = new byte[32];
        Random.Shared.NextBytes(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

using System.Text.Json;

namespace PiWebui;

/// <summary>
/// Small shared helpers for reading optional fields off a <see cref="JsonElement"/>.
/// Used by both the RPC event parser and config loading to avoid duplicating the
/// same "try-get-property, guard the value kind, clone if present" logic.
/// </summary>
public static class JsonElementUtils
{
    /// <summary>Return a cloned property value, or null when absent. Cloning lets the
    /// value outlive the source <see cref="JsonDocument"/>.</summary>
    public static JsonElement? Prop(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) ? v.Clone() : null;

    /// <summary>Return the string value of <paramref name="name"/>, or null when absent
    /// or not a string.</summary>
    public static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Return the boolean value of <paramref name="name"/>, or null when absent
    /// or not a boolean.</summary>
    public static bool? GetBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;

    /// <summary>Return the integer value of <paramref name="name"/>, or null when absent
    /// or not a number.</summary>
    public static int? GetInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
}

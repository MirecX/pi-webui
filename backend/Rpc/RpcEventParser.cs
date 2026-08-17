using System.Text.Json;

namespace PiWebui.Rpc;

/// <summary>
/// Parses one JSON line of the pi RPC protocol into a typed <see cref="RpcEvent"/>.
/// Nested payloads (messages, args, results ...) are kept as cloned
/// <see cref="JsonElement"/> values so they survive disposal of the source
/// JsonDocument. The original line is preserved on <see cref="RpcEvent.Raw"/>.
/// </summary>
public static class RpcEventParser
{
    public static RpcEvent Parse(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var type = GetString(root, "type");

        RpcEvent ev = type switch
        {
            "agent_start" => new AgentStartEvent(),
            "agent_end" => new AgentEndEvent(Prop(root, "messages"), GetBool(root, "willRetry") ?? false),
            "agent_settled" => new AgentSettledEvent(),
            "turn_start" => new TurnStartEvent(),
            "turn_end" => new TurnEndEvent(Prop(root, "message"), Prop(root, "toolResults")),
            "message_start" => new MessageStartEvent(Prop(root, "message")),
            "message_end" => new MessageEndEvent(Prop(root, "message")),
            "message_update" => ParseMessageUpdate(root),
            "bash_execution_update" => new BashExecutionUpdateEvent(GetString(root, "id"), GetString(root, "delta")),
            "tool_execution_start" => new ToolExecutionStartEvent(
                GetString(root, "toolCallId"), GetString(root, "toolName"), Prop(root, "args")),
            "tool_execution_update" => new ToolExecutionUpdateEvent(
                GetString(root, "toolCallId"), GetString(root, "toolName"), Prop(root, "args"), Prop(root, "partialResult")),
            "tool_execution_end" => new ToolExecutionEndEvent(
                GetString(root, "toolCallId"), GetString(root, "toolName"), Prop(root, "result"), GetBool(root, "isError") ?? false),
            "queue_update" => new QueueUpdateEvent(Prop(root, "steering"), Prop(root, "followUp")),
            "extension_error" => new ExtensionErrorEvent(
                GetString(root, "extensionPath"), GetString(root, "event"), GetString(root, "error")),
            "extension_ui_request" => new ExtensionUiRequestEvent(
                GetString(root, "id"), GetString(root, "method"), GetString(root, "title"),
                GetString(root, "message"), GetString(root, "placeholder"), GetString(root, "prefill"),
                Prop(root, "options")),
            "response" => new ResponseEvent(
                GetString(root, "id"), GetString(root, "command") ?? "", GetBool(root, "success") ?? false,
                GetString(root, "error"), Prop(root, "data")),
            _ => new UnknownEvent(type),
        };
        ev.Raw = line;
        return ev;
    }

    private static MessageUpdateEvent ParseMessageUpdate(JsonElement root)
    {
        JsonElement? ev = null;
        if (root.TryGetProperty("assistantMessageEvent", out var msg) && msg.ValueKind == JsonValueKind.Object)
            ev = msg.Clone();

        string? deltaType = null, delta = null;
        if (ev is { } e && e.TryGetProperty("type", out var t))
            deltaType = t.GetString();
        if (deltaType is "text_delta" or "thinking_delta" && ev is { } ed && ed.TryGetProperty("delta", out var d))
            delta = d.GetString();

        return new MessageUpdateEvent(Prop(root, "message"), ev, deltaType, delta);
    }

    private static JsonElement? Prop(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) ? v.Clone() : null;

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? GetBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;
}

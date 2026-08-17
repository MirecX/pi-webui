using System.Text;
using System.Text.Json;

namespace PiWebui.Rpc;

// ---------------------------------------------------------------------------
// Commands
// ---------------------------------------------------------------------------

/// <summary>A command written to the pi RPC child's stdin (one JSON line).</summary>
public abstract record RpcCommand
{
    public string? Id { get; init; }
    public abstract string Type { get; }

    /// <summary>Serialise the command as a single JSON line for stdin.</summary>
    public string ToJson(string? id = null)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            if (id is not null) w.WriteString("id", id);
            w.WriteString("type", Type);
            WriteExtra(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    protected virtual void WriteExtra(Utf8JsonWriter w) { }
}

public sealed record PromptCommand(string Message, string? StreamingBehavior = null) : RpcCommand
{
    public override string Type => "prompt";
    protected override void WriteExtra(Utf8JsonWriter w)
    {
        w.WriteString("message", Message);
        if (StreamingBehavior is not null) w.WriteString("streamingBehavior", StreamingBehavior);
    }
}

public sealed record AbortCommand : RpcCommand
{
    public override string Type => "abort";
}

public sealed record GetStateCommand : RpcCommand
{
    public override string Type => "get_state";
}

// ---------------------------------------------------------------------------
// Response + events
// ---------------------------------------------------------------------------

/// <summary>Base type for everything that comes back on pi's stdout.</summary>
public abstract record RpcEvent
{
    public abstract string Type { get; }
    /// <summary>The original JSON line as received (used for faithful WS relay).</summary>
    public string Raw { get; set; } = "";
}

public sealed record ResponseEvent(string? Id, string Command, bool Success, string? Error, JsonElement? Data) : RpcEvent
{
    public override string Type => "response";
}

public sealed record AgentStartEvent : RpcEvent
{
    public override string Type => "agent_start";
}

public sealed record AgentEndEvent(JsonElement? Messages, bool WillRetry) : RpcEvent
{
    public override string Type => "agent_end";
}

public sealed record AgentSettledEvent : RpcEvent
{
    public override string Type => "agent_settled";
}

public sealed record TurnStartEvent : RpcEvent
{
    public override string Type => "turn_start";
}

public sealed record TurnEndEvent(JsonElement? Message, JsonElement? ToolResults) : RpcEvent
{
    public override string Type => "turn_end";
}

public sealed record MessageStartEvent(JsonElement? Message) : RpcEvent
{
    public override string Type => "message_start";
}

public sealed record MessageEndEvent(JsonElement? Message) : RpcEvent
{
    public override string Type => "message_end";
}

public sealed record MessageUpdateEvent(
    JsonElement? Message,
    JsonElement? AssistantMessageEvent,
    string? DeltaType,
    string? Delta) : RpcEvent
{
    public override string Type => "message_update";
}

public sealed record BashExecutionUpdateEvent(string? Id, string? Delta) : RpcEvent
{
    public override string Type => "bash_execution_update";
}

public sealed record ToolExecutionStartEvent(string? ToolCallId, string? ToolName, JsonElement? Args) : RpcEvent
{
    public override string Type => "tool_execution_start";
}

public sealed record ToolExecutionUpdateEvent(
    string? ToolCallId, string? ToolName, JsonElement? Args, JsonElement? PartialResult) : RpcEvent
{
    public override string Type => "tool_execution_update";
}

public sealed record ToolExecutionEndEvent(
    string? ToolCallId, string? ToolName, JsonElement? Result, bool IsError) : RpcEvent
{
    public override string Type => "tool_execution_end";
}

public sealed record QueueUpdateEvent(JsonElement? Steering, JsonElement? FollowUp) : RpcEvent
{
    public override string Type => "queue_update";
}

public sealed record ExtensionErrorEvent(string? ExtensionPath, string? Event, string? Error) : RpcEvent
{
    public override string Type => "extension_error";
}

/// <summary>HITL request surfaced by an extension (select/confirm/input/editor/notify).</summary>
public sealed record ExtensionUiRequestEvent(
    string? Id, string? Method, string? Title, string? Message, string? Placeholder,
    string? Prefill, JsonElement? Options) : RpcEvent
{
    public override string Type => "extension_ui_request";
}

/// <summary>An event type we don't model yet; still relayed verbatim over WS.</summary>
public sealed record UnknownEvent(string? EventType) : RpcEvent
{
    public override string Type => EventType ?? "unknown";
}

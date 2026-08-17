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

/// <summary>
/// Queue a steering message while the agent is running. Delivered after the current
/// assistant turn finishes executing its tool calls, before the next LLM call.
/// </summary>
public sealed record SteerCommand(string Message) : RpcCommand
{
    public override string Type => "steer";
    protected override void WriteExtra(Utf8JsonWriter w) => w.WriteString("message", Message);
}

/// <summary>
/// Queue a follow-up message to be processed after the agent finishes. Delivered only
/// when no more tool calls or steering messages remain.
/// </summary>
public sealed record FollowUpCommand(string Message) : RpcCommand
{
    public override string Type => "follow_up";
    protected override void WriteExtra(Utf8JsonWriter w) => w.WriteString("message", Message);
}

public sealed record GetStateCommand : RpcCommand
{
    public override string Type => "get_state";
}

/// <summary>Load a stored session file, so a fresh child can resume a preserved history.</summary>
public sealed record SwitchSessionCommand(string SessionPath) : RpcCommand
{
    public override string Type => "switch_session";
    protected override void WriteExtra(Utf8JsonWriter w) => w.WriteString("sessionPath", SessionPath);
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

/// <summary>
/// A <c>response</c> event: the id-correlated reply to a command. One shared type
/// used both by the event parser and by <see cref="PiRpcClient.SendAsync"/> to
/// resolve pending commands (no identity conversion between them).
/// </summary>
public sealed record RpcResponse(string? Id, string Command, bool Success, string? Error, JsonElement? Data) : RpcEvent
{
    public override string Type => "response";

    /// <summary>Data rendered as a JSON string, or null.</summary>
    public string? DataJson => Data is { } d ? d.GetRawText() : null;
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

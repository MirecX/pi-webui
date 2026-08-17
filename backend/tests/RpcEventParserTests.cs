using System.Text.Json;
using PiWebui.Rpc;
using Xunit;

namespace PiWebui.Tests;

public class RpcEventParserTests
{
    [Fact]
    public void Parses_agent_start_end_and_settled()
    {
        var start = Assert.IsType<AgentStartEvent>(RpcEventParser.Parse(@"{""type"":""agent_start""}"));
        Assert.Equal("agent_start", start.Type);

        var end = Assert.IsType<AgentEndEvent>(RpcEventParser.Parse(
            @"{""type"":""agent_end"",""messages"":[],""willRetry"":false}"));
        Assert.False(end.WillRetry);

        var settled = Assert.IsType<AgentSettledEvent>(RpcEventParser.Parse(@"{""type"":""agent_settled""}"));
        Assert.Equal("agent_settled", settled.Type);
    }

    [Fact]
    public void Parses_turn_start_and_end()
    {
        var ts = Assert.IsType<TurnStartEvent>(RpcEventParser.Parse(@"{""type"":""turn_start""}"));
        Assert.NotNull(ts);

        var te = Assert.IsType<TurnEndEvent>(RpcEventParser.Parse(
            @"{""type"":""turn_end"",""message"":{""role"":""assistant""},""toolResults"":[]}"));
        Assert.NotNull(te.Message);
        Assert.Equal("assistant", te.Message.Value.GetProperty("role").GetString());
    }

    [Fact]
    public void Parses_message_start_and_end()
    {
        var ms = Assert.IsType<MessageStartEvent>(RpcEventParser.Parse(
            @"{""type"":""message_start"",""message"":{""role"":""user"",""content"":""hi""}}"));
        Assert.Equal("user", ms.Message!.Value.GetProperty("role").GetString());

        var me = Assert.IsType<MessageEndEvent>(RpcEventParser.Parse(
            @"{""type"":""message_end"",""message"":{""role"":""assistant""}}"));
        Assert.Equal("assistant", me.Message!.Value.GetProperty("role").GetString());
    }

    [Fact]
    public void Parses_message_update_text_delta()
    {
        var ev = Assert.IsType<MessageUpdateEvent>(RpcEventParser.Parse(
            @"{
                ""type"":""message_update"",
                ""message"":{""role"":""assistant""},
                ""assistantMessageEvent"":{""type"":""text_delta"",""contentIndex"":0,""delta"":""Hello world""}
            }"));
        Assert.Equal("text_delta", ev.DeltaType);
        Assert.Equal("Hello world", ev.Delta);
        Assert.NotNull(ev.AssistantMessageEvent);
        Assert.NotNull(ev.Message);
    }

    [Fact]
    public void Parses_message_update_thinking_delta()
    {
        var ev = Assert.IsType<MessageUpdateEvent>(RpcEventParser.Parse(
            @"{
                ""type"":""message_update"",
                ""message"":{},
                ""assistantMessageEvent"":{""type"":""thinking_delta"",""delta"":""reasoning..."", ""partial"":{}}
            }"));
        Assert.Equal("thinking_delta", ev.DeltaType);
        Assert.Equal("reasoning...", ev.Delta);
    }

    [Fact]
    public void Parses_bash_execution_update_with_id()
    {
        var ev = Assert.IsType<BashExecutionUpdateEvent>(RpcEventParser.Parse(
            @"{""type"":""bash_execution_update"",""id"":""req-7"",""delta"":""total 48\n""}"));
        Assert.Equal("req-7", ev.Id);
        Assert.Equal("total 48\n", ev.Delta); // JSON \\n decodes to a real newline
    }

    [Fact]
    public void Parses_tool_execution_events()
    {
        var s = Assert.IsType<ToolExecutionStartEvent>(RpcEventParser.Parse(
            @"{""type"":""tool_execution_start"",""toolCallId"":""call_1"",""toolName"":""bash"",""args"":{""command"":""ls""}}"));
        Assert.Equal("call_1", s.ToolCallId);
        Assert.Equal("bash", s.ToolName);
        Assert.Equal("ls", s.Args!.Value.GetProperty("command").GetString());

        var u = Assert.IsType<ToolExecutionUpdateEvent>(RpcEventParser.Parse(
            @"{""type"":""tool_execution_update"",""toolCallId"":""call_1"",""toolName"":""bash"",""partialResult"":{""content"":[]}}"));
        Assert.Equal("call_1", u.ToolCallId);

        var e = Assert.IsType<ToolExecutionEndEvent>(RpcEventParser.Parse(
            @"{""type"":""tool_execution_end"",""toolCallId"":""call_1"",""result"":{""content"":[]},""isError"":false}"));
        Assert.False(e.IsError);
        Assert.NotNull(e.Result);
    }

    [Fact]
    public void Parses_queue_update()
    {
        var ev = Assert.IsType<QueueUpdateEvent>(RpcEventParser.Parse(
            @"{""type"":""queue_update"",""steering"":[""a""],""followUp"":[]}"));
        Assert.Single(ev.Steering!.Value.EnumerateArray());
    }

    [Fact]
    public void Parses_extension_ui_request_select()
    {
        var ev = Assert.IsType<ExtensionUiRequestEvent>(RpcEventParser.Parse(
            @"{""type"":""extension_ui_request"",""id"":""u1"",""method"":""select"",""title"":""Pick?"",""options"":[""A"",""B""]}"));
        Assert.Equal("select", ev.Method);
        Assert.Equal(2, ev.Options!.Value.GetArrayLength());
    }

    [Fact]
    public void Parses_extension_error()
    {
        var ev = Assert.IsType<ExtensionErrorEvent>(RpcEventParser.Parse(
            @"{""type"":""extension_error"",""extensionPath"":""/x.ts"",""event"":""tool_call"",""error"":""boom""}"));
        Assert.Equal("boom", ev.Error);
    }

    [Fact]
    public void Parses_response_and_preserves_raw()
    {
        var line = @"{""id"":""r1"",""type"":""response"",""command"":""prompt"",""success"":true}";
        var ev = Assert.IsType<RpcResponse>(RpcEventParser.Parse(line));
        Assert.Equal("r1", ev.Id);
        Assert.Equal("prompt", ev.Command);
        Assert.True(ev.Success);
        Assert.Equal(line, ev.Raw);
    }

    [Fact]
    public void Parses_response_with_data()
    {
        var ev = Assert.IsType<RpcResponse>(RpcEventParser.Parse(
            @"{""type"":""response"",""command"":""get_state"",""success"":true,""data"":{""isStreaming"":false}}"));
        Assert.False(ev.Data!.Value.GetProperty("isStreaming").GetBoolean());
    }

    [Fact]
    public void Parses_unknown_event_without_throwing()
    {
        var ev = Assert.IsType<UnknownEvent>(RpcEventParser.Parse(@"{""type"":""some_future_event"",""x"":1}"));
        Assert.Equal("some_future_event", ev.EventType);
    }
}

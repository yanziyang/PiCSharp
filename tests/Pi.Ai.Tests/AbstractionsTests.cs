using System.Text.Json;
using System.Text.Json.Nodes;

using Pi.Ai;

using Xunit;

namespace Pi.Ai.Tests;

public sealed class AbstractionsTests
{
    [Fact]
    public async Task Assistant_event_stream_preserves_order_and_exposes_terminal_result()
    {
        var stream = new AssistantMessageEventStream();
        var message = Assistant("done", StopReasons.Stop);
        stream.Push(new StreamStartEvent(message));
        stream.Push(new TextStartEvent(0, message));
        stream.Push(new StreamDoneEvent(StopReasons.Stop, message));
        stream.End(message);

        var events = new List<AssistantMessageEvent>();
        await foreach (var @event in stream)
        {
            events.Add(@event);
        }

        Assert.Equal(["start", "text_start", "done"], events.Select(static @event => @event.Type));
        Assert.Same(message, await stream.Result);
    }

    [Fact]
    public async Task Event_stream_ignores_events_after_terminal_event()
    {
        var stream = new EventStream<int, int>(static value => value == 2, static value => value);
        stream.Push(1);
        stream.Push(2);
        stream.Push(3);
        stream.End(2);

        var values = new List<int>();
        await foreach (var value in stream)
        {
            values.Add(value);
        }

        Assert.Equal([1, 2], values);
        Assert.Equal(2, await stream.Result);
    }

    [Fact]
    public void Content_and_message_polymorphism_preserve_upstream_discriminators()
    {
        var toolCall = new ToolCall(
            "tool-1",
            "echo",
            new JsonObject { ["text"] = "hi" });
        var contentJson = JsonSerializer.Serialize<ContentBlock>(toolCall);
        Assert.Contains("\"type\":\"toolCall\"", contentJson, StringComparison.Ordinal);
        Assert.Contains("\"arguments\":{\"text\":\"hi\"}", contentJson, StringComparison.Ordinal);
        var roundTrippedContent = JsonSerializer.Deserialize<ContentBlock>(contentJson);
        var roundTrippedToolCall = Assert.IsType<ToolCall>(roundTrippedContent);
        Assert.Equal(toolCall.Id, roundTrippedToolCall.Id);
        Assert.Equal(toolCall.Name, roundTrippedToolCall.Name);
        Assert.Equal(toolCall.Arguments.ToJsonString(), roundTrippedToolCall.Arguments.ToJsonString());

        var message = Assistant("answer", StopReasons.Stop);
        var messageJson = JsonSerializer.Serialize<Message>(message);
        Assert.Contains("\"role\":\"assistant\"", messageJson, StringComparison.Ordinal);
        var roundTrippedMessage = JsonSerializer.Deserialize<Message>(messageJson);
        Assert.Equal(message.Api, Assert.IsType<AssistantMessage>(roundTrippedMessage).Api);
    }

    [Fact]
    public void Message_utilities_extract_text_and_diagnostics_append_in_place()
    {
        var blocks = new ContentBlock[]
        {
            new TextContent("first"),
            new ThinkingContent("hidden"),
            new TextContent("second"),
        };
        Assert.Equal("first|second", MessageUtilities.ContentText(blocks, "|"));
        Assert.Equal("plain", MessageUtilities.ContentText("plain"));

        var message = Assistant("answer", StopReasons.Stop);
        var diagnostic = DiagnosticUtilities.CreateAssistantMessageDiagnostic(
            "provider",
            new InvalidOperationException("failed"));
        DiagnosticUtilities.AppendAssistantMessageDiagnostic(message, diagnostic);
        Assert.Same(diagnostic, Assert.Single(message.Diagnostics!));
        Assert.Equal("failed", diagnostic.Error?.Message);
    }

    [Fact]
    public void Optional_values_are_omitted_when_null()
    {
        var message = Assistant("answer", StopReasons.Stop);
        var json = JsonSerializer.Serialize(message);
        Assert.DoesNotContain("responseModel", json, StringComparison.Ordinal);
        Assert.DoesNotContain("errorMessage", json, StringComparison.Ordinal);
        Assert.Contains("\"stopReason\":\"stop\"", json, StringComparison.Ordinal);
    }

    private static AssistantMessage Assistant(string text, string stopReason) => new()
    {
        Content = [new TextContent(text)],
        Api = ApiNames.OpenAiResponses,
        Provider = ProviderNames.Faux,
        Model = "faux-1",
        StopReason = stopReason,
        Timestamp = 1,
        Usage = new Usage
        {
            Input = 1,
            Output = 1,
            TotalTokens = 2,
        },
    };
}

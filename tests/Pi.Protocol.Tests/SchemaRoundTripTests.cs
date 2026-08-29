using Pi.Protocol;

using Xunit;

namespace Pi.Protocol.Tests;

public sealed class SchemaRoundTripTests
{
    [Fact]
    public void RoundTripsEveryClientSchemaBranch()
    {
        ModelRef model = new("test", "model");
        ClientMessage[] messages =
        [
            new ClientHello(1),
            new RequestEnvelope("list", new ListCommand()),
            new RequestEnvelope("create", new CreateCommand("/workspace", "New", model, ThinkingLevel.Low)),
            new RequestEnvelope("attach", new AttachCommand("session-1")),
            new RequestEnvelope("detach", new DetachCommand("session-1")),
            new RequestEnvelope("prompt", new PromptCommand("session-1", "hello")),
            new RequestEnvelope("steer", new SteerCommand("session-1", "continue")),
            new RequestEnvelope("abort", new AbortCommand("session-1")),
            new RequestEnvelope("set-model", new SetModelCommand("session-1", model)),
            new RequestEnvelope("set-thinking", new SetThinkingCommand("session-1", ThinkingLevel.High)),
        ];

        foreach (ClientMessage message in messages)
        {
            byte[] frame = ProtocolCodec.EncodeClientMessage(message);
            ClientMessage decoded = Assert.Single(new ClientMessageDecoder().Push(frame));
            Assert.Equal(frame, ProtocolCodec.EncodeClientMessage(decoded));
        }
    }

    [Fact]
    public void RoundTripsEveryServerSchemaBranch()
    {
        ModelRef model = new("test", "model");
        ModelCost modelCost = new(1, 2, 3, 4);
        ModelMetadata modelMetadata = new(
            "test", "model", "Test model", "openai", true,
            new[] { ModelInputKind.Text, ModelInputKind.Image },
            128_000, 4_096, modelCost,
            new[]
            {
                ThinkingLevel.Off, ThinkingLevel.Minimal, ThinkingLevel.Low, ThinkingLevel.Medium,
                ThinkingLevel.High, ThinkingLevel.Xhigh, ThinkingLevel.Max,
            },
            true);
        Usage usage = new(10, 20, 30, 40, 5, 105, new UsageCost(1, 2, 3, 4, 10));
        JsonValue.JsonObject input = new(new Dictionary<string, JsonValue>
        {
            ["null"] = new JsonValue.JsonNull(),
            ["boolean"] = new JsonValue.JsonBoolean(false),
            ["number"] = new JsonValue.JsonNumber(1.25),
            ["array"] = new JsonValue.JsonArray(new JsonValue[] { new JsonValue.JsonString("nested") }),
        });
        JsonValue.JsonObject details = new(new Dictionary<string, JsonValue>
        {
            ["lines"] = new JsonValue.JsonArray(new JsonValue[]
            {
                new JsonValue.JsonNumber(1), new JsonValue.JsonNumber(2), new JsonValue.JsonNumber(3),
            }),
            ["cached"] = new JsonValue.JsonBoolean(false),
        });
        IReadOnlyList<Content> userContent = new Content[]
        {
            new TextContent("user text"),
            new ImageContent("base64", "image/png"),
        };
        IReadOnlyList<Content> assistantContent = new Content[]
        {
            new TextContent("assistant text"),
            new ThinkingContent("private thought", false),
            new ToolCallContent("call-1", "read", input),
        };
        IReadOnlyList<Content> toolContent = new Content[]
        {
            new TextContent("tool text"),
            new ImageContent("result", "image/jpeg"),
        };
        UserTranscriptItem user = new("user-1", userContent, 1);
        StreamingAssistantTranscriptItem streaming = new("assistant-stream", assistantContent, model, "response-model", usage, 2);
        CompleteAssistantTranscriptItem complete = new("assistant-complete", assistantContent, model, null, usage, 3, TranscriptStopReason.ToolUse);
        ErrorAssistantTranscriptItem assistantError = new("assistant-error", assistantContent, model, null, null, 4, "failed");
        AbortedAssistantTranscriptItem aborted = new("assistant-aborted", assistantContent, model, null, null, 5, string.Empty);
        RunningToolTranscriptItem runningTool = new("tool-running", "call-1", "read", input, toolContent, null, null, 6);
        CompleteToolTranscriptItem completeTool = new("tool-complete", "call-1", "read", input, toolContent, details, usage, 7);
        ErrorToolTranscriptItem errorTool = new("tool-error", "call-1", "read", input, toolContent, new JsonValue.JsonNull(), null, 8);
        TranscriptItem[] transcript = [user, streaming, complete, assistantError, aborted, runningTool, completeTool, errorTool];
        SessionSnapshot session = new(
            "session-1", string.Empty, "/workspace", 1, 2, SessionPhase.Turn, model, ThinkingLevel.Medium,
            true, false, 9, transcript, new[] { user }, 1);
        SessionMetadata metadata = new("session-1", 1, 2, "parent-1", string.Empty, "/workspace");
        ServerSnapshot snapshot = new("server-1", 1, 10, new[] { metadata }, new[] { modelMetadata });
        ProtocolError error = new(ProtocolErrorCode.InternalError, "failure", details);

        ServerMessage[] messages =
        [
            new ServerHello(1, "connection-1", snapshot),
            new ServerHelloError(error),
            new ResponseEnvelope("list", true, new ListResult(new[] { metadata })),
            new ResponseEnvelope("create", true, new CreateResult(session)),
            new ResponseEnvelope("attach", true, new AttachResult(session)),
            new ResponseEnvelope("detach", true, Error: null, Result: new DetachResult("session-1")),
            new ResponseEnvelope("prompt", true, new PromptResult(session)),
            new ResponseEnvelope("steer", true, new SteerResult(session)),
            new ResponseEnvelope("abort", true, new AbortResult(session)),
            new ResponseEnvelope("set-model", true, new SetModelResult(session)),
            new ResponseEnvelope("set-thinking", true, new SetThinkingResult(session)),
            new ResponseEnvelope("error", false, Error: error),
            new EventEnvelope(new ServerSnapshotEvent(snapshot)),
            new EventEnvelope(new SessionSnapshotEvent(session)),
            new EventEnvelope(new SessionProgressEvent("session-1", new ItemStartedProgress(user))),
            new EventEnvelope(new SessionProgressEvent("session-1", new AssistantDeltaProgress("assistant-stream", 0, ContentKind.Thinking, "delta"))),
            new EventEnvelope(new SessionProgressEvent("session-1", new ItemUpdatedProgress(streaming))),
            new EventEnvelope(new SessionProgressEvent("session-1", new ItemFinishedProgress(complete))),
            new EventEnvelope(new SessionRemovedEvent("session-1")),
        ];

        foreach (ServerMessage message in messages)
        {
            byte[] frame = ProtocolCodec.EncodeServerMessage(message);
            ServerMessage decoded = Assert.Single(new ServerMessageDecoder().Push(frame));
            Assert.Equal(frame, ProtocolCodec.EncodeServerMessage(decoded));
        }
    }
}

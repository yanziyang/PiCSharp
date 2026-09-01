using System.Numerics;
using System.Text.Json.Nodes;

using Pi.Protocol;
using Pi.Server;

using Xunit;

using NodeJsonValue = System.Text.Json.Nodes.JsonValue;
using ProtocolJsonValue = Pi.Protocol.JsonValue;

namespace Pi.Server.Tests;

public sealed class R5ServerProtocolTests
{
    [Fact(DisplayName = "maps model metadata and produces protocol-valid output")]
    public void Maps_model_metadata_and_produces_protocol_valid_output()
    {
        var result = ServerProtocol.ToProtocolModelMetadata(TestModel(), authenticated: true);

        Assert.Equal("test-provider", result.Provider);
        Assert.Equal("model-1", result.Id);
        Assert.Equal("test-api", result.Api);
        Assert.Equal([ModelInputKind.Text, ModelInputKind.Image], result.Input);
        Assert.True(result.Authenticated);
        Assert.Contains(ThinkingLevel.Off, result.SupportedThinkingLevels);
        AssertValidServerPayload(new UserTranscriptItem(
            "user-1",
            [new TextContent("hello")],
            1));
    }

    [Fact(DisplayName = "maps user and tool messages without leaking non-JSON details")]
    public void Maps_user_and_tool_messages_without_leaking_non_JSON_details()
    {
        var user = Pi.Ai.UserMessage.Text("hello", 1);
        var details = new JsonObject { ["self"] = "[Circular]" };
        var tool = new Pi.Ai.ToolResultMessage
        {
            ToolCallId = "call-1",
            ToolName = "read",
            Content = [new Pi.Ai.TextContent("result")],
            Details = details,
            IsError = false,
            Timestamp = 2,
        };
        var call = new Pi.Ai.ToolCall(
            "call-1",
            "read",
            new JsonObject { ["path"] = "README.md" });

        var userResult = ServerProtocol.ToProtocolUserMessage(user, "user-1");
        Assert.Equal("user-1", userResult.Id);
        Assert.Equal([new TextContent("hello")], userResult.Content);
        AssertValidServerPayload(userResult);

        var toolResult = ServerProtocol.ToProtocolToolResultMessage(tool, "tool-1", call);
        Assert.Equal("tool-1", toolResult.Id);
        Assert.Equal("read", toolResult.ToolName);
        Assert.Equal("complete", toolResult.Status);
        var sanitizedDetails = Assert.IsType<ProtocolJsonValue.JsonObject>(toolResult.Details);
        Assert.Equal(
            new ProtocolJsonValue.JsonString("[Circular]"),
            sanitizedDetails.Properties["self"]);
        AssertValidServerPayload(toolResult);
    }

    [Fact(DisplayName = "rejects tool results associated with a different call")]
    public void Rejects_tool_results_associated_with_a_different_call()
    {
        var call = new Pi.Ai.ToolCall(
            "call-1",
            "read",
            new JsonObject { ["path"] = "README.md" });
        var result = new Pi.Ai.ToolResultMessage
        {
            ToolCallId = "call-2",
            ToolName = "read",
            Content = [new Pi.Ai.TextContent("result")],
            IsError = false,
            Timestamp = 2,
        };

        Assert.Throws<ProtocolValidationError>(() =>
            ServerProtocol.ToProtocolToolResultMessage(result, "tool-1", call));
        result = result with { ToolCallId = "call-1", ToolName = "write" };
        Assert.Throws<ProtocolValidationError>(() =>
            ServerProtocol.ToProtocolToolResultMessage(result, "tool-1", call));
    }

    [Fact(DisplayName = "derives streaming status from a pending stop reason")]
    public void Derives_streaming_status_from_a_pending_stop_reason()
    {
        var message = new Pi.Ai.AssistantMessage
        {
            Api = "test-api",
            Provider = "test-provider",
            Model = "model-1",
            Usage = EmptyUsage(),
            StopReason = Pi.Ai.StopReasons.Pending,
            Timestamp = 123,
            Content = [new Pi.Ai.TextContent("partial")],
        };

        var result = ServerProtocol.ToProtocolAssistantMessage(message, "message-pending");

        Assert.IsType<StreamingAssistantTranscriptItem>(result);
        Assert.Equal("streaming", Assert.IsType<StreamingAssistantTranscriptItem>(result).Status);
        AssertValidServerPayload(result);
    }

    [Fact(DisplayName = "preserves optional non-empty assistant error messages")]
    public void Preserves_optional_non_empty_assistant_error_messages()
    {
        var message = new Pi.Ai.AssistantMessage
        {
            Api = "test-api",
            Provider = "test-provider",
            Model = "model-1",
            Usage = EmptyUsage(),
            StopReason = Pi.Ai.StopReasons.Error,
            Timestamp = 123,
            Content = [],
        };

        var resultWithoutMessage = ServerProtocol.ToProtocolAssistantMessage(message, "message-error");
        var errorWithoutMessage = Assert.IsType<ErrorAssistantTranscriptItem>(resultWithoutMessage);
        Assert.Null(errorWithoutMessage.ErrorMessage);
        AssertValidServerPayload(resultWithoutMessage);

        Assert.Throws<ProtocolValidationError>(() => ServerProtocol.ToProtocolAssistantMessage(
            message with { ErrorMessage = string.Empty },
            "message-error"));
        var resultWithMessage = ServerProtocol.ToProtocolAssistantMessage(
            message with { ErrorMessage = "failed" },
            "message-error");
        Assert.Equal("failed", Assert.IsType<ErrorAssistantTranscriptItem>(resultWithMessage).ErrorMessage);
        AssertValidServerPayload(resultWithMessage);
    }

    [Fact(DisplayName = "rejects invalid source identifiers and timestamps")]
    public void Rejects_invalid_source_identifiers_and_timestamps()
    {
        var message = new Pi.Ai.AssistantMessage
        {
            Api = "test-api",
            Provider = "test-provider",
            Model = "model-1",
            Usage = EmptyUsage(),
            StopReason = Pi.Ai.StopReasons.ToolUse,
            Timestamp = 1,
            Content = [new Pi.Ai.ToolCall(string.Empty, "read", new JsonObject())],
        };

        Assert.Throws<ProtocolValidationError>(() =>
            ServerProtocol.ToProtocolAssistantMessage(message, "assistant-1"));
        Assert.Throws<ProtocolValidationError>(() =>
            ServerProtocol.ToProtocolUserMessage(Pi.Ai.UserMessage.Text("hello", -1), "user-1"));
    }

    [Fact(DisplayName = "rejects lossy tool input conversions")]
    public void Rejects_lossy_tool_input_conversions()
    {
        Assert.Throws<ProtocolValidationError>(() =>
            ServerProtocol.ToProtocolJsonValue(NodeJsonValue.Create(double.PositiveInfinity)));
        Assert.Throws<ProtocolValidationError>(() =>
            ServerProtocol.ToProtocolJsonValue(NodeJsonValue.Create(BigInteger.One)));

        // JavaScript undefined and object cycles have no direct JsonNode value. Null is
        // intentionally retained as protocol JSON null, so those source-only cases are
        // documented in R5Findings.md rather than silently treating null as undefined.
        Assert.Equal(
            new ProtocolJsonValue.JsonNull(),
            ServerProtocol.ToProtocolJsonValue(null));
    }

    [Fact(DisplayName = "rejects sparse execution data and normalizes sparse diagnostic arrays")]
    public void Rejects_sparse_execution_data_and_normalizes_sparse_diagnostic_arrays()
    {
        var sparse = new JsonArray();
        sparse.Add(null);
        sparse.Add(NodeJsonValue.Create("value"));

        var normalized = Assert.IsType<ProtocolJsonValue.JsonArray>(ServerProtocol.SanitizeProtocolDetails(sparse));
        Assert.Equal(
            [new ProtocolJsonValue.JsonNull(), new ProtocolJsonValue.JsonString("value")],
            normalized.Values);
    }

    private static Pi.Ai.Model TestModel() => new()
    {
        Id = "model-1",
        Name = "Model One",
        Api = "test-api",
        Provider = "test-provider",
        BaseUrl = "https://example.test",
        Reasoning = true,
        Input = ["text", "image"],
        Cost = new Pi.Ai.ModelCost
        {
            Input = 1,
            Output = 2,
            CacheRead = 0.1,
            CacheWrite = 0.2,
        },
        ContextWindow = 100_000,
        MaxTokens = 10_000,
    };

    private static Pi.Ai.Usage EmptyUsage() => new()
    {
        Cost = new Pi.Ai.UsageCost(),
    };

    private static void AssertValidServerPayload(TranscriptItem item)
    {
        var metadata = ServerProtocol.ToProtocolModelMetadata(TestModel(), authenticated: true);
        var hello = new ServerHello(
            ProtocolConstants.ProtocolVersion,
            "connection-1",
            new ServerSnapshot(
                "server-1",
                ProtocolConstants.ProtocolVersion,
                0,
                [new SessionMetadata("session-1", 1, UpdatedAt: 1, SessionName: "Session one", Cwd: "/workspace")],
                [metadata]));
        Assert.Null(Record.Exception(() => ProtocolCodec.EncodeServerMessage(hello)));

        var snapshot = new SessionSnapshot(
            "session-1",
            null,
            "/workspace",
            1,
            1,
            SessionPhase.Idle,
            new ModelRef("test-provider", "model-1"),
            ThinkingLevel.Off,
            Attached: true,
            Locked: true,
            Revision: 1,
            Transcript: [item],
            QueuedSteer: [],
            QueuedSteerCount: 0);
        Assert.Null(Record.Exception(() => ProtocolCodec.EncodeServerMessage(
            new EventEnvelope(new SessionSnapshotEvent(snapshot)))));
    }
}

using Pi.Protocol;

using Xunit;

namespace Pi.Protocol.Tests;

public sealed class ProtocolTests
{
    private static readonly Dictionary<string, object?> _emptyServerSnapshot = Map(
        ("serverId", "server-1"),
        ("protocolVersion", 1L),
        ("revision", 0L),
        ("sessions", Array.Empty<object?>()),
        ("models", Array.Empty<object?>()));

    [Fact]
    public void UsesProtocolVersionOne()
    {
        Assert.Equal(1, Protocol.ProtocolVersion);
        Assert.True(Protocol.IsSupportedProtocolVersion(1));
        Assert.False(Protocol.IsSupportedProtocolVersion(2));
        Assert.False(Protocol.IsSupportedProtocolVersion(2.5));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(2L)]
    public void AcceptsIntegerClientHelloVersionForNegotiation(long version)
    {
        ClientHello hello = Assert.IsType<ClientHello>(ProtocolCodec.ParseClientMessage(Map(
            ("type", "hello"),
            ("version", version))));

        Assert.Equal(version, hello.Version);
    }

    [Fact]
    public void RejectsMalformedClientHello()
    {
        object?[] messages =
        [
            Map(("type", "hello"), ("version", "1")),
            Map(("type", "hello"), ("version", 1.5d)),
            Map(("type", "hello"), ("version", 1L), ("token", "secret")),
            Map(("type", "hello"), ("version", 1L), ("extra", true)),
        ];

        foreach (object? message in messages)
        {
            Assert.Throws<ProtocolValidationError>(() => ProtocolCodec.ParseClientMessage(message));
        }
    }

    [Fact]
    public void DoesNotParseJsonStringsAsWireMessages()
    {
        string clientHello = "{\"type\":\"hello\",\"version\":1}";
        string serverHello = "{\"type\":\"hello\",\"version\":1}";

        Assert.Throws<ProtocolValidationError>(() => ProtocolCodec.ParseClientMessage(clientHello));
        Assert.Throws<ProtocolValidationError>(() => ProtocolCodec.ParseServerMessage(serverHello));
    }

    [Fact]
    public void RejectsImageInputOnTextOnlyPromptCommand()
    {
        Dictionary<string, object?> message = Map(
            ("type", "request"),
            ("id", "request-1"),
            ("request", Map(
                ("command", "prompt"),
                ("sessionId", "session-1"),
                ("text", "inspect"),
                ("images", new object?[]
                {
                    Map(("type", "image"), ("data", "abc"), ("mimeType", "image/png")),
                }))));

        Assert.Throws<ProtocolValidationError>(() => ProtocolCodec.ParseClientMessage(message));
    }

    [Fact]
    public void ParsesServerHandshakeSnapshot()
    {
        ServerHello hello = Assert.IsType<ServerHello>(ProtocolCodec.ParseServerMessage(Map(
            ("type", "hello"),
            ("version", 1L),
            ("connectionId", "connection-1"),
            ("snapshot", _emptyServerSnapshot))));

        Assert.Equal(1, hello.Version);
        Assert.Equal("connection-1", hello.ConnectionId);
        Assert.Equal("server-1", hello.Snapshot.ServerId);
    }

    [Fact]
    public void RepresentsListedSessionsAsDurableMetadata()
    {
        Dictionary<string, object?> message = Map(
            ("type", "response"),
            ("id", "request-1"),
            ("ok", true),
            ("result", Map(
                ("command", "list"),
                ("sessions", new object?[]
                {
                    Map(
                        ("id", "session-1"),
                        ("createdAt", 1L),
                        ("updatedAt", 2L),
                        ("parentSessionId", "parent-1"),
                        ("sessionName", "Named session"),
                        ("cwd", "/workspace")),
                }))));

        ResponseEnvelope response = Assert.IsType<ResponseEnvelope>(ProtocolCodec.ParseServerMessage(message));
        ListResult result = Assert.IsType<ListResult>(response.Result);
        SessionMetadata session = Assert.Single(result.Sessions);
        Assert.Equal("session-1", session.Id);
        Assert.Equal(1, session.CreatedAt);
        Assert.Equal(2, session.UpdatedAt);
        Assert.Equal("parent-1", session.ParentSessionId);
        Assert.Equal("Named session", session.SessionName);
        Assert.Equal("/workspace", session.Cwd);

        Dictionary<string, object?> missingRequired = Map(
            ("type", "response"),
            ("id", "request-1"),
            ("ok", true),
            ("result", Map(
                ("command", "list"),
                ("sessions", new object?[]
                {
                    Map(("id", "session-1"), ("createdAt", 1L), ("phase", "idle")),
                }))));
        Assert.Throws<ProtocolValidationError>(() => ProtocolCodec.ParseServerMessage(missingRequired));
    }

    [Theory]
    [InlineData("not_implemented")]
    [InlineData("internal_error")]
    public void AcceptsSupportedErrorCodes(string code)
    {
        ResponseEnvelope response = Assert.IsType<ResponseEnvelope>(ProtocolCodec.ParseServerMessage(Map(
            ("type", "response"),
            ("id", "request-1"),
            ("ok", false),
            ("error", Map(("code", code), ("message", "safe"))))));

        Assert.False(response.Ok);
        Assert.NotNull(response.Error);
        Assert.Equal(code, response.Error!.Code switch
        {
            ProtocolErrorCode.NotImplemented => "not_implemented",
            ProtocolErrorCode.InternalError => "internal_error",
            _ => string.Empty,
        });
    }

    [Fact]
    public void RejectsInvalidServerMessages()
    {
        object?[] messages =
        [
            Map(
                ("type", "hello"),
                ("version", 2L),
                ("connectionId", "connection-1"),
                ("snapshot", _emptyServerSnapshot)),
            Map(("type", "hello_error"), ("error", Map(("code", "auth"), ("message", "Authentication failed")))),
            Map(("type", "response"), ("id", "request-1"), ("ok", true), ("result", Map(("command", "unknown")))),
            Map(("type", "event"), ("event", Map(("type", "session_removed"), ("sessionId", 42L)))),
        ];

        foreach (object? message in messages)
        {
            Assert.Throws<ProtocolValidationError>(() => ProtocolCodec.ParseServerMessage(message));
        }
    }

    [Fact]
    public void ValidatesNestedJsonToolDetails()
    {
        ServerMessage message = ProtocolCodec.ParseServerMessage(ItemMessage(
            Map(
                ("id", "tool-1"),
                ("role", "tool"),
                ("toolCallId", "call-1"),
                ("toolName", "read"),
                ("input", Map(("path", "/tmp/file"))),
                ("content", new object?[] { Map(("type", "text"), ("text", "done")) }),
                ("details", Map(("lines", new object?[] { 1L, 2L, 3L }), ("cached", false))),
                ("status", "complete"),
                ("isError", false),
                ("timestamp", 1L))));

        CompleteToolTranscriptItem item = GetFinishedTool(message);
        JsonValue.JsonObject details = Assert.IsType<JsonValue.JsonObject>(item.Details);
        Assert.Contains("lines", details.Properties.Keys);
        Assert.Contains("cached", details.Properties.Keys);
    }

    [Fact]
    public void AcceptsConsistentAssistantItems()
    {
        Dictionary<string, object?> streaming = AssistantItem("streaming");
        Dictionary<string, object?> complete = AssistantItem("complete", ("stopReason", "stop"));
        Dictionary<string, object?> error = AssistantItem("error", ("stopReason", "error"));
        Dictionary<string, object?> errorWithMessage = AssistantItem("error", ("stopReason", "error"), ("errorMessage", "failed"));
        Dictionary<string, object?> aborted = AssistantItem("aborted", ("stopReason", "aborted"));

        Assert.IsType<StreamingAssistantTranscriptItem>(GetProgressItem(ProtocolCodec.ParseServerMessage(ItemMessage(streaming, "item_updated"))));
        Assert.IsType<CompleteAssistantTranscriptItem>(GetProgressItem(ProtocolCodec.ParseServerMessage(ItemMessage(complete))));
        Assert.IsType<ErrorAssistantTranscriptItem>(GetProgressItem(ProtocolCodec.ParseServerMessage(ItemMessage(error))));
        Assert.IsType<ErrorAssistantTranscriptItem>(GetProgressItem(ProtocolCodec.ParseServerMessage(ItemMessage(errorWithMessage))));
        Assert.IsType<AbortedAssistantTranscriptItem>(GetProgressItem(ProtocolCodec.ParseServerMessage(ItemMessage(aborted))));
    }

    [Fact]
    public void RejectsInconsistentAssistantItems()
    {
        object?[] states =
        [
            AssistantItem("streaming", ("stopReason", "stop")),
            AssistantItem("complete"),
            AssistantItem("complete", ("stopReason", "error")),
            AssistantItem("error", ("stopReason", "error"), ("errorMessage", string.Empty)),
            AssistantItem("aborted", ("stopReason", "stop")),
        ];

        foreach (object? state in states)
        {
            Assert.Throws<ProtocolValidationError>(() => ProtocolCodec.ParseServerMessage(ItemMessage(state!)));
        }
    }

    [Fact]
    public void AcceptsConsistentToolItems()
    {
        Assert.IsType<RunningToolTranscriptItem>(GetProgressItem(ProtocolCodec.ParseServerMessage(ItemMessage(ToolItem("running", false), "item_updated"))));
        Assert.IsType<CompleteToolTranscriptItem>(GetProgressItem(ProtocolCodec.ParseServerMessage(ItemMessage(ToolItem("complete", false)))));
        Assert.IsType<ErrorToolTranscriptItem>(GetProgressItem(ProtocolCodec.ParseServerMessage(ItemMessage(ToolItem("error", true)))));
    }

    [Fact]
    public void RejectsNonterminalItemsReportedAsFinished()
    {
        Assert.Throws<ProtocolValidationError>(() => ProtocolCodec.ParseServerMessage(ItemMessage(AssistantItem("streaming"))));
        Assert.Throws<ProtocolValidationError>(() => ProtocolCodec.ParseServerMessage(ItemMessage(ToolItem("running", false))));
    }

    [Fact]
    public void RejectsInconsistentToolItems()
    {
        Assert.Throws<ProtocolValidationError>(() => ProtocolCodec.ParseServerMessage(ItemMessage(ToolItem("running", true))));
        Assert.Throws<ProtocolValidationError>(() => ProtocolCodec.ParseServerMessage(ItemMessage(ToolItem("complete", true))));
        Assert.Throws<ProtocolValidationError>(() => ProtocolCodec.ParseServerMessage(ItemMessage(ToolItem("error", false))));
    }

    [Fact]
    public void RejectsCyclicProtocolValues()
    {
        Dictionary<string, object?> details = [];
        details["self"] = details;
        Dictionary<string, object?> message = Map(
            ("type", "response"),
            ("id", "request-1"),
            ("ok", false),
            ("error", Map(("code", "invalid_request"), ("message", "invalid"), ("details", details))));

        Assert.Throws<ProtocolValidationError>(() => ProtocolCodec.ParseServerMessage(message));
    }

    [Fact]
    public void ValidationErrorsDoNotRetainRejectedPayloads()
    {
        ProtocolValidationError? thrown = null;
        try
        {
            ProtocolCodec.ParseClientMessage(Map(
                ("type", "hello"),
                ("version", "1"),
                ("extra", new string('x', 2_000_000))));
        }
        catch (ProtocolValidationError error)
        {
            thrown = error;
        }

        Assert.NotNull(thrown);
        Assert.DoesNotContain("x", thrown!.Message);
        Assert.True(thrown.Message.Length < 1_000);
    }

    [Fact]
    public void EncodesClientHelloWithStableWireVector()
    {
        byte[] expected = Convert.FromHexString("00000015a264747970656568656c6c6f6776657273696f6e01");
        Assert.Equal(expected, ProtocolCodec.EncodeClientMessage(new ClientHello(1)));
    }

    [Fact]
    public void EncodesCompleteClientAndServerFrames()
    {
        ClientHello clientHello = new(1);
        ServerHello serverHello = new(
            1,
            "connection-1",
            new ServerSnapshot("server-1", 1, 0, Array.Empty<SessionMetadata>(), Array.Empty<ModelMetadata>()));

        byte[] clientFrame = Assert.Single(new FrameDecoder().Push(ProtocolCodec.EncodeClientMessage(clientHello)));
        ClientHello decodedClient = Assert.IsType<ClientHello>(ProtocolCodec.ParseClientMessage(CborDecoder.DecodeCbor(clientFrame)));
        Assert.Equal(clientHello, decodedClient);

        byte[] serverFrame = Assert.Single(new FrameDecoder().Push(ProtocolCodec.EncodeServerMessage(serverHello)));
        ServerHello decodedServer = Assert.IsType<ServerHello>(ProtocolCodec.ParseServerMessage(CborDecoder.DecodeCbor(serverFrame)));
        Assert.Equal(serverHello, decodedServer);
    }

    [Fact]
    public void EnforcesOutboundFrameLimitBeforeReturningBytes()
    {
        Assert.Throws<ProtocolValidationError>(() => ProtocolCodec.EncodeClientMessage(new ClientHello(1), new FrameDecoderOptions { MaxFrameLength = 8 }));
        Assert.Throws<ProtocolValidationError>(() => ProtocolCodec.EncodeServerMessage(
            new ServerHello(1, "connection-1", new ServerSnapshot("server-1", 1, 0, [], [])),
            new FrameDecoderOptions { MaxFrameLength = 8 }));
    }

    [Fact]
    public void ValidatesRawMessagesBeforeEncoding()
    {
        Assert.Throws<ProtocolValidationError>(() => ProtocolCodec.EncodeClientMessage(Map(
            ("type", "hello"),
            ("version", 1.5d))));
    }

    [Fact]
    public void OmitsNullOptionalPropertiesOnWire()
    {
        ClientMessage message = new RequestEnvelope("request-1", new CreateCommand());
        byte[] payload = Assert.Single(new FrameDecoder().Push(ProtocolCodec.EncodeClientMessage(message)));
        IReadOnlyDictionary<string, object?> wire = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(CborDecoder.DecodeCbor(payload));
        IReadOnlyDictionary<string, object?> request = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(wire["request"]);

        Assert.Collection(
            wire.Keys,
            key => Assert.Equal("type", key),
            key => Assert.Equal("id", key),
            key => Assert.Equal("request", key));
        Assert.Collection(request.Keys, key => Assert.Equal("command", key));
        Assert.Equal("create", request["command"]);
    }

    [Fact]
    public void IncrementallyDecodesFragmentedAndCoalescedClientMessages()
    {
        ClientMessage first = new ClientHello(1);
        ClientMessage second = new RequestEnvelope("request-1", new ListCommand());
        byte[] firstWire = ProtocolCodec.EncodeClientMessage(first);
        byte[] secondWire = ProtocolCodec.EncodeClientMessage(second);
        byte[] wire = Concatenate(firstWire, secondWire);

        for (int split = 0; split <= wire.Length; split++)
        {
            ClientMessageDecoder decoder = new();
            List<ClientMessage> messages = [];
            messages.AddRange(decoder.Push(wire.AsSpan(0, split)));
            messages.AddRange(decoder.Push(wire.AsSpan(split)));
            decoder.End();

            Assert.Equal(new ClientMessage[] { first, second }, messages);
        }
    }

    [Fact]
    public void IncrementallyDecodesServerMessages()
    {
        ServerMessage expected = new ServerHelloError(new ProtocolError(ProtocolErrorCode.Version, "Unsupported protocol version"));
        ServerMessageDecoder decoder = new();

        Assert.Equal(new[] { expected }, decoder.Push(ProtocolCodec.EncodeServerMessage(expected)));
        decoder.End();
    }

    [Fact]
    public void RejectsInvalidFramedClientInputAndPoisonsDecoder()
    {
        byte[][] wires =
        [
            Framing.EncodeFrame([]),
            Framing.EncodeFrame(new byte[] { 0xff }),
            Framing.EncodeFrame(CborEncoder.EncodeCbor(Map(("type", "hello"), ("version", 1L), ("extra", true)))),
        ];

        foreach (byte[] wire in wires)
        {
            ClientMessageDecoder decoder = new();
            Assert.Throws<ProtocolValidationError>(() => decoder.Push(wire));
            Assert.Contains("failed", Assert.Throws<ProtocolValidationError>(() => decoder.Push(ProtocolCodec.EncodeClientMessage(new ClientHello(1)))).Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void RejectsCborByteStringsNestedInJsonValuedFields()
    {
        byte[] wire = Framing.EncodeFrame(CborEncoder.EncodeCbor(Map(
            ("type", "response"),
            ("id", "request-1"),
            ("ok", false),
            ("error", Map(
                ("code", "invalid_request"),
                ("message", "invalid"),
                ("details", Map(("nested", new byte[] { 1, 2, 3 }))))))));

        Assert.Throws<ProtocolValidationError>(() => new ServerMessageDecoder().Push(wire));
    }

    [Fact]
    public void RejectsTruncatedAndOversizedFramingThroughValidatedDecoder()
    {
        ServerMessageDecoder truncated = new();
        Assert.Empty(truncated.Push(new byte[] { 0, 0, 0, 2, 1 }));
        Assert.Throws<ProtocolValidationError>(truncated.End);

        ClientMessageDecoder oversized = new(new FrameDecoderOptions { MaxFrameLength = 3 });
        Assert.Throws<ProtocolValidationError>(() => oversized.Push(new byte[] { 0, 0, 0, 4 }));
    }

    private static Dictionary<string, object?> AssistantItem(string status, params (string Key, object? Value)[] additional)
    {
        Dictionary<string, object?> item = Map(
            ("id", "assistant-1"),
            ("role", "assistant"),
            ("content", new object?[] { Map(("type", "text"), ("text", "hello")) }),
            ("model", Map(("provider", "test"), ("id", "model"))),
            ("timestamp", 1L),
            ("status", status));
        foreach ((string key, object? value) in additional)
        {
            item[key] = value;
        }

        return item;
    }

    private static Dictionary<string, object?> ToolItem(string status, bool isError)
    {
        return Map(
            ("id", "tool-1"),
            ("role", "tool"),
            ("toolCallId", "call-1"),
            ("toolName", "read"),
            ("input", new Dictionary<string, object?>()),
            ("content", Array.Empty<object?>()),
            ("timestamp", 1L),
            ("status", status),
            ("isError", isError));
    }

    private static Dictionary<string, object?> ItemMessage(object item, string progressType = "item_finished")
    {
        return Map(
            ("type", "event"),
            ("event", Map(
                ("type", "session_progress"),
                ("sessionId", "session-1"),
                ("progress", Map(("type", progressType), ("item", item))))));
    }

    private static CompleteToolTranscriptItem GetFinishedTool(ServerMessage message)
    {
        return Assert.IsType<CompleteToolTranscriptItem>(GetProgressItem(message));
    }

    private static TranscriptItem GetProgressItem(ServerMessage message)
    {
        EventEnvelope envelope = Assert.IsType<EventEnvelope>(message);
        SessionProgressEvent progress = Assert.IsType<SessionProgressEvent>(envelope.Event);
        return progress.Progress switch
        {
            ItemStartedProgress started => started.Item,
            ItemUpdatedProgress updated => updated.Item,
            ItemFinishedProgress finished => finished.Item,
            _ => throw new Xunit.Sdk.XunitException("Unexpected progress type"),
        };
    }

    private static Dictionary<string, object?> Map(params (string Key, object? Value)[] values)
    {
        Dictionary<string, object?> result = new(StringComparer.Ordinal);
        foreach ((string key, object? value) in values)
        {
            result.Add(key, value);
        }

        return result;
    }

    private static byte[] Concatenate(params byte[][] chunks)
    {
        byte[] result = new byte[chunks.Sum(chunk => chunk.Length)];
        int offset = 0;
        foreach (byte[] chunk in chunks)
        {
            chunk.CopyTo(result, offset);
            offset += chunk.Length;
        }

        return result;
    }
}

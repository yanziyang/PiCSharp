using Pi.Protocol;

using Xunit;

namespace Pi.Protocol.Tests;

public sealed class ProtocolWireVectorTests
{
    [Fact]
    public void MatchesTypeScriptWireVectorsForEveryTopLevelAndNestedBranch()
    {
        foreach (WireVector vector in Vectors())
        {
            byte[] actual = vector.IsClient
                ? ProtocolCodec.EncodeClientMessage(vector.Message)
                : ProtocolCodec.EncodeServerMessage(vector.Message);

            Assert.Equal(vector.ExpectedHex, Convert.ToHexString(actual).ToLowerInvariant());
        }
    }

    private static IEnumerable<WireVector> Vectors()
    {
        yield return new WireVector(
            "client.hello",
            true,
            Map(("type", "hello"), ("version", 1L)),
            "00000015a264747970656568656c6c6f6776657273696f6e01");
        yield return new WireVector(
            "client.list",
            true,
            Map(
                ("type", "request"),
                ("id", "request-1"),
                ("request", Map(("command", "list")))),
            "00000031a36474797065677265717565737462696469726571756573742d316772657175657374a167636f6d6d616e64646c697374");
        yield return new WireVector(
            "client.create",
            true,
            Map(
                ("type", "request"),
                ("id", "request-2"),
                ("request", Map(
                    ("command", "create"),
                    ("cwd", "/workspace"),
                    ("name", "New"),
                    ("model", Map(("provider", "test"), ("id", "model"))),
                    ("thinkingLevel", "low")))),
            "0000007ba36474797065677265717565737462696469726571756573742d326772657175657374a567636f6d6d616e6466637265617465636377646a2f776f726b7370616365646e616d65634e6577656d6f64656ca26870726f76696465726474657374626964656d6f64656c6d7468696e6b696e674c6576656c636c6f77");
        yield return new WireVector(
            "client.attach",
            true,
            Map(
                ("type", "request"),
                ("id", "request-3"),
                ("request", Map(("command", "attach"), ("sessionId", "session-1")))),
            "00000047a36474797065677265717565737462696469726571756573742d336772657175657374a267636f6d6d616e64666174746163686973657373696f6e49646973657373696f6e2d31");
        yield return new WireVector(
            "client.detach",
            true,
            Map(
                ("type", "request"),
                ("id", "request-4"),
                ("request", Map(("command", "detach"), ("sessionId", "session-1")))),
            "00000047a36474797065677265717565737462696469726571756573742d346772657175657374a267636f6d6d616e64666465746163686973657373696f6e49646973657373696f6e2d31");
        yield return new WireVector(
            "client.prompt",
            true,
            Map(
                ("type", "request"),
                ("id", "request-5"),
                ("request", Map(("command", "prompt"), ("sessionId", "session-1"), ("text", "hello")))),
            "00000052a36474797065677265717565737462696469726571756573742d356772657175657374a367636f6d6d616e646670726f6d70746973657373696f6e49646973657373696f6e2d3164746578746568656c6c6f");
        yield return new WireVector(
            "client.steer",
            true,
            Map(
                ("type", "request"),
                ("id", "request-6"),
                ("request", Map(("command", "steer"), ("sessionId", "session-1"), ("text", "continue")))),
            "00000054a36474797065677265717565737462696469726571756573742d366772657175657374a367636f6d6d616e646573746565726973657373696f6e49646973657373696f6e2d31647465787468636f6e74696e7565");
        yield return new WireVector(
            "client.abort",
            true,
            Map(
                ("type", "request"),
                ("id", "request-7"),
                ("request", Map(("command", "abort"), ("sessionId", "session-1")))),
            "00000046a36474797065677265717565737462696469726571756573742d376772657175657374a267636f6d6d616e646561626f72746973657373696f6e49646973657373696f6e2d31");
        yield return new WireVector(
            "client.set_model",
            true,
            Map(
                ("type", "request"),
                ("id", "request-8"),
                ("request", Map(
                    ("command", "set_model"),
                    ("sessionId", "session-1"),
                    ("model", Map(("provider", "test"), ("id", "model-2")))))),
            "0000006aa36474797065677265717565737462696469726571756573742d386772657175657374a367636f6d6d616e64697365745f6d6f64656c6973657373696f6e49646973657373696f6e2d31656d6f64656ca26870726f76696465726474657374626964676d6f64656c2d32");
        yield return new WireVector(
            "client.set_thinking",
            true,
            Map(
                ("type", "request"),
                ("id", "request-9"),
                ("request", Map(
                    ("command", "set_thinking"),
                    ("sessionId", "session-1"),
                    ("thinkingLevel", "high")))),
            "00000060a36474797065677265717565737462696469726571756573742d396772657175657374a367636f6d6d616e646c7365745f7468696e6b696e676973657373696f6e49646973657373696f6e2d316d7468696e6b696e674c6576656c6468696768");

        yield return new WireVector("server.hello", false, Map(
            ("type", "hello"), ("version", 1L), ("connectionId", "connection-1"), ("snapshot", Snapshot())),
            "000001a4a464747970656568656c6c6f6776657273696f6e016c636f6e6e656374696f6e49646c636f6e6e656374696f6e2d3168736e617073686f74a5687365727665724964687365727665722d316f70726f746f636f6c56657273696f6e01687265766973696f6e046873657373696f6e7381a66269646973657373696f6e2d31696372656174656441740169757064617465644174026f706172656e7453657373696f6e496468706172656e742d316b73657373696f6e4e616d656d4e616d65642073657373696f6e636377646a2f776f726b7370616365666d6f64656c7381ab6870726f76696465726474657374626964656d6f64656c646e616d656a54657374206d6f64656c63617069666f70656e616969726561736f6e696e67f565696e7075748164746578746d636f6e7465787457696e646f771a0001f400696d6178546f6b656e7319100064636f7374a465696e70757401666f75747075740269636163686552656164036a636163686557726974650477737570706f727465645468696e6b696e674c6576656c7382636f6666636c6f776d61757468656e74696361746564f5");
        yield return new WireVector("server.hello_error", false, Map(
            ("type", "hello_error"), ("error", Map(("code", "version"), ("message", "Unsupported protocol version")))),
            "0000004ca264747970656b68656c6c6f5f6572726f72656572726f72a264636f64656776657273696f6e676d657373616765781c556e737570706f727465642070726f746f636f6c2076657273696f6e");
        yield return new WireVector("server.response_list", false, Map(
            ("type", "response"), ("id", "request-1"), ("ok", true),
            ("result", Map(("command", "list"), ("sessions", new object?[] { Metadata() })))),
            "000000a5a4647479706568726573706f6e736562696469726571756573742d31626f6bf566726573756c74a267636f6d6d616e64646c6973746873657373696f6e7381a66269646973657373696f6e2d31696372656174656441740169757064617465644174026f706172656e7453657373696f6e496468706172656e742d316b73657373696f6e4e616d656d4e616d65642073657373696f6e636377646a2f776f726b7370616365");

        string[] resultNames = ["create", "attach", "prompt", "steer", "abort", "set_model", "set_thinking"];
        string[] resultHex =
        [
            "000000f7a4647479706568726573706f6e736562696469726571756573742d32626f6bf566726573756c74a267636f6d6d616e64666372656174656773657373696f6ead6269646973657373696f6e2d31636377646a2f776f726b7370616365696372656174656441740169757064617465644174026570686173656469646c65656d6f64656ca26870726f76696465726474657374626964656d6f64656c6d7468696e6b696e674c6576656c666d656469756d686174746163686564f5666c6f636b6564f4687265766973696f6e036a7472616e736372697074806b717565756564537465657280707175657565645374656572436f756e7400",
            "000000f7a4647479706568726573706f6e736562696469726571756573742d33626f6bf566726573756c74a267636f6d6d616e64666174746163686773657373696f6ead6269646973657373696f6e2d31636377646a2f776f726b7370616365696372656174656441740169757064617465644174026570686173656469646c65656d6f64656ca26870726f76696465726474657374626964656d6f64656c6d7468696e6b696e674c6576656c666d656469756d686174746163686564f5666c6f636b6564f4687265766973696f6e036a7472616e736372697074806b717565756564537465657280707175657565645374656572436f756e7400",
            "000000f7a4647479706568726573706f6e736562696469726571756573742d34626f6bf566726573756c74a267636f6d6d616e646670726f6d70746773657373696f6ead6269646973657373696f6e2d31636377646a2f776f726b7370616365696372656174656441740169757064617465644174026570686173656469646c65656d6f64656ca26870726f76696465726474657374626964656d6f64656c6d7468696e6b696e674c6576656c666d656469756d686174746163686564f5666c6f636b6564f4687265766973696f6e036a7472616e736372697074806b717565756564537465657280707175657565645374656572436f756e7400",
            "000000f6a4647479706568726573706f6e736562696469726571756573742d35626f6bf566726573756c74a267636f6d6d616e646573746565726773657373696f6ead6269646973657373696f6e2d31636377646a2f776f726b7370616365696372656174656441740169757064617465644174026570686173656469646c65656d6f64656ca26870726f76696465726474657374626964656d6f64656c6d7468696e6b696e674c6576656c666d656469756d686174746163686564f5666c6f636b6564f4687265766973696f6e036a7472616e736372697074806b717565756564537465657280707175657565645374656572436f756e7400",
            "000000f6a4647479706568726573706f6e736562696469726571756573742d36626f6bf566726573756c74a267636f6d6d616e646561626f72746773657373696f6ead6269646973657373696f6e2d31636377646a2f776f726b7370616365696372656174656441740169757064617465644174026570686173656469646c65656d6f64656ca26870726f76696465726474657374626964656d6f64656c6d7468696e6b696e674c6576656c666d656469756d686174746163686564f5666c6f636b6564f4687265766973696f6e036a7472616e736372697074806b717565756564537465657280707175657565645374656572436f756e7400",
            "000000faa4647479706568726573706f6e736562696469726571756573742d37626f6bf566726573756c74a267636f6d6d616e64697365745f6d6f64656c6773657373696f6ead6269646973657373696f6e2d31636377646a2f776f726b7370616365696372656174656441740169757064617465644174026570686173656469646c65656d6f64656ca26870726f76696465726474657374626964656d6f64656c6d7468696e6b696e674c6576656c666d656469756d686174746163686564f5666c6f636b6564f4687265766973696f6e036a7472616e736372697074806b717565756564537465657280707175657565645374656572436f756e7400",
            "000000fda4647479706568726573706f6e736562696469726571756573742d38626f6bf566726573756c74a267636f6d6d616e646c7365745f7468696e6b696e676773657373696f6ead6269646973657373696f6e2d31636377646a2f776f726b7370616365696372656174656441740169757064617465644174026570686173656469646c65656d6f64656ca26870726f76696465726474657374626964656d6f64656c6d7468696e6b696e674c6576656c666d656469756d686174746163686564f5666c6f636b6564f4687265766973696f6e036a7472616e736372697074806b717565756564537465657280707175657565645374656572436f756e7400",
        ];

        for (int index = 0; index < resultNames.Length; index++)
        {
            yield return new WireVector(
                $"server.response_{resultNames[index]}",
                false,
                Map(
                    ("type", "response"),
                    ("id", $"request-{index + 2}"),
                    ("ok", true),
                    ("result", Map(("command", resultNames[index]), ("session", Session())))),
                resultHex[index]);
        }

        yield return new WireVector(
            "server.response_detach",
            false,
            Map(
                ("type", "response"),
                ("id", "request-9"),
                ("ok", true),
                ("result", Map(("command", "detach"), ("sessionId", "session-1")))),
            "0000004ba4647479706568726573706f6e736562696469726571756573742d39626f6bf566726573756c74a267636f6d6d616e64666465746163686973657373696f6e49646973657373696f6e2d31");
        yield return new WireVector(
            "server.response_error",
            false,
            Map(
                ("type", "response"),
                ("id", "request-10"),
                ("ok", false),
                ("error", Map(
                    ("code", "internal_error"),
                    ("message", "failure"),
                    ("details", Map(("retryable", false)))))),
            "00000060a4647479706568726573706f6e73656269646a726571756573742d3130626f6bf4656572726f72a364636f64656e696e7465726e616c5f6572726f72676d657373616765676661696c7572656764657461696c73a169726574727961626c65f4");
        yield return new WireVector(
            "server.event_snapshot",
            false,
            Map(("type", "event"), ("event", Map(("type", "server_snapshot"), ("snapshot", Snapshot())))),
            "0000019da26474797065656576656e74656576656e74a264747970656f7365727665725f736e617073686f7468736e617073686f74a5687365727665724964687365727665722d316f70726f746f636f6c56657273696f6e01687265766973696f6e046873657373696f6e7381a66269646973657373696f6e2d31696372656174656441740169757064617465644174026f706172656e7453657373696f6e496468706172656e742d316b73657373696f6e4e616d656d4e616d65642073657373696f6e636377646a2f776f726b7370616365666d6f64656c7381ab6870726f76696465726474657374626964656d6f64656c646e616d656a54657374206d6f64656c63617069666f70656e616969726561736f6e696e67f565696e7075748164746578746d636f6e7465787457696e646f771a0001f400696d6178546f6b656e7319100064636f7374a465696e70757401666f75747075740269636163686552656164036a636163686557726974650477737570706f727465645468696e6b696e674c6576656c7382636f6666636c6f776d61757468656e74696361746564f5");
        yield return new WireVector(
            "server.event_session_snapshot",
            false,
            Map(("type", "event"), ("event", Map(("type", "session_snapshot"), ("snapshot", Session())))),
            "000000eaa26474797065656576656e74656576656e74a264747970657073657373696f6e5f736e617073686f7468736e617073686f74ad6269646973657373696f6e2d31636377646a2f776f726b7370616365696372656174656441740169757064617465644174026570686173656469646c65656d6f64656ca26870726f76696465726474657374626964656d6f64656c6d7468696e6b696e674c6576656c666d656469756d686174746163686564f5666c6f636b6564f4687265766973696f6e036a7472616e736372697074806b717565756564537465657280707175657565645374656572436f756e7400");
        yield return new WireVector(
            "server.event_progress",
            false,
            Map(("type", "event"), ("event", Map(
                ("type", "session_progress"), ("sessionId", "session-1"),
                ("progress", Map(("type", "assistant_delta"), ("messageId", "assistant-1"), ("contentIndex", 0L), ("kind", "text"), ("delta", "hi")))))),
            "00000093a26474797065656576656e74656576656e74a364747970657073657373696f6e5f70726f67726573736973657373696f6e49646973657373696f6e2d316870726f6772657373a564747970656f617373697374616e745f64656c7461696d65737361676549646b617373697374616e742d316c636f6e74656e74496e64657800646b696e6464746578746564656c7461626869");
        yield return new WireVector(
            "server.event_removed",
            false,
            Map(("type", "event"), ("event", Map(("type", "session_removed"), ("sessionId", "session-1")))),
            "0000003ca26474797065656576656e74656576656e74a264747970656f73657373696f6e5f72656d6f7665646973657373696f6e49646973657373696f6e2d31");
    }

    private sealed record WireVector(string Name, bool IsClient, object Message, string ExpectedHex);

    private static Dictionary<string, object?> Metadata() => Map(
        ("id", "session-1"), ("createdAt", 1L), ("updatedAt", 2L),
        ("parentSessionId", "parent-1"), ("sessionName", "Named session"), ("cwd", "/workspace"));

    private static Dictionary<string, object?> Session() => Map(
        ("id", "session-1"), ("cwd", "/workspace"), ("createdAt", 1L), ("updatedAt", 2L),
        ("phase", "idle"), ("model", Map(("provider", "test"), ("id", "model"))),
        ("thinkingLevel", "medium"), ("attached", true), ("locked", false), ("revision", 3L),
        ("transcript", Array.Empty<object?>()), ("queuedSteer", Array.Empty<object?>()), ("queuedSteerCount", 0L));

    private static Dictionary<string, object?> Snapshot() => Map(
        ("serverId", "server-1"), ("protocolVersion", 1L), ("revision", 4L),
        ("sessions", new object?[] { Metadata() }),
        ("models", new object?[]
        {
            Map(
                ("provider", "test"), ("id", "model"), ("name", "Test model"), ("api", "openai"),
                ("reasoning", true), ("input", new object?[] { "text" }), ("contextWindow", 128000L),
                ("maxTokens", 4096L),
                ("cost", Map(("input", 1L), ("output", 2L), ("cacheRead", 3L), ("cacheWrite", 4L))),
                ("supportedThinkingLevels", new object?[] { "off", "low" }), ("authenticated", true)),
        }));

    private static Dictionary<string, object?> Map(params (string Key, object? Value)[] values)
    {
        Dictionary<string, object?> result = new(StringComparer.Ordinal);
        foreach ((string key, object? value) in values)
        {
            result.Add(key, value);
        }

        return result;
    }
}

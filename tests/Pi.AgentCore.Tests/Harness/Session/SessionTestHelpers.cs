using System.Text.Json.Nodes;
using Pi.AgentCore.Harness.Session;
using Pi.AgentCore.Harness.Session.Jsonl;
using Pi.Ai;

namespace Pi.AgentCore.Tests.Harness.Session;

internal static class SessionTestHelpers
{
    public static string CreateTempRoot(string prefix = "pi-csharp-h1-")
    {
        var root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    public static void DeleteTempRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static JsonlSessionRepo CreateRepository(string root) => new(root);

    public static async Task<Session<JsonlSessionMetadata>> ReopenAsync(
        string root,
        Session<JsonlSessionMetadata> session)
    {
        var metadata = await session.GetMetadataAsync().ConfigureAwait(false);
        return await CreateRepository(root).OpenAsync(metadata).ConfigureAwait(false);
    }

    public static UserMessage User(string text, long timestamp = 1) =>
        UserMessage.Blocks([new TextContent(text)], timestamp);

    public static AssistantMessage Assistant(
        string text,
        long timestamp = 1,
        string stopReason = "stop") =>
        new()
        {
            Content = [new TextContent(text)],
            Api = "anthropic-messages",
            Provider = "anthropic",
            Model = "claude-sonnet-4-5",
            Usage = Usage(0),
            StopReason = stopReason,
            Timestamp = timestamp,
        };

    public static ToolResultMessage ToolResult(string text, long timestamp = 1) =>
        new()
        {
            ToolCallId = "call-1",
            ToolName = "read",
            Content = [new TextContent(text)],
            Details = new JsonObject { ["path"] = "README.md" },
            Usage = Usage(0),
            IsError = false,
            Timestamp = timestamp,
        };

    public static Usage Usage(int multiplier) =>
        new()
        {
            Input = multiplier,
            Output = multiplier * 2,
            CacheRead = multiplier * 3,
            CacheWrite = multiplier * 4,
            TotalTokens = multiplier * 10,
            Cost = new UsageCost
            {
                Input = multiplier * 0.1,
                Output = multiplier * 0.2,
                CacheRead = multiplier * 0.3,
                CacheWrite = multiplier * 0.4,
                Total = multiplier,
            },
        };

    public static OperationStartedRecord RunStarted(
        string id = "run",
        string lane = "main",
        string? sourceLeafId = null) =>
        new()
        {
            Id = id,
            Lane = lane,
            SourceLeafId = sourceLeafId,
            Intent = new RunOperationIntent
            {
                OriginalPrompt = [],
                InitialMessages = [],
            },
        };

    public static JsonObject Object(params (string Key, JsonNode? Value)[] values)
    {
        var result = new JsonObject();
        foreach (var (key, value) in values)
        {
            result[key] = value;
        }

        return result;
    }

    public static JsonObject ParseObject(string json) =>
        JsonNode.Parse(json)?.AsObject() ?? throw new InvalidOperationException("Expected a JSON object.");

    public static void WriteLines(string path, params JsonObject[] lines) =>
        File.WriteAllText(path, string.Join('\n', lines.Select(line => line.ToJsonString())) + "\n");
}

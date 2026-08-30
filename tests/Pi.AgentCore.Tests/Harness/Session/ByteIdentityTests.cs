using System.Text.Json.Nodes;

using Pi.AgentCore.Harness.Session;
using Pi.AgentCore.Harness.Session.Jsonl;

using Xunit;

namespace Pi.AgentCore.Tests.Harness.Session;

public sealed class ByteIdentityTests
{
    [Fact(DisplayName = "matches the TypeScript JSONL v4 fixture byte for byte")]
    public void Matches_the_TypeScript_JSONL_v4_fixture_byte_for_byte()
    {
        var header = new JsonlV4Header
        {
            Id = "fixture",
            CreatedAt = 1_700_000_000_000,
            Cwd = "/workspace/project",
            ParentSessionId = "parent",
            Metadata = new JsonObject
            {
                ["owner"] = "agent",
                ["nested"] = new JsonObject { ["enabled"] = true },
                ["values"] = new JsonArray(1, null, "two"),
            },
        };
        var mutations = new SessionMutation[]
        {
            new EntryMutation
            {
                Seq = 1,
                Lane = "main",
                Entry = new CustomEntry
                {
                    Id = "entry-1",
                    CustomType = "note",
                    Data = new JsonObject { ["text"] = "hello" },
                    DataPresent = true,
                    ParentId = null,
                    Seq = 1,
                    Timestamp = 100,
                },
            },
            new LaneMutation { Seq = 2, Lane = "thread", LeafId = "entry-1" },
            new FactMutation { Seq = 3, Fact = "name", Name = "Example" },
            new FactMutation { Seq = 4, Fact = "name", Name = null },
            new RecordMutation
            {
                Seq = 5,
                Record = new OperationStartedRecord
                {
                    Id = "run-1",
                    Lane = "thread",
                    SourceLeafId = null,
                    Intent = new RunOperationIntent { OriginalPrompt = [], InitialMessages = [] },
                    Seq = 5,
                    Timestamp = 100,
                },
            },
        };

        var actual = JsonlCodec.EncodeHeader(header) + string.Concat(mutations.Select(JsonlCodec.EncodeMutation));
        Assert.Equal(LoadFixture(), actual);
    }

    private static string LoadFixture()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "Harness", "Session", "Fixtures", "jsonl-v4-basic.jsonl");
            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("The committed byte-identity fixture was not found.");
    }
}

using System.Text.Json.Nodes;

using Pi.AgentCore.Harness.Session;
using Pi.AgentCore.Harness.Session.Jsonl;

using Xunit;

namespace Pi.AgentCore.Tests.Harness.Session;

public sealed class JsonlCodecTests
{
    [Fact(DisplayName = "round trips every header field with a resolved parent")]
    public void Round_trips_every_header_field_with_a_resolved_parent()
    {
        var header = new JsonlV4Header
        {
            Id = "session",
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

        AssertHeaderRoundTrip(header);
    }

    [Fact(DisplayName = "round trips an unresolved legacy parent path")]
    public void Round_trips_an_unresolved_legacy_parent_path()
    {
        var header = new JsonlV4Header
        {
            Id = "legacy-child",
            CreatedAt = 1_700_000_000_001,
            Cwd = "/workspace/project",
            LegacyParentSessionPath = "/sessions/missing-parent.jsonl",
        };

        AssertHeaderRoundTrip(header);
    }

    [Fact(DisplayName = "projects header and filesystem fields into metadata")]
    public void Projects_header_and_filesystem_fields_into_metadata()
    {
        var header = new JsonlV4Header
        {
            Id = "session",
            CreatedAt = 1_700_000_000_000,
            Cwd = "/workspace/project",
            LegacyParentSessionPath = "/sessions/missing-parent.jsonl",
            Metadata = new JsonObject { ["owner"] = "agent" },
        };

        var metadata = JsonlCodec.MetadataFromHeader(header, "/sessions/session.jsonl", 1_700_000_000_100);

        Assert.Equal("session", metadata.Id);
        Assert.Equal(1_700_000_000_000, metadata.CreatedAt);
        Assert.Equal("/workspace/project", metadata.Cwd);
        Assert.Equal("/sessions/session.jsonl", metadata.Path);
        Assert.Equal(1_700_000_000_100, metadata.ModifiedAt);
        Assert.Equal(4, metadata.SourceFormat);
        Assert.Equal("/sessions/missing-parent.jsonl", metadata.LegacyParentSessionPath);
        Assert.Equal("agent", metadata.Metadata!["owner"]!.GetValue<string>());
    }

    [Fact(DisplayName = "returns syntax and schema errors")]
    public void Returns_syntax_and_schema_errors()
    {
        var syntax = JsonlCodec.ParseMutation("{");
        var schema = JsonlCodec.ParseMutation("{\"kind\":\"unknown\",\"seq\":1}");

        Assert.False(syntax.IsSuccess);
        Assert.False(schema.IsSuccess);
        Assert.Equal(JsonlDecodeErrorKind.Syntax, syntax.Error!.Kind);
        Assert.Equal(JsonlDecodeErrorKind.Schema, schema.Error!.Kind);
    }

    [Fact(DisplayName = "round trips a lane-bound entry line")]
    public void Round_trips_a_lane_bound_entry_line()
    {
        var mutation = new EntryMutation
        {
            Seq = 1,
            Lane = "main",
            Entry = new CustomEntry
            {
                Id = "entry-1",
                ParentId = null,
                Seq = 1,
                Timestamp = 100,
                CustomType = "note",
                Data = new JsonObject { ["text"] = "hello" },
                DataPresent = true,
            },
        };

        AssertMutationRoundTrip(mutation);
    }

    [Fact(DisplayName = "round trips an imported entry line without a lane")]
    public void Round_trips_an_imported_entry_line_without_a_lane()
    {
        var mutation = new EntryMutation
        {
            Seq = 1,
            Entry = new CustomEntry
            {
                Id = "entry-1",
                ParentId = null,
                Seq = 1,
                Timestamp = 100,
                CustomType = "note",
                DataPresent = false,
            },
        };

        AssertMutationRoundTrip(mutation);
    }

    [Fact(DisplayName = "round trips a record line")]
    public void Round_trips_a_record_line()
    {
        var mutation = new RecordMutation
        {
            Seq = 1,
            Record = SessionTestHelpers.RunStarted("run-1") with { Lane = "main", Seq = 1, Timestamp = 100 },
        };

        AssertMutationRoundTrip(mutation);
    }

    [Fact(DisplayName = "round trips a lane line")]
    public void Round_trips_a_lane_line()
    {
        AssertMutationRoundTrip(new LaneMutation { Seq = 1, Lane = "thread", LeafId = "entry-1" });
    }

    [Fact(DisplayName = "round trips fact lines, including cleared values")]
    public void Round_trips_fact_lines_including_cleared_values()
    {
        AssertMutationRoundTrip(new FactMutation { Seq = 1, Fact = "name", Name = "Example" });
        AssertMutationRoundTrip(new FactMutation { Seq = 2, Fact = "name", Name = null });
        AssertMutationRoundTrip(new FactMutation { Seq = 3, Fact = "label", TargetId = "entry-1", Label = "checkpoint" });
    }

    [Fact(DisplayName = "rejects a custom entry without customType")]
    public void Rejects_a_custom_entry_without_custom_type()
    {
        AssertSchemaFailure(new JsonObject
        {
            ["kind"] = "entry",
            ["type"] = "custom",
            ["id"] = "entry",
            ["parentId"] = null,
            ["seq"] = 1,
            ["timestamp"] = 1,
        });
    }

    [Fact(DisplayName = "rejects an operation_started record without intent")]
    public void Rejects_an_operation_started_record_without_intent()
    {
        AssertSchemaFailure(new JsonObject
        {
            ["kind"] = "record",
            ["type"] = "operation_started",
            ["id"] = "run",
            ["lane"] = "main",
            ["seq"] = 1,
            ["timestamp"] = 1,
            ["sourceLeafId"] = null,
        });
    }

    [Fact(DisplayName = "rejects an operation_finished record without runId")]
    public void Rejects_an_operation_finished_record_without_run_id()
    {
        AssertSchemaFailure(new JsonObject
        {
            ["kind"] = "record",
            ["type"] = "operation_finished",
            ["id"] = "finish",
            ["lane"] = "main",
            ["seq"] = 1,
            ["timestamp"] = 1,
            ["outcome"] = "completed",
        });
    }

    private static void AssertHeaderRoundTrip(JsonlV4Header expected)
    {
        var encoded = JsonlCodec.EncodeHeader(expected);
        Assert.EndsWith("\n", encoded);
        var parsed = JsonlCodec.ParseHeader(encoded.TrimEnd('\r', '\n'));
        Assert.True(parsed.IsSuccess, parsed.Error?.Message);
        Assert.Equal(expected.Kind, parsed.Value!.Kind);
        Assert.Equal(expected.Version, parsed.Value.Version);
        Assert.Equal(expected.Id, parsed.Value.Id);
        Assert.Equal(expected.CreatedAt, parsed.Value.CreatedAt);
        Assert.Equal(expected.Cwd, parsed.Value.Cwd);
        Assert.Equal(expected.ParentSessionId, parsed.Value.ParentSessionId);
        Assert.Equal(expected.LegacyParentSessionPath, parsed.Value.LegacyParentSessionPath);
        Assert.Equal(expected.Metadata?.ToJsonString(), parsed.Value.Metadata?.ToJsonString());
    }

    private static void AssertMutationRoundTrip(SessionMutation mutation)
    {
        var encoded = JsonlCodec.EncodeMutation(mutation);
        Assert.EndsWith("\n", encoded);
        var parsed = JsonlCodec.ParseMutation(encoded.TrimEnd('\r', '\n'));
        Assert.True(parsed.IsSuccess, parsed.Error?.Message);
        Assert.Equal(encoded, JsonlCodec.EncodeMutation(parsed.Value!));
    }

    private static void AssertSchemaFailure(JsonObject value)
    {
        var parsed = JsonlCodec.ParseMutation(value.ToJsonString());
        Assert.False(parsed.IsSuccess);
        Assert.Equal(JsonlDecodeErrorKind.Schema, parsed.Error!.Kind);
    }
}

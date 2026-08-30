using System.Text.Json.Nodes;

namespace Pi.AgentCore.Harness.Session;

/// <summary>Current model selection projected from a session path.</summary>
public sealed record SessionModel
{
    /// <summary>Provider identifier.</summary>
    public required string Provider { get; init; }

    /// <summary>Model identifier.</summary>
    public required string ModelId { get; init; }
}

/// <summary>Context materialized from a session branch.</summary>
public sealed record SessionContext
{
    /// <summary>Messages sent to the next model request.</summary>
    public IReadOnlyList<AgentMessage> Messages { get; init; } = [];

    /// <summary>Last projected thinking level.</summary>
    public string ThinkingLevel { get; init; } = "off";

    /// <summary>Last projected model, or null before one is selected.</summary>
    public SessionModel? Model { get; init; }

    /// <summary>Active tool names, or null before the first active-tools entry.</summary>
    public IReadOnlyList<string>? ActiveToolNames { get; init; }
}

/// <summary>Transforms a branch's entries before message materialization.</summary>
public delegate IReadOnlyList<Entry> SessionEntryTransform(IReadOnlyList<Entry> entries);

/// <summary>Projects an application-defined custom entry into messages.</summary>
public delegate IReadOnlyList<AgentMessage> SessionEntryProjector(CustomEntry entry);

/// <summary>Options for context projection.</summary>
public sealed record SessionContextOptions
{
    /// <summary>Transforms applied in declaration order.</summary>
    public IReadOnlyList<SessionEntryTransform> EntryTransforms { get; init; } = [];

    /// <summary>Projectors indexed by custom entry type.</summary>
    public IReadOnlyDictionary<string, SessionEntryProjector> EntryProjectors { get; init; } =
        new Dictionary<string, SessionEntryProjector>(StringComparer.Ordinal);
}

/// <summary>Builds the default context entry boundary at the latest compaction.</summary>
public static class SessionContextBuilder
{
    /// <summary>Returns the latest compaction and entries after it.</summary>
    public static IReadOnlyList<Entry> DefaultContextEntryTransform(IReadOnlyList<Entry> entries)
    {
        var index = -1;
        for (var candidate = entries.Count - 1; candidate >= 0; candidate--)
        {
            if (entries[candidate] is CompactionEntry)
            {
                index = candidate;
                break;
            }
        }

        return index < 0 ? entries.ToArray() : entries.Skip(index).ToArray();
    }

    /// <summary>Builds session context from an oldest-first branch path.</summary>
    public static SessionContext BuildSessionContext(
        IReadOnlyList<Entry> entries,
        SessionContextOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var actualOptions = options ?? new SessionContextOptions();
        var transformed = (IReadOnlyList<Entry>)entries.ToArray();
        transformed = DefaultContextEntryTransform(transformed);
        foreach (var transform in actualOptions.EntryTransforms)
        {
            transformed = transform(transformed);
        }

        string thinkingLevel = "off";
        SessionModel? model = null;
        IReadOnlyList<string>? activeToolNames = null;
        foreach (var entry in entries)
        {
            switch (entry)
            {
                case ThinkingLevelEntry thinking:
                    thinkingLevel = thinking.ThinkingLevel;
                    break;
                case ModelChangeEntry modelChange:
                    model = new SessionModel { Provider = modelChange.Provider, ModelId = modelChange.ModelId };
                    break;
                case ActiveToolsEntry activeTools:
                    activeToolNames = activeTools.ActiveToolNames.ToArray();
                    break;
                case MessageEntry message when message.Message.Role == "assistant":
                    var provider = SessionJson.GetString(message.Message.Value, "provider");
                    var modelId = SessionJson.GetString(message.Message.Value, "model");
                    if (provider is not null && modelId is not null)
                    {
                        model = new SessionModel { Provider = provider, ModelId = modelId };
                    }

                    break;
            }
        }

        var messages = new List<AgentMessage>();
        foreach (var entry in transformed)
        {
            switch (entry)
            {
                case MessageEntry message:
                    if (!(message.Message.Role == "assistant" &&
                          SessionJson.GetString(message.Message.Value, "stopReason") == "deferred"))
                    {
                        messages.Add(AgentMessage.FromJson(SessionJson.CloneObject(message.Message.Value)));
                    }

                    break;
                case CompactionEntry compaction:
                    messages.Add(AgentMessage.FromJson(new JsonObject
                    {
                        ["role"] = "compactionSummary",
                        ["summary"] = compaction.Summary,
                        ["tokensBefore"] = compaction.TokensBefore,
                        ["timestamp"] = compaction.Timestamp,
                    }));
                    messages.AddRange(compaction.RetainedTail.Select(item => AgentMessage.FromJson(SessionJson.CloneObject(item.Value))));
                    break;
                case BranchSummaryEntry branch when branch.Summary.Length > 0:
                    messages.Add(AgentMessage.FromJson(new JsonObject
                    {
                        ["role"] = "branchSummary",
                        ["summary"] = branch.Summary,
                        ["fromId"] = branch.FromId,
                        ["timestamp"] = branch.Timestamp,
                    }));
                    break;
                case CustomEntry custom when actualOptions.EntryProjectors.TryGetValue(custom.CustomType, out var projector):
                    messages.AddRange(projector(custom));
                    break;
            }
        }

        return new SessionContext
        {
            Messages = messages,
            ThinkingLevel = thinkingLevel,
            Model = model,
            ActiveToolNames = activeToolNames,
        };
    }
}

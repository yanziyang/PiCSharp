using System.Text.Json.Nodes;
using Pi.AgentCore.Harness.Session;
using Pi.Ai;
using SessionAgentMessage = Pi.AgentCore.Harness.Session.AgentMessage;

namespace Pi.AgentCore.Harness.Compaction;

/// <summary>Generated branch-summary data ready to be persisted.</summary>
public sealed record BranchSummaryResult
{
    /// <summary>Generated branch summary.</summary>
    public required string Summary { get; init; }

    /// <summary>Provider usage for the summary request, when a request was made.</summary>
    public Usage? Usage { get; init; }

    /// <summary>Files read while exploring the summarized branch.</summary>
    public IReadOnlyList<string> ReadFiles { get; init; } = [];

    /// <summary>Files modified while exploring the summarized branch.</summary>
    public IReadOnlyList<string> ModifiedFiles { get; init; } = [];
}

/// <summary>File-operation details stored on generated branch-summary entries.</summary>
public sealed record BranchSummaryDetails
{
    /// <summary>Files read while exploring the summarized branch.</summary>
    public IReadOnlyList<string> ReadFiles { get; init; } = [];

    /// <summary>Files modified while exploring the summarized branch.</summary>
    public IReadOnlyList<string> ModifiedFiles { get; init; } = [];
}

/// <summary>Prepared branch content for summarization.</summary>
public sealed record BranchPreparation
{
    /// <summary>Messages selected for the branch summary.</summary>
    public IReadOnlyList<SessionAgentMessage> Messages { get; init; } = [];

    /// <summary>File operations extracted from the branch.</summary>
    public required FileOperations FileOps { get; init; }

    /// <summary>Estimated token count for selected messages.</summary>
    public int TotalTokens { get; init; }
}

/// <summary>Entries selected for branch summarization.</summary>
public sealed record CollectEntriesResult
{
    /// <summary>Entries to summarize in chronological order.</summary>
    public IReadOnlyList<Entry> Entries { get; init; } = [];

    /// <summary>Deepest common ancestor between the previous leaf and target entry.</summary>
    public string? CommonAncestorId { get; init; }
}

/// <summary>Options for generating a branch summary.</summary>
public sealed record GenerateBranchSummaryOptions
{
    /// <summary>Provider collection through which the request is sent.</summary>
    public required Models Models { get; init; }

    /// <summary>Model used for summarization.</summary>
    public required Model Model { get; init; }

    /// <summary>Cancellation signal for the summarization request.</summary>
    public CancellationToken Signal { get; init; }

    /// <summary>Optional instructions appended to or replacing the default prompt.</summary>
    public string? CustomInstructions { get; init; }

    /// <summary>Replaces the default prompt with custom instructions.</summary>
    public bool ReplaceInstructions { get; init; }

    /// <summary>Tokens reserved for prompt and model output.</summary>
    public int ReserveTokens { get; init; } = 16384;

    /// <summary>Optional retry policy for transient summarization errors.</summary>
    public RetryPolicy? Retry { get; init; }

    /// <summary>Optional callbacks for retry reporting.</summary>
    public RetryCallbacks? Callbacks { get; init; }
}

/// <summary>Branch traversal and summarization algorithms.</summary>
public static class BranchSummarization
{
    private const string _branchSummaryPreamble = "The user explored a different conversation branch before returning here.\nSummary of that exploration:\n\n";

    private const string _branchSummaryPrompt = "Create a structured summary of this conversation branch for context when returning later.\n\nUse this EXACT format:\n\n## Goal\n[What was the user trying to accomplish in this branch?]\n\n## Constraints & Preferences\n- [Any constraints, preferences, or requirements mentioned]\n- [Or \"(none)\" if none were mentioned]\n\n## Progress\n### Done\n- [x] [Completed tasks/changes]\n\n### In Progress\n- [ ] [Work that was started but not finished]\n\n### Blocked\n- [Issues preventing progress, if any]\n\n## Key Decisions\n- **[Decision]**: [Brief rationale]\n\n## Next Steps\n1. [What should happen next to continue this work]\n\nKeep each section concise. Preserve exact file paths, function names, and error messages.";

    /// <summary>Collects abandoned branch entries in chronological order.</summary>
    public static async Task<CollectEntriesResult> CollectEntriesForBranchSummaryAsync<TMetadata>(
        Session<TMetadata> session,
        string? oldLeafId,
        string targetId,
        CancellationToken cancellationToken = default)
        where TMetadata : SessionMetadata
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(targetId);
        if (oldLeafId is null)
        {
            return new CollectEntriesResult { CommonAncestorId = null };
        }

        var oldPath = await session.FindEntriesOnBranchAsync(
            query: null,
            bounds: new BranchBounds { Start = oldLeafId },
            cancellationToken).ConfigureAwait(false);
        var oldPathIds = oldPath.Select(static entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        var targetPath = await session.FindEntriesOnBranchAsync(
            query: null,
            bounds: new BranchBounds { Start = targetId },
            cancellationToken).ConfigureAwait(false);
        string? commonAncestorId = null;
        foreach (var entry in targetPath)
        {
            if (oldPathIds.Contains(entry.Id))
            {
                commonAncestorId = entry.Id;
                break;
            }
        }

        var entries = new List<Entry>();
        var current = oldLeafId;
        while (current is not null && current != commonAncestorId)
        {
            var entry = await session.GetEntryAsync(current, cancellationToken).ConfigureAwait(false);
            if (entry is null)
            {
                throw new SessionError(SessionErrorCode.InvalidEntry, $"Entry {current} not found");
            }

            entries.Add(entry);
            current = entry.ParentId;
        }

        entries.Reverse();
        return new CollectEntriesResult
        {
            Entries = entries,
            CommonAncestorId = commonAncestorId,
        };
    }

    /// <summary>Pascal-case alias for branch-entry collection.</summary>
    public static Task<CollectEntriesResult> CollectEntriesForBranchSummary<TMetadata>(
        Session<TMetadata> session,
        string? oldLeafId,
        string targetId,
        CancellationToken cancellationToken = default)
        where TMetadata : SessionMetadata =>
        CollectEntriesForBranchSummaryAsync(session, oldLeafId, targetId, cancellationToken);

    /// <summary>Prepares branch entries within an optional token budget.</summary>
    public static BranchPreparation PrepareBranchEntries(IReadOnlyList<Entry> entries, int tokenBudget = 0)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var messages = new List<SessionAgentMessage>();
        var fileOps = CompactionUtilities.CreateFileOps();
        var totalTokens = 0;

        foreach (var entry in entries)
        {
            if (entry is BranchSummaryEntry branch)
            {
                AddPersistedFileDetails(branch.Details, fileOps);
            }
        }

        for (var index = entries.Count - 1; index >= 0; index--)
        {
            var entry = entries[index];
            var message = GetMessageFromEntry(entry);
            if (message is null)
            {
                continue;
            }

            CompactionUtilities.ExtractFileOpsFromMessage(message, fileOps);
            var tokens = Compaction.EstimateTokens(message);
            if (tokenBudget > 0 && totalTokens + tokens > tokenBudget)
            {
                if (entry is CompactionEntry or BranchSummaryEntry && totalTokens < tokenBudget * 0.9)
                {
                    messages.Insert(0, message);
                    totalTokens += tokens;
                }

                break;
            }

            messages.Insert(0, message);
            totalTokens += tokens;
        }

        return new BranchPreparation
        {
            Messages = messages,
            FileOps = fileOps,
            TotalTokens = totalTokens,
        };
    }

    /// <summary>Generates a summary for abandoned branch entries.</summary>
    public static async Task<Result<BranchSummaryResult, BranchSummaryError>> GenerateBranchSummaryAsync(
        IReadOnlyList<Entry> entries,
        GenerateBranchSummaryOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(options);
        var signal = options.Signal.CanBeCanceled ? options.Signal : cancellationToken;
        var contextWindow = options.Model.ContextWindow == 0 ? 128000 : options.Model.ContextWindow;
        var tokenBudget = contextWindow - options.ReserveTokens;
        var preparation = PrepareBranchEntries(entries, tokenBudget);
        if (preparation.Messages.Count == 0)
        {
            return Result<BranchSummaryResult, BranchSummaryError>.Success(new BranchSummaryResult
            {
                Summary = "No content to summarize",
            });
        }

        var conversationText = CompactionUtilities.SerializeConversation(
            HarnessMessageUtilities.ConvertToLlm(preparation.Messages));
        var instructions = options.ReplaceInstructions && !string.IsNullOrEmpty(options.CustomInstructions)
            ? options.CustomInstructions
            : !string.IsNullOrEmpty(options.CustomInstructions)
                ? $"{_branchSummaryPrompt}\n\nAdditional focus: {options.CustomInstructions}"
                : _branchSummaryPrompt;
        var promptText = $"<conversation>\n{conversationText}\n</conversation>\n\n{instructions}";
        var response = await Compaction.CompleteSimpleWithRetriesAsync(
            options.Models,
            options.Model,
            new Context
            {
                SystemPrompt = Compaction.SummarizationSystemPrompt,
                Messages = [UserMessage.Text(promptText, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())],
            },
            new ModelsSimpleStreamOptions
            {
                Signal = signal,
                MaxTokens = 2048,
            },
            options.Retry,
            options.Callbacks,
            signal).ConfigureAwait(false);

        if (response.StopReason == StopReasons.Aborted)
        {
            return Result<BranchSummaryResult, BranchSummaryError>.Failure(
                new BranchSummaryError(BranchSummaryErrorCodes.Aborted, response.ErrorMessage ?? "Branch summary aborted"));
        }

        if (response.StopReason == StopReasons.Error)
        {
            return Result<BranchSummaryResult, BranchSummaryError>.Failure(
                new BranchSummaryError(
                    BranchSummaryErrorCodes.SummarizationFailed,
                    $"Branch summary failed: {response.ErrorMessage ?? "Unknown error"}"));
        }

        var summary = _branchSummaryPreamble + HarnessMessageUtilities.ContentText(response);
        var (readFiles, modifiedFiles) = CompactionUtilities.ComputeFileLists(preparation.FileOps);
        summary += CompactionUtilities.FormatFileOperations(readFiles, modifiedFiles);
        return Result<BranchSummaryResult, BranchSummaryError>.Success(new BranchSummaryResult
        {
            Summary = string.IsNullOrEmpty(summary) ? "No summary generated" : summary,
            Usage = response.Usage,
            ReadFiles = readFiles,
            ModifiedFiles = modifiedFiles,
        });
    }

    /// <summary>Pascal-case alias for branch-summary generation.</summary>
    public static Task<Result<BranchSummaryResult, BranchSummaryError>> GenerateBranchSummary(
        IReadOnlyList<Entry> entries,
        GenerateBranchSummaryOptions options,
        CancellationToken cancellationToken = default) =>
        GenerateBranchSummaryAsync(entries, options, cancellationToken);

    private static SessionAgentMessage? GetMessageFromEntry(Entry entry)
    {
        return entry switch
        {
            MessageEntry { Message: { Role: "toolResult" } } => null,
            MessageEntry message => message.Message,
            BranchSummaryEntry branch => HarnessMessageUtilities.CreateBranchSummaryMessage(
                branch.Summary,
                branch.FromId,
                branch.Timestamp).ToAgentMessage(),
            CompactionEntry compaction => HarnessMessageUtilities.CreateCompactionSummaryMessage(
                compaction.Summary,
                compaction.TokensBefore,
                compaction.Timestamp).ToAgentMessage(),
            _ => null,
        };
    }

    private static void AddPersistedFileDetails(JsonNode? detailsNode, FileOperations fileOps)
    {
        if (detailsNode is not JsonObject details)
        {
            return;
        }

        AddFiles(details["readFiles"], fileOps.Read);
        AddFiles(details["modifiedFiles"], fileOps.Edited);
    }

    private static void AddFiles(JsonNode? node, HashSet<string> destination)
    {
        if (node is not JsonArray files)
        {
            return;
        }

        foreach (var file in files)
        {
            if (file is JsonValue value && value.TryGetValue<string>(out var path))
            {
                destination.Add(path);
            }
        }
    }
}

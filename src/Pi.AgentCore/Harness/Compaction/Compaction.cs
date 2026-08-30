using System.Text.Json.Nodes;
using Pi.AgentCore.Harness.Session;
using Pi.Ai;
using SessionAgentMessage = Pi.AgentCore.Harness.Session.AgentMessage;

namespace Pi.AgentCore.Harness.Compaction;

/// <summary>File-operation details stored on generated compaction entries.</summary>
public sealed record CompactionDetails
{
    /// <summary>Files read in the compacted history.</summary>
    public IReadOnlyList<string> ReadFiles { get; init; } = [];

    /// <summary>Files modified in the compacted history.</summary>
    public IReadOnlyList<string> ModifiedFiles { get; init; } = [];
}

/// <summary>Summary text and provider usage returned by summary generation.</summary>
public sealed record SummaryWithUsage
{
    /// <summary>Generated summary text.</summary>
    public required string Text { get; init; }

    /// <summary>Provider usage for the summary request.</summary>
    public required Usage Usage { get; init; }
}

/// <summary>Generated compaction data ready to be persisted as a compaction entry.</summary>
public sealed record CompactResult
{
    /// <summary>Summary text that replaces compacted history in future context.</summary>
    public required string Summary { get; init; }

    /// <summary>Estimated context tokens before compaction.</summary>
    public long TokensBefore { get; init; }

    /// <summary>Usage from the LLM call or calls that generated the summary.</summary>
    public Usage? Usage { get; init; }

    /// <summary>Retained recent messages stored directly on the compaction entry.</summary>
    public IReadOnlyList<SessionAgentMessage> RetainedTail { get; init; } = [];

    /// <summary>File-operation details stored with the compaction entry.</summary>
    public CompactionDetails? Details { get; init; }
}

/// <summary>Generated compaction data with application-defined details.</summary>
public sealed record CompactResult<TDetails>
{
    /// <summary>Summary text that replaces compacted history in future context.</summary>
    public required string Summary { get; init; }

    /// <summary>Estimated context tokens before compaction.</summary>
    public long TokensBefore { get; init; }

    /// <summary>Usage from the LLM call or calls that generated the summary.</summary>
    public Usage? Usage { get; init; }

    /// <summary>Retained recent messages stored directly on the compaction entry.</summary>
    public IReadOnlyList<SessionAgentMessage> RetainedTail { get; init; } = [];

    /// <summary>Application-defined details stored with the compaction entry.</summary>
    public TDetails? Details { get; init; }
}

/// <summary>Compaction thresholds and retention settings.</summary>
public sealed record CompactionSettings
{
    /// <summary>Enable automatic compaction decisions.</summary>
    public bool Enabled { get; init; }

    /// <summary>Tokens reserved for the summary prompt and output.</summary>
    public int ReserveTokens { get; init; }

    /// <summary>Approximate recent-context tokens to keep after compaction.</summary>
    public int KeepRecentTokens { get; init; }
}

/// <summary>Compaction and summary-generation algorithms.</summary>
public static class Compaction
{
    /// <summary>Default compaction settings used by the harness.</summary>
    public static readonly CompactionSettings DefaultCompactionSettings = new()
    {
        Enabled = true,
        ReserveTokens = 16384,
        KeepRecentTokens = 20000,
    };

    /// <summary>System prompt used for all context summaries.</summary>
    public const string SummarizationSystemPrompt = "You are a context summarization assistant. Your task is to read a conversation between a user and an AI assistant, then produce a structured summary following the exact format specified.\n\nDo NOT continue the conversation. Do NOT respond to any questions in the conversation. ONLY output the structured summary.";

    private const string _summarizationPrompt = "The messages above are a conversation to summarize. Create a structured context checkpoint summary that another LLM will use to continue the work.\n\nUse this EXACT format:\n\n## Goal\n[What is the user trying to accomplish? Can be multiple items if the session covers different tasks.]\n\n## Constraints & Preferences\n- [Any constraints, preferences, or requirements mentioned by user]\n- [Or \"(none)\" if none were mentioned]\n\n## Progress\n### Done\n- [x] [Completed tasks/changes]\n\n### In Progress\n- [ ] [Current work]\n\n### Blocked\n- [Issues preventing progress, if any]\n\n## Key Decisions\n- **[Decision]**: [Brief rationale]\n\n## Next Steps\n1. [Ordered list of what should happen next]\n\n## Critical Context\n- [Any data, examples, or references needed to continue]\n- [Or \"(none)\" if not applicable]\n\nKeep each section concise. Preserve exact file paths, function names, and error messages.";

    private const string _updateSummarizationPrompt = "The messages above are NEW conversation messages to incorporate into the existing summary provided in <previous-summary> tags.\n\nUpdate the existing structured summary with new information. RULES:\n- PRESERVE all existing information from the previous summary\n- ADD new progress, decisions, and context from the new messages\n- UPDATE the Progress section: move items from \"In Progress\" to \"Done\" when completed\n- UPDATE \"Next Steps\" based on what was accomplished\n- PRESERVE exact file paths, function names, and error messages\n- If something is no longer relevant, you may remove it\n\nUse this EXACT format:\n\n## Goal\n[Preserve existing goals, add new ones if the task expanded]\n\n## Constraints & Preferences\n- [Preserve existing, add new ones discovered]\n\n## Progress\n### Done\n- [x] [Include previously done items AND newly completed items]\n\n### In Progress\n- [ ] [Current work - update based on progress]\n\n### Blocked\n- [Current blockers - remove if resolved]\n\n## Key Decisions\n- **[Decision]**: [Brief rationale] (preserve all previous, add new)\n\n## Next Steps\n1. [Update based on current state]\n\n## Critical Context\n- [Preserve important context, add new if needed]\n\nKeep each section concise. Preserve exact file paths, function names, and error messages.";

    private const string _turnPrefixSummarizationPrompt = "This is the PREFIX of a turn that was too large to keep. The SUFFIX (recent work) is retained.\n\nSummarize the prefix to provide context for the retained suffix:\n\n## Original Request\n[What did the user ask for in this turn?]\n\n## Early Progress\n- [Key decisions and work done in the prefix]\n\n## Context for Suffix\n- [Information needed to understand the kept recent work]\n\nBe concise. Focus on what's needed to understand the kept suffix.";

    /// <summary>Runs an assistant call with cache isolation and the shared retry policy.</summary>
    public static Task<AssistantMessage> CompleteSimpleWithRetriesAsync(
        Models models,
        Model model,
        Context context,
        ModelsSimpleStreamOptions? options = null,
        RetryPolicy? retry = null,
        RetryCallbacks? callbacks = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(context);

        var requestSignal = options?.Signal.CanBeCanceled == true ? options.Signal : cancellationToken;
        var requestOptions = new ModelsSimpleStreamOptions
        {
            Signal = requestSignal,
            TelemetryContext = options?.TelemetryContext,
            ApiKey = options?.ApiKey,
            Fetch = options?.Fetch,
            Environment = options?.Environment,
            OnPayload = options?.OnPayload,
            OnResponse = options?.OnResponse,
            Headers = options?.Headers,
            TimeoutMs = options?.TimeoutMs,
            MaxRetries = options?.MaxRetries,
            MaxRetryDelayMs = options?.MaxRetryDelayMs,
            Temperature = options?.Temperature,
            SamplingParameters = options?.SamplingParameters,
            MaxTokens = options?.MaxTokens,
            Transport = options?.Transport,
            CacheRetention = CacheRetentions.None,
            SessionId = Guid.CreateVersion7().ToString(),
            WebSocketConnectTimeoutMs = options?.WebSocketConnectTimeoutMs,
            Metadata = options?.Metadata,
            TransformHeaders = options?.TransformHeaders,
            ToolChoice = options?.ToolChoice,
            Reasoning = options?.Reasoning,
            Deferred = options?.Deferred ?? false,
            DeferredWindow = options?.DeferredWindow,
            ThinkingBudgets = options?.ThinkingBudgets,
        };

        return RetryUtilities.RetryAssistantCall(
            () => models.CompleteSimpleAsync(model, context, requestOptions, requestSignal),
            retry,
            callbacks,
            requestSignal);
    }

    /// <summary>Pascal-case alias for the upstream helper name.</summary>
    public static Task<AssistantMessage> CompleteSimpleWithRetries(
        Models models,
        Model model,
        Context context,
        ModelsSimpleStreamOptions? options = null,
        RetryPolicy? retry = null,
        RetryCallbacks? callbacks = null,
        CancellationToken cancellationToken = default) =>
        CompleteSimpleWithRetriesAsync(models, model, context, options, retry, callbacks, cancellationToken);

    /// <summary>Combines provider usage from two summary calls.</summary>
    public static Usage CombineUsage(Usage first, Usage second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        return new Usage
        {
            Input = first.Input + second.Input,
            Output = first.Output + second.Output,
            CacheRead = first.CacheRead + second.CacheRead,
            CacheWrite = first.CacheWrite + second.CacheWrite,
            CacheWrite1h = first.CacheWrite1h is not null || second.CacheWrite1h is not null
                ? (first.CacheWrite1h ?? 0) + (second.CacheWrite1h ?? 0)
                : null,
            Reasoning = first.Reasoning is not null || second.Reasoning is not null
                ? (first.Reasoning ?? 0) + (second.Reasoning ?? 0)
                : null,
            TotalTokens = first.TotalTokens + second.TotalTokens,
            Cost = new UsageCost
            {
                Input = first.Cost.Input + second.Cost.Input,
                Output = first.Cost.Output + second.Cost.Output,
                CacheRead = first.Cost.CacheRead + second.Cost.CacheRead,
                CacheWrite = first.Cost.CacheWrite + second.Cost.CacheWrite,
                Total = first.Cost.Total + second.Cost.Total,
            },
        };
    }

    /// <summary>Calculates total context tokens from provider usage.</summary>
    public static int CalculateContextTokens(Usage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        return usage.TotalTokens != 0
            ? usage.TotalTokens
            : usage.Input + usage.Output + usage.CacheRead + usage.CacheWrite;
    }

    /// <summary>Returns usage from the last valid assistant message in session entries.</summary>
    public static Usage? GetLastAssistantUsage(IReadOnlyList<Entry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        for (var index = entries.Count - 1; index >= 0; index--)
        {
            if (entries[index] is MessageEntry message)
            {
                var usage = GetAssistantUsage(message.Message);
                if (usage is not null)
                {
                    return usage;
                }
            }
        }

        return null;
    }

    /// <summary>Estimated context-token usage for an agent-message list.</summary>
    public sealed record ContextUsageEstimate
    {
        /// <summary>Estimated total context tokens.</summary>
        public int Tokens { get; init; }

        /// <summary>Tokens reported by the most recent assistant usage block.</summary>
        public int UsageTokens { get; init; }

        /// <summary>Estimated tokens after the most recent assistant usage block.</summary>
        public int TrailingTokens { get; init; }

        /// <summary>Index of the message that provided usage, or null when none exists.</summary>
        public int? LastUsageIndex { get; init; }
    }

    /// <summary>Estimates context tokens, using provider usage when available.</summary>
    public static ContextUsageEstimate EstimateContextTokens(IReadOnlyList<SessionAgentMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var usageInfo = GetLastAssistantUsageInfo(messages);
        if (usageInfo is null)
        {
            var estimated = messages.Sum(HarnessMessageUtilities.EstimateTokens);
            return new ContextUsageEstimate
            {
                Tokens = estimated,
                UsageTokens = 0,
                TrailingTokens = estimated,
                LastUsageIndex = null,
            };
        }

        var usageTokens = CalculateContextTokens(usageInfo.Value.Usage);
        var trailingTokens = 0;
        for (var index = usageInfo.Value.Index + 1; index < messages.Count; index++)
        {
            trailingTokens += HarnessMessageUtilities.EstimateTokens(messages[index]);
        }

        return new ContextUsageEstimate
        {
            Tokens = usageTokens + trailingTokens,
            UsageTokens = usageTokens,
            TrailingTokens = trailingTokens,
            LastUsageIndex = usageInfo.Value.Index,
        };
    }

    /// <summary>Returns whether context usage exceeds the configured threshold.</summary>
    public static bool ShouldCompact(int contextTokens, int contextWindow, CompactionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Enabled && contextTokens > contextWindow - settings.ReserveTokens;
    }

    /// <summary>Estimates tokens for one agent message.</summary>
    public static int EstimateTokens(SessionAgentMessage message) => HarnessMessageUtilities.EstimateTokens(message);

    /// <summary>Finds the user-visible message that starts the turn containing an entry.</summary>
    public static int FindTurnStartIndex(IReadOnlyList<Entry> entries, int entryIndex, int startIndex)
    {
        ArgumentNullException.ThrowIfNull(entries);
        for (var index = entryIndex; index >= startIndex; index--)
        {
            var entry = entries[index];
            if (entry is BranchSummaryEntry)
            {
                return index;
            }

            if (entry is MessageEntry message && message.Message.Role is "user" or "bashExecution")
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>Cut point selected for compaction.</summary>
    public sealed record CutPointResult
    {
        /// <summary>Index of the first entry retained after compaction.</summary>
        public int FirstKeptEntryIndex { get; init; }

        /// <summary>Index of the turn-start entry when the cut splits a turn, otherwise -1.</summary>
        public int TurnStartIndex { get; init; }

        /// <summary>Whether the selected cut point splits an in-progress turn.</summary>
        public bool IsSplitTurn { get; init; }
    }

    /// <summary>Finds the compaction cut point for an approximate recent-token budget.</summary>
    public static CutPointResult FindCutPoint(
        IReadOnlyList<Entry> entries,
        int startIndex,
        int endIndex,
        int keepRecentTokens)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var cutPoints = FindValidCutPoints(entries, startIndex, endIndex);
        if (cutPoints.Count == 0)
        {
            return new CutPointResult
            {
                FirstKeptEntryIndex = startIndex,
                TurnStartIndex = -1,
                IsSplitTurn = false,
            };
        }

        var accumulatedTokens = 0;
        var cutIndex = cutPoints[0];
        for (var index = endIndex - 1; index >= startIndex; index--)
        {
            if (entries[index] is not MessageEntry message)
            {
                continue;
            }

            accumulatedTokens += EstimateTokens(message.Message);
            if (accumulatedTokens < keepRecentTokens)
            {
                continue;
            }

            for (var cutPointIndex = 0; cutPointIndex < cutPoints.Count; cutPointIndex++)
            {
                if (cutPoints[cutPointIndex] >= index)
                {
                    cutIndex = cutPoints[cutPointIndex];
                    break;
                }
            }

            break;
        }

        while (cutIndex > startIndex)
        {
            var previous = entries[cutIndex - 1];
            if (previous is CompactionEntry or MessageEntry)
            {
                break;
            }

            cutIndex--;
        }

        var cutEntry = entries[cutIndex];
        var isUserMessage = cutEntry is MessageEntry user && user.Message.Role == "user";
        var turnStartIndex = isUserMessage ? -1 : FindTurnStartIndex(entries, cutIndex, startIndex);
        return new CutPointResult
        {
            FirstKeptEntryIndex = cutIndex,
            TurnStartIndex = turnStartIndex,
            IsSplitTurn = !isUserMessage && turnStartIndex != -1,
        };
    }

    /// <summary>Generates or updates a conversation summary.</summary>
    public static async Task<Result<string, CompactionError>> GenerateSummaryAsync(
        IReadOnlyList<SessionAgentMessage> currentMessages,
        Models models,
        Model model,
        int reserveTokens,
        string? customInstructions = null,
        string? previousSummary = null,
        string? thinkingLevel = null,
        RetryPolicy? retry = null,
        RetryCallbacks? callbacks = null,
        CancellationToken cancellationToken = default)
    {
        var result = await GenerateSummaryWithUsageAsync(
            currentMessages,
            models,
            model,
            reserveTokens,
            customInstructions,
            previousSummary,
            thinkingLevel,
            retry,
            callbacks,
            cancellationToken).ConfigureAwait(false);
        return result.Ok
            ? Result<string, CompactionError>.Success(result.Value!.Text)
            : Result<string, CompactionError>.Failure(result.Error!);
    }

    /// <summary>Pascal-case alias for summary generation.</summary>
    public static Task<Result<string, CompactionError>> GenerateSummary(
        IReadOnlyList<SessionAgentMessage> currentMessages,
        Models models,
        Model model,
        int reserveTokens,
        string? customInstructions = null,
        string? previousSummary = null,
        string? thinkingLevel = null,
        RetryPolicy? retry = null,
        RetryCallbacks? callbacks = null,
        CancellationToken cancellationToken = default) =>
        GenerateSummaryAsync(currentMessages, models, model, reserveTokens, customInstructions, previousSummary, thinkingLevel, retry, callbacks, cancellationToken);

    /// <summary>Generates or updates a conversation summary and returns provider usage.</summary>
    public static async Task<Result<SummaryWithUsage, CompactionError>> GenerateSummaryWithUsageAsync(
        IReadOnlyList<SessionAgentMessage> currentMessages,
        Models models,
        Model model,
        int reserveTokens,
        string? customInstructions = null,
        string? previousSummary = null,
        string? thinkingLevel = null,
        RetryPolicy? retry = null,
        RetryCallbacks? callbacks = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentMessages);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(model);

        var maxTokens = Math.Min(
            (int)Math.Floor(0.8 * reserveTokens),
            model.MaxTokens > 0 ? model.MaxTokens : int.MaxValue);
        var basePrompt = previousSummary is not null ? _updateSummarizationPrompt : _summarizationPrompt;
        if (!string.IsNullOrEmpty(customInstructions))
        {
            basePrompt = $"{basePrompt}\n\nAdditional focus: {customInstructions}";
        }

        var conversationText = CompactionUtilities.SerializeConversation(HarnessMessageUtilities.ConvertToLlm(currentMessages));
        var promptText = $"<conversation>\n{conversationText}\n</conversation>\n\n";
        if (previousSummary is not null)
        {
            promptText += $"<previous-summary>\n{previousSummary}\n</previous-summary>\n\n";
        }

        promptText += basePrompt;
        var requestOptions = new ModelsSimpleStreamOptions
        {
            MaxTokens = maxTokens,
            Signal = cancellationToken,
            Reasoning = model.Reasoning && !string.IsNullOrEmpty(thinkingLevel) && thinkingLevel != ThinkingLevels.Off
                ? thinkingLevel
                : null,
        };
        var response = await CompleteSimpleWithRetriesAsync(
            models,
            model,
            new Context
            {
                SystemPrompt = SummarizationSystemPrompt,
                Messages = [UserMessage.Text(promptText, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())],
            },
            requestOptions,
            retry,
            callbacks,
            cancellationToken).ConfigureAwait(false);

        if (response.StopReason == StopReasons.Aborted)
        {
            return Result<SummaryWithUsage, CompactionError>.Failure(
                new CompactionError(CompactionErrorCodes.Aborted, response.ErrorMessage ?? "Summarization aborted"));
        }

        if (response.StopReason == StopReasons.Error)
        {
            return Result<SummaryWithUsage, CompactionError>.Failure(
                new CompactionError(
                    CompactionErrorCodes.SummarizationFailed,
                    $"Summarization failed: {response.ErrorMessage ?? "Unknown error"}"));
        }

        return Result<SummaryWithUsage, CompactionError>.Success(new SummaryWithUsage
        {
            Text = HarnessMessageUtilities.ContentText(response),
            Usage = response.Usage,
        });
    }

    /// <summary>Pascal-case alias for summary generation with usage.</summary>
    public static Task<Result<SummaryWithUsage, CompactionError>> GenerateSummaryWithUsage(
        IReadOnlyList<SessionAgentMessage> currentMessages,
        Models models,
        Model model,
        int reserveTokens,
        string? customInstructions = null,
        string? previousSummary = null,
        string? thinkingLevel = null,
        RetryPolicy? retry = null,
        RetryCallbacks? callbacks = null,
        CancellationToken cancellationToken = default) =>
        GenerateSummaryWithUsageAsync(currentMessages, models, model, reserveTokens, customInstructions, previousSummary, thinkingLevel, retry, callbacks, cancellationToken);

    /// <summary>Prepares session entries for compaction.</summary>
    public static Result<CompactionPreparation?, CompactionError> PrepareCompaction(
        IReadOnlyList<Entry> pathEntries,
        CompactionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(pathEntries);
        ArgumentNullException.ThrowIfNull(settings);
        if (pathEntries.Count == 0 || pathEntries[^1] is CompactionEntry)
        {
            return Result<CompactionPreparation?, CompactionError>.Success(null);
        }

        var previousCompactionIndex = -1;
        for (var index = pathEntries.Count - 1; index >= 0; index--)
        {
            if (pathEntries[index] is CompactionEntry)
            {
                previousCompactionIndex = index;
                break;
            }
        }

        string? previousSummary = null;
        IReadOnlyList<Entry> compactableEntries = pathEntries;
        if (previousCompactionIndex >= 0)
        {
            var previousCompaction = (CompactionEntry)pathEntries[previousCompactionIndex];
            previousSummary = previousCompaction.Summary;
            var virtualRetainedEntries = previousCompaction.RetainedTail
                .Select((message, index) => (Entry)new MessageEntry
                {
                    Id = $"{previousCompaction.Id}:retained:{index}",
                    ParentId = index == 0
                        ? previousCompaction.Id
                        : $"{previousCompaction.Id}:retained:{index - 1}",
                    Seq = previousCompaction.Seq,
                    Timestamp = GetTimestamp(message),
                    Message = new SessionAgentMessage(message.Value),
                })
                .ToArray();
            compactableEntries = virtualRetainedEntries
                .Concat(pathEntries.Skip(previousCompactionIndex + 1))
                .ToArray();
        }

        var tokensBefore = EstimateContextTokens(SessionContextBuilder.BuildSessionContext(pathEntries).Messages).Tokens;
        var cutPoint = FindCutPoint(compactableEntries, 0, compactableEntries.Count, settings.KeepRecentTokens);
        var historyEnd = cutPoint.IsSplitTurn ? cutPoint.TurnStartIndex : cutPoint.FirstKeptEntryIndex;

        var messagesToSummarize = new List<SessionAgentMessage>();
        for (var index = 0; index < historyEnd; index++)
        {
            var message = GetMessageFromEntryForCompaction(compactableEntries[index]);
            if (message is not null)
            {
                messagesToSummarize.Add(message);
            }
        }

        var turnPrefixMessages = new List<SessionAgentMessage>();
        if (cutPoint.IsSplitTurn)
        {
            for (var index = cutPoint.TurnStartIndex; index < cutPoint.FirstKeptEntryIndex; index++)
            {
                var message = GetMessageFromEntryForCompaction(compactableEntries[index]);
                if (message is not null)
                {
                    turnPrefixMessages.Add(message);
                }
            }
        }

        var retainedTail = new List<SessionAgentMessage>();
        for (var index = cutPoint.FirstKeptEntryIndex; index < compactableEntries.Count; index++)
        {
            var message = GetMessageFromEntryForCompaction(compactableEntries[index]);
            if (message is not null)
            {
                retainedTail.Add(message);
            }
        }

        var fileOps = ExtractFileOperations(messagesToSummarize, pathEntries, previousCompactionIndex);
        if (cutPoint.IsSplitTurn)
        {
            foreach (var message in turnPrefixMessages)
            {
                CompactionUtilities.ExtractFileOpsFromMessage(message, fileOps);
            }
        }

        return Result<CompactionPreparation?, CompactionError>.Success(new CompactionPreparation
        {
            MessagesToSummarize = messagesToSummarize,
            TurnPrefixMessages = turnPrefixMessages,
            RetainedTail = retainedTail,
            IsSplitTurn = cutPoint.IsSplitTurn,
            TokensBefore = tokensBefore,
            PreviousSummary = previousSummary,
            FileOps = fileOps,
            Settings = settings,
        });
    }

    /// <summary>Generates compaction summary data from prepared session history.</summary>
    public static async Task<Result<CompactResult, CompactionError>> CompactAsync(
        CompactionPreparation preparation,
        Models models,
        Model model,
        string? customInstructions = null,
        string? thinkingLevel = null,
        RetryPolicy? retry = null,
        RetryCallbacks? callbacks = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(model);

        string summary;
        Usage summaryUsage;
        if (preparation.IsSplitTurn && preparation.TurnPrefixMessages.Count > 0)
        {
            var historyText = "No prior history.";
            Usage? historyUsage = null;
            if (preparation.MessagesToSummarize.Count > 0)
            {
                var historyResult = await GenerateSummaryWithUsageAsync(
                    preparation.MessagesToSummarize,
                    models,
                    model,
                    preparation.Settings.ReserveTokens,
                    customInstructions,
                    preparation.PreviousSummary,
                    thinkingLevel,
                    retry,
                    callbacks,
                    cancellationToken).ConfigureAwait(false);
                if (!historyResult.Ok)
                {
                    return Result<CompactResult, CompactionError>.Failure(historyResult.Error!);
                }

                historyText = historyResult.Value!.Text;
                historyUsage = historyResult.Value.Usage;
            }

            var prefixResult = await GenerateTurnPrefixSummaryAsync(
                preparation.TurnPrefixMessages,
                models,
                model,
                preparation.Settings.ReserveTokens,
                thinkingLevel,
                retry,
                callbacks,
                cancellationToken).ConfigureAwait(false);
            if (!prefixResult.Ok)
            {
                return Result<CompactResult, CompactionError>.Failure(prefixResult.Error!);
            }

            summary = $"{historyText}\n\n---\n\n**Turn Context (split turn):**\n\n{prefixResult.Value!.Text}";
            summaryUsage = historyUsage is null
                ? prefixResult.Value.Usage
                : CombineUsage(historyUsage, prefixResult.Value.Usage);
        }
        else
        {
            var summaryResult = await GenerateSummaryWithUsageAsync(
                preparation.MessagesToSummarize,
                models,
                model,
                preparation.Settings.ReserveTokens,
                customInstructions,
                preparation.PreviousSummary,
                thinkingLevel,
                retry,
                callbacks,
                cancellationToken).ConfigureAwait(false);
            if (!summaryResult.Ok)
            {
                return Result<CompactResult, CompactionError>.Failure(summaryResult.Error!);
            }

            summary = summaryResult.Value!.Text;
            summaryUsage = summaryResult.Value.Usage;
        }

        var (readFiles, modifiedFiles) = CompactionUtilities.ComputeFileLists(preparation.FileOps);
        summary += CompactionUtilities.FormatFileOperations(readFiles, modifiedFiles);
        return Result<CompactResult, CompactionError>.Success(new CompactResult
        {
            Summary = summary,
            TokensBefore = preparation.TokensBefore,
            Usage = summaryUsage,
            RetainedTail = preparation.RetainedTail,
            Details = new CompactionDetails
            {
                ReadFiles = readFiles,
                ModifiedFiles = modifiedFiles,
            },
        });
    }

    /// <summary>Pascal-case alias for compaction.</summary>
    public static Task<Result<CompactResult, CompactionError>> Compact(
        CompactionPreparation preparation,
        Models models,
        Model model,
        string? customInstructions = null,
        string? thinkingLevel = null,
        RetryPolicy? retry = null,
        RetryCallbacks? callbacks = null,
        CancellationToken cancellationToken = default) =>
        CompactAsync(preparation, models, model, customInstructions, thinkingLevel, retry, callbacks, cancellationToken);

    /// <summary>Re-exports provider-message conversion for summary callers.</summary>
    public static IReadOnlyList<Message> ConvertToLlm(IEnumerable<SessionAgentMessage> messages) =>
        HarnessMessageUtilities.ConvertToLlm(messages);

    /// <summary>Re-exports conversation serialization for summary callers.</summary>
    public static string SerializeConversation(IReadOnlyList<Message> messages) =>
        CompactionUtilities.SerializeConversation(messages);

    private static List<int> FindValidCutPoints(IReadOnlyList<Entry> entries, int startIndex, int endIndex)
    {
        var cutPoints = new List<int>();
        for (var index = startIndex; index < endIndex; index++)
        {
            var entry = entries[index];
            if (entry is MessageEntry message && message.Message.Role is
                "bashExecution" or "custom" or "branchSummary" or "compactionSummary" or "user" or "assistant")
            {
                cutPoints.Add(index);
            }

            if (entry is BranchSummaryEntry)
            {
                cutPoints.Add(index);
            }
        }

        return cutPoints;
    }

    private static Usage? GetAssistantUsage(SessionAgentMessage message)
    {
        var assistant = HarnessMessageUtilities.TryGetAssistant(message);
        if (assistant is null || assistant.StopReason is StopReasons.Aborted or StopReasons.Error)
        {
            return null;
        }

        return CalculateContextTokens(assistant.Usage) > 0 ? assistant.Usage : null;
    }

    private static (Usage Usage, int Index)? GetLastAssistantUsageInfo(IReadOnlyList<SessionAgentMessage> messages)
    {
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            var usage = GetAssistantUsage(messages[index]);
            if (usage is not null)
            {
                return (usage, index);
            }
        }

        return null;
    }

    private static FileOperations ExtractFileOperations(
        IReadOnlyList<SessionAgentMessage> messages,
        IReadOnlyList<Entry> entries,
        int previousCompactionIndex)
    {
        var fileOps = CompactionUtilities.CreateFileOps();
        if (previousCompactionIndex >= 0 && entries[previousCompactionIndex] is CompactionEntry previousCompaction)
        {
            AddPersistedFileDetails(previousCompaction.Details, fileOps);
        }

        foreach (var message in messages)
        {
            CompactionUtilities.ExtractFileOpsFromMessage(message, fileOps);
        }

        return fileOps;
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

    private static SessionAgentMessage? GetMessageFromEntryForCompaction(Entry entry) =>
        entry is CompactionEntry ? null : GetMessageFromEntry(entry);

    private static SessionAgentMessage? GetMessageFromEntry(Entry entry)
    {
        return entry switch
        {
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

    private static long GetTimestamp(SessionAgentMessage message) =>
        message.Value["timestamp"] is JsonValue value && value.TryGetValue<long>(out var timestamp)
            ? timestamp
            : 0;

    private static async Task<Result<SummaryWithUsage, CompactionError>> GenerateTurnPrefixSummaryAsync(
        IReadOnlyList<SessionAgentMessage> messages,
        Models models,
        Model model,
        int reserveTokens,
        string? thinkingLevel,
        RetryPolicy? retry,
        RetryCallbacks? callbacks,
        CancellationToken cancellationToken)
    {
        var maxTokens = Math.Min(
            (int)Math.Floor(0.5 * reserveTokens),
            model.MaxTokens > 0 ? model.MaxTokens : int.MaxValue);
        var conversationText = CompactionUtilities.SerializeConversation(HarnessMessageUtilities.ConvertToLlm(messages));
        var promptText = $"<conversation>\n{conversationText}\n</conversation>\n\n{_turnPrefixSummarizationPrompt}";
        var response = await CompleteSimpleWithRetriesAsync(
            models,
            model,
            new Context
            {
                SystemPrompt = SummarizationSystemPrompt,
                Messages = [UserMessage.Text(promptText, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())],
            },
            new ModelsSimpleStreamOptions
            {
                MaxTokens = maxTokens,
                Signal = cancellationToken,
                Reasoning = model.Reasoning && !string.IsNullOrEmpty(thinkingLevel) && thinkingLevel != ThinkingLevels.Off
                    ? thinkingLevel
                    : null,
            },
            retry,
            callbacks,
            cancellationToken).ConfigureAwait(false);

        if (response.StopReason == StopReasons.Aborted)
        {
            return Result<SummaryWithUsage, CompactionError>.Failure(
                new CompactionError(CompactionErrorCodes.Aborted, response.ErrorMessage ?? "Turn prefix summarization aborted"));
        }

        if (response.StopReason == StopReasons.Error)
        {
            return Result<SummaryWithUsage, CompactionError>.Failure(
                new CompactionError(
                    CompactionErrorCodes.SummarizationFailed,
                    $"Turn prefix summarization failed: {response.ErrorMessage ?? "Unknown error"}"));
        }

        return Result<SummaryWithUsage, CompactionError>.Success(new SummaryWithUsage
        {
            Text = HarnessMessageUtilities.ContentText(response),
            Usage = response.Usage,
        });
    }
}

/// <summary>Prepared inputs for a compaction run.</summary>
public sealed record CompactionPreparation
{
    /// <summary>Messages summarized into the history summary.</summary>
    public IReadOnlyList<SessionAgentMessage> MessagesToSummarize { get; init; } = [];

    /// <summary>Prefix messages summarized separately when compaction splits a turn.</summary>
    public IReadOnlyList<SessionAgentMessage> TurnPrefixMessages { get; init; } = [];

    /// <summary>Recent messages retained after compaction.</summary>
    public IReadOnlyList<SessionAgentMessage> RetainedTail { get; init; } = [];

    /// <summary>Whether compaction splits a turn.</summary>
    public bool IsSplitTurn { get; init; }

    /// <summary>Estimated context tokens before compaction.</summary>
    public long TokensBefore { get; init; }

    /// <summary>Previous compaction summary used for iterative updates.</summary>
    public string? PreviousSummary { get; init; }

    /// <summary>File operations extracted from summarized history.</summary>
    public required FileOperations FileOps { get; init; }

    /// <summary>Settings used to prepare compaction.</summary>
    public required CompactionSettings Settings { get; init; }
}

using System.Text.Json.Nodes;
using Pi.AgentCore.Harness;
using Pi.AgentCore.Harness.Compaction;
using Pi.AgentCore.Harness.Session;
using Pi.AgentCore.Harness.Session.Jsonl;
using Pi.Ai;
using Pi.Ai.Testing;
using Xunit;

namespace Pi.AgentCore.Tests.Harness;

public sealed class CompactionTests
{
    [Fact(DisplayName = "calculates total context tokens from usage")]
    public void Calculates_total_context_tokens_from_usage()
    {
        Assert.Equal(1800, Compaction.CalculateContextTokens(HarnessTestHelpers.Usage(1000, 500, 200, 100)));
        Assert.Equal(0, Compaction.CalculateContextTokens(HarnessTestHelpers.Usage(0, 0, 0, 0)));
    }

    [Fact(DisplayName = "checks compaction threshold")]
    public void Checks_compaction_threshold()
    {
        var settings = new CompactionSettings
        {
            Enabled = true,
            ReserveTokens = 10000,
            KeepRecentTokens = 20000,
        };

        Assert.True(Compaction.ShouldCompact(95000, 100000, settings));
        Assert.False(Compaction.ShouldCompact(89000, 100000, settings));
        Assert.False(Compaction.ShouldCompact(95000, 100000, settings with { Enabled = false }));
    }

    [Fact(DisplayName = "finds a cut point based on token differences")]
    public void Finds_a_cut_point_based_on_token_differences()
    {
        var entries = new List<Entry>();
        string? parentId = null;
        for (var index = 0; index < 10; index++)
        {
            var user = MessageEntryOf($"user-{index}", HarnessTestHelpers.User($"User {index}"), index * 2 + 1, parentId);
            entries.Add(user);
            var assistant = MessageEntryOf(
                $"assistant-{index}",
                HarnessTestHelpers.AssistantText(
                    $"Assistant {index}",
                    HarnessTestHelpers.Usage(0, 100, (index + 1) * 1000)),
                index * 2 + 2,
                user.Id);
            entries.Add(assistant);
            parentId = assistant.Id;
        }

        var result = Compaction.FindCutPoint(entries, 0, entries.Count, 2500);

        Assert.IsType<MessageEntry>(entries[result.FirstKeptEntryIndex]);
    }

    [Fact(DisplayName = "covers cut-point and turn-start edge cases")]
    public void Covers_cut_point_and_turn_start_edge_cases()
    {
        var thinking = new ThinkingLevelEntry
        {
            Id = "thinking",
            ParentId = null,
            Seq = 1,
            Timestamp = 1,
            ThinkingLevel = ThinkingLevels.High,
        };
        var modelChange = new ModelChangeEntry
        {
            Id = "model",
            ParentId = thinking.Id,
            Seq = 2,
            Timestamp = 2,
            Provider = "openai",
            ModelId = "gpt-4",
        };

        Assert.Equal(
            new Compaction.CutPointResult { FirstKeptEntryIndex = 0, TurnStartIndex = -1, IsSplitTurn = false },
            Compaction.FindCutPoint([thinking, modelChange], 0, 2, 1));

        var branchSummary = new BranchSummaryEntry
        {
            Id = "branch-summary",
            ParentId = modelChange.Id,
            Seq = 3,
            Timestamp = 3,
            FromId = "branch",
            Summary = "branch summary",
        };
        Assert.Equal(1, Compaction.FindTurnStartIndex([thinking, branchSummary], 1, 0));
        Assert.Equal(-1, Compaction.FindTurnStartIndex([thinking, modelChange], 1, 0));
        Assert.Equal(0, Compaction.FindCutPoint([thinking, branchSummary], 0, 2, 1).FirstKeptEntryIndex);

        var toolResult = MessageEntryOf("tool-result", HarnessTestHelpers.ToolResult(timestamp: 4), 1);
        Assert.Equal(
            new Compaction.CutPointResult { FirstKeptEntryIndex = 0, TurnStartIndex = -1, IsSplitTurn = false },
            Compaction.FindCutPoint([toolResult], 0, 1, 1));

        var user = MessageEntryOf("user", HarnessTestHelpers.User("user"), 1);
        var compaction = CreateCompaction("summary", user.Id, 2);
        var assistant = MessageEntryOf("assistant", HarnessTestHelpers.AssistantText("assistant"), 3, compaction.Id);
        Assert.Equal(2, Compaction.FindCutPoint([user, compaction, assistant], 0, 3, 1).FirstKeptEntryIndex);
    }

    [Fact(DisplayName = "estimates tokens and context usage across supported message roles")]
    public void Estimates_tokens_and_context_usage_across_supported_message_roles()
    {
        var usage = HarnessTestHelpers.Usage(10, 5, 3, 2);
        var assistant = HarnessTestHelpers.AssistantText("assistant", usage);
        var assistantWithThinkingAndTool = HarnessTestHelpers.Assistant(
            [
                new ThinkingContent("thinking"),
                new ToolCall("call-1", "read", new JsonObject { ["path"] = "file.ts" }),
            ],
            stopReason: StopReasons.ToolUse,
            usage: usage);
        var custom = new CustomMessage<object>
        {
            CustomType = "note",
            Content = "custom text",
            Display = true,
            Timestamp = 1,
        }.ToAgentMessage();
        var toolResultWithImage = new AgentMessage(new ToolResultMessage
        {
            ToolCallId = "call-1",
            ToolName = "read",
            Content = [new TextContent("tool text"), new ImageContent("abc", "image/png")],
            IsError = false,
            Timestamp = 1,
        });
        var bashExecution = new BashExecutionMessage
        {
            Command = "npm run check",
            Output = "ok",
            ExitCode = 0,
            Timestamp = 1,
        }.ToAgentMessage();
        var branchSummary = HarnessMessageUtilities.CreateBranchSummaryMessage("branch", "x", 1).ToAgentMessage();
        var compactionSummary = HarnessMessageUtilities.CreateCompactionSummaryMessage("compact", 123, 1).ToAgentMessage();
        var unknown = new AgentMessage(new JsonObject { ["role"] = "unknown", ["timestamp"] = 1 });

        Assert.True(Compaction.EstimateTokens(HarnessTestHelpers.UserPlain("plain user")) > 0);
        Assert.True(Compaction.EstimateTokens(assistantWithThinkingAndTool) > 0);
        Assert.True(Compaction.EstimateTokens(custom) > 0);
        Assert.True(Compaction.EstimateTokens(toolResultWithImage) > 1000);
        Assert.True(Compaction.EstimateTokens(bashExecution) > 0);
        Assert.True(Compaction.EstimateTokens(branchSummary) > 0);
        Assert.True(Compaction.EstimateTokens(compactionSummary) > 0);
        Assert.Equal(0, Compaction.EstimateTokens(unknown));

        var lastUsage = Compaction.GetLastAssistantUsage(
            [
                MessageEntryOf("user", HarnessTestHelpers.User("user"), 1),
                MessageEntryOf("assistant", assistant, 2),
            ]);
        Assert.NotNull(lastUsage);
        Assert.Equal(usage, lastUsage);

        Assert.Null(Compaction.GetLastAssistantUsage(
            [
                MessageEntryOf("aborted", HarnessTestHelpers.Assistant([], stopReason: StopReasons.Aborted), 1),
                MessageEntryOf("error", HarnessTestHelpers.Assistant([], stopReason: StopReasons.Error), 2),
            ]));

        var lastValid = Compaction.GetLastAssistantUsage(
            [
                MessageEntryOf("user", HarnessTestHelpers.User("user"), 1),
                MessageEntryOf("assistant", assistant, 2),
                MessageEntryOf("partial", HarnessTestHelpers.AssistantText("partial", HarnessTestHelpers.Usage(0, 0)), 3),
            ]);
        Assert.Equal(usage, lastValid);

        var noUsage = Compaction.EstimateContextTokens([HarnessTestHelpers.User("no usage")]);
        Assert.Null(noUsage.LastUsageIndex);

        var assistantWithTail = Compaction.EstimateContextTokens([assistant, HarnessTestHelpers.User("tail")]);
        Assert.Equal(20, assistantWithTail.UsageTokens);
        Assert.Equal(0, assistantWithTail.LastUsageIndex);

        var estimate = Compaction.EstimateContextTokens(
            [
                HarnessTestHelpers.User("Hello"),
                assistant,
                HarnessTestHelpers.User("continue"),
                HarnessTestHelpers.AssistantText("Partial thinking", HarnessTestHelpers.Usage(0, 0)),
            ]);
        Assert.Equal(20, estimate.UsageTokens);
        Assert.Equal(1, estimate.LastUsageIndex);
        Assert.True(estimate.TrailingTokens > 0);
        Assert.Equal(20 + estimate.TrailingTokens, estimate.Tokens);
    }

    [Fact(DisplayName = "builds session context with a compaction entry")]
    public void Builds_session_context_with_a_compaction_entry()
    {
        var u1 = MessageEntryOf("u1", HarnessTestHelpers.User("1"), 1);
        var a1 = MessageEntryOf("a1", HarnessTestHelpers.AssistantText("a"), 2, u1.Id);
        var u2 = MessageEntryOf("u2", HarnessTestHelpers.User("2"), 3, a1.Id);
        var a2 = MessageEntryOf("a2", HarnessTestHelpers.AssistantText("b"), 4, u2.Id);
        var compaction = CreateCompaction(
            "Summary of 1,a,2,b",
            a2.Id,
            5,
            [HarnessTestHelpers.User("2"), HarnessTestHelpers.AssistantText("b")]);
        var u3 = MessageEntryOf("u3", HarnessTestHelpers.User("3"), 6, compaction.Id);
        var a3 = MessageEntryOf("a3", HarnessTestHelpers.AssistantText("c"), 7, u3.Id);

        var loaded = SessionContextBuilder.BuildSessionContext([u1, a1, u2, a2, compaction, u3, a3]);

        Assert.Equal(5, loaded.Messages.Count);
        Assert.Equal("compactionSummary", loaded.Messages[0].Role);
        Assert.Equal(["compactionSummary", "user", "assistant", "user", "assistant"], loaded.Messages.Select(m => m.Role));
    }

    [Fact(DisplayName = "tracks model and thinking level changes in built context")]
    public void Tracks_model_and_thinking_level_changes_in_built_context()
    {
        var user = MessageEntryOf("user", HarnessTestHelpers.User("1"), 1);
        var modelChange = new ModelChangeEntry
        {
            Id = "model",
            ParentId = user.Id,
            Seq = 2,
            Timestamp = 2,
            Provider = "openai",
            ModelId = "gpt-4",
        };
        var assistant = MessageEntryOf(
            "assistant",
            new AgentMessage(new AssistantMessage
            {
                Content = [new TextContent("a")],
                Api = "anthropic-messages",
                Provider = "anthropic",
                Model = "claude-sonnet-4-5",
                Usage = HarnessTestHelpers.Usage(100, 50),
                StopReason = StopReasons.Stop,
                Timestamp = 3,
            }),
            3,
            modelChange.Id);
        var thinkingChange = new ThinkingLevelEntry
        {
            Id = "thinking",
            ParentId = assistant.Id,
            Seq = 4,
            Timestamp = 4,
            ThinkingLevel = ThinkingLevels.High,
        };

        var loaded = SessionContextBuilder.BuildSessionContext([user, modelChange, assistant, thinkingChange]);

        Assert.Equal(new SessionModel { Provider = "anthropic", ModelId = "claude-sonnet-4-5" }, loaded.Model);
        Assert.Equal(ThinkingLevels.High, loaded.ThinkingLevel);
    }

    [Fact(DisplayName = "prepares compaction using the latest compaction summary as previousSummary")]
    public void Prepares_compaction_using_the_latest_compaction_summary_as_previous_summary()
    {
        var u1 = MessageEntryOf("u1", HarnessTestHelpers.User("user msg 1"), 1);
        var a1 = MessageEntryOf("a1", HarnessTestHelpers.AssistantText("assistant msg 1"), 2, u1.Id);
        var u2 = MessageEntryOf("u2", HarnessTestHelpers.User("user msg 2"), 3, a1.Id);
        var a2 = MessageEntryOf("a2", HarnessTestHelpers.AssistantText("assistant msg 2", HarnessTestHelpers.Usage(5000, 1000)), 4, u2.Id);
        var compaction = CreateCompaction("First summary", a2.Id, 5);
        var u3 = MessageEntryOf("u3", HarnessTestHelpers.User("user msg 3"), 6, compaction.Id);
        var a3 = MessageEntryOf("a3", HarnessTestHelpers.AssistantText("assistant msg 3", HarnessTestHelpers.Usage(8000, 2000)), 7, u3.Id);
        var entries = new Entry[] { u1, a1, u2, a2, compaction, u3, a3 };

        var preparation = Result.GetOrThrow(Compaction.PrepareCompaction(entries, Compaction.DefaultCompactionSettings));
        Assert.NotNull(preparation);
        Assert.Equal("First summary", preparation!.PreviousSummary);
        Assert.NotEmpty(preparation.RetainedTail);
        Assert.Equal(
            Compaction.EstimateContextTokens(SessionContextBuilder.BuildSessionContext(entries).Messages).Tokens,
            preparation.TokensBefore);
    }

    [Fact(DisplayName = "carries a previous compaction's retained tail into the next preparation")]
    public void Carries_a_previous_compactions_retained_tail_into_the_next_preparation()
    {
        var retainedUser = HarnessTestHelpers.User("retained user");
        var retainedAssistant = HarnessTestHelpers.AssistantText("retained assistant");
        var compaction = CreateCompaction("previous summary", null, 1, [retainedUser, retainedAssistant]);
        var user = MessageEntryOf("new-user", HarnessTestHelpers.User("new user"), 2, compaction.Id);
        var assistant = MessageEntryOf("new-assistant", HarnessTestHelpers.AssistantText("new assistant"), 3, user.Id);

        var preparation = Result.GetOrThrow(Compaction.PrepareCompaction(
            [compaction, user, assistant],
            new CompactionSettings { Enabled = true, ReserveTokens = 100, KeepRecentTokens = 1 }));

        Assert.NotNull(preparation);
        Assert.Equal("previous summary", preparation!.PreviousSummary);
        var aggregate = preparation.MessagesToSummarize
            .Concat(preparation.TurnPrefixMessages)
            .Concat(preparation.RetainedTail)
            .Select(message => message.Value.ToJsonString())
            .ToArray();
        Assert.Equal(
            new[] { retainedUser, retainedAssistant, user.Message, assistant.Message }
                .Select(message => message.Value.ToJsonString()),
            aggregate);
    }

    [Fact(DisplayName = "prepares split-turn compaction with prior file-operation details")]
    public void Prepares_split_turn_compaction_with_prior_file_operation_details()
    {
        var u1 = MessageEntryOf("u1", HarnessTestHelpers.User("user msg 1"), 1);
        var assistantMessage = HarnessTestHelpers.Assistant(
            [new ToolCall("tool-1", "write", new JsonObject { ["path"] = "written.ts" })],
            stopReason: StopReasons.ToolUse);
        var a1 = MessageEntryOf("a1", assistantMessage, 2, u1.Id);
        var compaction = CreateCompaction("First summary", a1.Id, 3) with
        {
            Details = new JsonObject
            {
                ["readFiles"] = new JsonArray { JsonValue.Create("old-read.ts") },
                ["modifiedFiles"] = new JsonArray { JsonValue.Create("old-edit.ts"), JsonValue.Create("written.ts") },
            },
        };
        var u2 = MessageEntryOf("u2", HarnessTestHelpers.User("large turn"), 4, compaction.Id);
        var a2 = MessageEntryOf("a2", HarnessTestHelpers.AssistantText("large assistant message"), 5, u2.Id);

        var preparation = Result.GetOrThrow(Compaction.PrepareCompaction(
            [u1, a1, compaction, u2, a2],
            new CompactionSettings { Enabled = true, ReserveTokens = 100, KeepRecentTokens = 1 }));

        Assert.NotNull(preparation);
        Assert.Equal("First summary", preparation!.PreviousSummary);
        Assert.True(preparation.IsSplitTurn);
        Assert.Equal(["user"], preparation.TurnPrefixMessages.Select(message => message.Role));
        Assert.Contains("old-read.ts", preparation.FileOps.Read);
        Assert.Contains("old-edit.ts", preparation.FileOps.Edited);
        Assert.Contains("written.ts", preparation.FileOps.Edited);
    }

    [Fact(DisplayName = "does not prepare compaction when there is nothing valid to compact")]
    public void Does_not_prepare_compaction_when_there_is_nothing_valid_to_compact()
    {
        var compaction = CreateCompaction("already compacted", null, 1);

        Assert.Null(Result.GetOrThrow(Compaction.PrepareCompaction([compaction], Compaction.DefaultCompactionSettings)));
        Assert.Null(Result.GetOrThrow(Compaction.PrepareCompaction([], Compaction.DefaultCompactionSettings)));
    }

    [Fact(DisplayName = "serializes conversation with truncated tool results")]
    public void Serializes_conversation_with_truncated_tool_results()
    {
        var result = Compaction.SerializeConversation(
        [
            new ToolResultMessage
            {
                ToolCallId = "tc1",
                ToolName = "read",
                Content = [new TextContent(new string('x', 5000))],
                IsError = false,
                Timestamp = 1,
            },
        ]);

        Assert.Contains("[Tool result]:", result);
        Assert.Contains("[... 3000 more characters truncated]", result);
    }

    [Fact(DisplayName = "passes reasoning through generateSummary only for reasoning models with thinking enabled")]
    public async Task Passes_reasoning_through_generate_summary_only_for_reasoning_models_with_thinking_enabled()
    {
        var messages = new[] { HarnessTestHelpers.User("Summarize this.") };
        var seenOptions = new List<SimpleStreamOptions?>();

        using (var reasoning = HarnessTestHelpers.CreateFauxModel(true))
        {
            reasoning.Registration.SetResponses(
            [
                FauxResponseStep.FromFactory((_, options, _, _) =>
                {
                    seenOptions.Add(options);
                    return Task.FromResult(FauxMessages.FauxAssistantMessage("## Goal\nTest summary"));
                }),
            ]);
            Assert.True((await Compaction.GenerateSummaryAsync(
                messages,
                reasoning.Models,
                reasoning.Model,
                2000,
                thinkingLevel: ThinkingLevels.Medium,
                cancellationToken: TestContext.Current.CancellationToken)).Ok);
        }

        using (var off = HarnessTestHelpers.CreateFauxModel(true))
        {
            off.Registration.SetResponses(
            [
                FauxResponseStep.FromFactory((_, options, _, _) =>
                {
                    seenOptions.Add(options);
                    return Task.FromResult(FauxMessages.FauxAssistantMessage("## Goal\nTest summary"));
                }),
            ]);
            Assert.True((await Compaction.GenerateSummaryAsync(
                messages,
                off.Models,
                off.Model,
                2000,
                thinkingLevel: ThinkingLevels.Off,
                cancellationToken: TestContext.Current.CancellationToken)).Ok);
        }

        using (var nonReasoning = HarnessTestHelpers.CreateFauxModel(false))
        {
            nonReasoning.Registration.SetResponses(
            [
                FauxResponseStep.FromFactory((_, options, _, _) =>
                {
                    seenOptions.Add(options);
                    return Task.FromResult(FauxMessages.FauxAssistantMessage("## Goal\nTest summary"));
                }),
            ]);
            Assert.True((await Compaction.GenerateSummaryAsync(
                messages,
                nonReasoning.Models,
                nonReasoning.Model,
                2000,
                thinkingLevel: ThinkingLevels.Medium,
                cancellationToken: TestContext.Current.CancellationToken)).Ok);
        }

        Assert.Equal(ThinkingLevels.Medium, seenOptions[0]!.Reasoning);
        Assert.Null(seenOptions[1]!.Reasoning);
        Assert.Null(seenOptions[2]!.Reasoning);
    }

    [Fact(DisplayName = "includes previous summaries and custom instructions in generateSummary prompts")]
    public async Task Includes_previous_summaries_and_custom_instructions_in_generate_summary_prompts()
    {
        var messages = new[] { HarnessTestHelpers.User("Summarize this.") };
        var promptText = string.Empty;
        using var faux = HarnessTestHelpers.CreateFauxModel(false);
        faux.Registration.SetResponses(
        [
            FauxResponseStep.FromFactory((context, _, _, _) =>
            {
                var message = Assert.IsType<UserMessage>(context.Messages[0]);
                promptText = HarnessMessageUtilities.ContentText(message);
                return Task.FromResult(FauxMessages.FauxAssistantMessage("## Goal\nTest summary"));
            }),
        ]);

        var result = await Compaction.GenerateSummaryWithUsageAsync(
            messages,
            faux.Models,
            faux.Model,
            2000,
            customInstructions: "focus",
            previousSummary: "old summary",
            cancellationToken: TestContext.Current.CancellationToken);

        var summary = Result.GetOrThrow(result);
        Assert.Contains("Test summary", summary.Text);
        Assert.True(summary.Usage.Input > 0);
        Assert.True(summary.Usage.Output > 0);
        Assert.Equal(
            summary.Usage.Input + summary.Usage.Output + summary.Usage.CacheRead + summary.Usage.CacheWrite,
            summary.Usage.TotalTokens);
        Assert.Contains("<previous-summary>\nold summary\n</previous-summary>", promptText);
        Assert.Contains("Additional focus: focus", promptText);
    }

    [Fact(DisplayName = "preserves the string result from generateSummary")]
    public async Task Preserves_the_string_result_from_generate_summary()
    {
        using var faux = HarnessTestHelpers.CreateFauxModel(false);
        faux.Registration.SetResponses(
        [FauxResponseStep.FromMessage(FauxMessages.FauxAssistantMessage("## Goal\nTest summary"))]);

        var result = await Compaction.GenerateSummaryAsync(
            [HarnessTestHelpers.User("Summarize this.")],
            faux.Models,
            faux.Model,
            2000,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("## Goal\nTest summary", Result.GetOrThrow(result));
    }

    [Fact(DisplayName = "returns error results for failed or aborted summary generations")]
    public async Task Returns_error_results_for_failed_or_aborted_summary_generations()
    {
        var messages = new[] { HarnessTestHelpers.User("Summarize this.") };
        using var error = HarnessTestHelpers.CreateFauxModel(false);
        error.Registration.SetResponses(
        [FauxResponseStep.FromMessage(FauxMessages.FauxAssistantMessage(
            string.Empty,
            stopReason: StopReasons.Error,
            errorMessage: "boom"))]);
        var errorResult = await Compaction.GenerateSummaryAsync(
            messages,
            error.Models,
            error.Model,
            2000,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(errorResult.Ok);
        Assert.Equal(CompactionErrorCodes.SummarizationFailed, errorResult.Error!.Code);
        Assert.Equal("Summarization failed: boom", errorResult.Error.Message);

        using var aborted = HarnessTestHelpers.CreateFauxModel(false);
        aborted.Registration.SetResponses(
        [FauxResponseStep.FromMessage(FauxMessages.FauxAssistantMessage(
            string.Empty,
            stopReason: StopReasons.Aborted,
            errorMessage: "stopped"))]);
        var abortedResult = await Compaction.GenerateSummaryAsync(
            messages,
            aborted.Models,
            aborted.Model,
            2000,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(abortedResult.Ok);
        Assert.Equal(CompactionErrorCodes.Aborted, abortedResult.Error!.Code);
        Assert.Equal("stopped", abortedResult.Error.Message);
    }

    [Fact(DisplayName = "clamps compaction summary maxTokens to the model output cap")]
    public async Task Clamps_compaction_summary_max_tokens_to_the_model_output_cap()
    {
        var seenOptions = new List<SimpleStreamOptions?>();
        using var faux = HarnessTestHelpers.CreateFauxModel(false, 128000);
        faux.Registration.SetResponses(
        [
            FauxResponseStep.FromFactory((_, options, _, _) =>
            {
                seenOptions.Add(options);
                return Task.FromResult(FauxMessages.FauxAssistantMessage("## Goal\nTest summary"));
            }),
            FauxResponseStep.FromFactory((_, options, _, _) =>
            {
                seenOptions.Add(options);
                return Task.FromResult(FauxMessages.FauxAssistantMessage("## Goal\nTest summary"));
            }),
        ]);
        var messages = new[] { HarnessTestHelpers.User("Summarize this.") };
        var preparation = new CompactionPreparation
        {
            MessagesToSummarize = messages,
            TurnPrefixMessages = messages,
            RetainedTail = messages,
            IsSplitTurn = true,
            TokensBefore = 600000,
            FileOps = CompactionUtilities.CreateFileOps(),
            Settings = new CompactionSettings { Enabled = true, ReserveTokens = 500000, KeepRecentTokens = 20000 },
        };

        Result.GetOrThrow(await Compaction.CompactAsync(
            preparation,
            faux.Models,
            faux.Model,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal([128000, 128000], seenOptions.Select(options => options!.MaxTokens));
        Assert.Equal([CacheRetentions.None, CacheRetentions.None], seenOptions.Select(options => options!.CacheRetention));
        Assert.NotEqual(seenOptions[0]!.SessionId, seenOptions[1]!.SessionId);
    }

    [Fact(DisplayName = "returns compaction error results without throwing")]
    public async Task Returns_compaction_error_results_without_throwing()
    {
        var messages = new[] { HarnessTestHelpers.User("Summarize this.") };
        var preparation = new CompactionPreparation
        {
            MessagesToSummarize = messages,
            RetainedTail = messages,
            TokensBefore = 100,
            FileOps = CompactionUtilities.CreateFileOps(),
            Settings = new CompactionSettings { Enabled = true, ReserveTokens = 2000, KeepRecentTokens = 20 },
        };
        using var faux = HarnessTestHelpers.CreateFauxModel(false);
        faux.Registration.SetResponses(
        [FauxResponseStep.FromMessage(FauxMessages.FauxAssistantMessage(
            string.Empty,
            stopReason: StopReasons.Error,
            errorMessage: "history failed"))]);

        var result = await Compaction.CompactAsync(
            preparation,
            faux.Models,
            faux.Model,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal(CompactionErrorCodes.SummarizationFailed, result.Error!.Code);
        Assert.Equal("Summarization failed: history failed", result.Error.Message);
    }

    [Fact(DisplayName = "combines usage for split-turn compaction summaries")]
    public async Task Combines_usage_for_split_turn_compaction_summaries()
    {
        var messages = new[] { HarnessTestHelpers.User("Summarize this.") };
        var historyUsage = HarnessTestHelpers.Usage(1, 2, 3, 4);
        var prefixUsage = HarnessTestHelpers.Usage(5, 6, 7, 8);
        var scripted = HarnessTestHelpers.CreateScriptedModel(
            [
                HarnessTestHelpers.AssistantWithUsage("history summary", historyUsage),
                HarnessTestHelpers.AssistantWithUsage("turn prefix summary", prefixUsage),
            ]);
        var preparation = new CompactionPreparation
        {
            MessagesToSummarize = messages,
            TurnPrefixMessages = messages,
            RetainedTail = messages,
            IsSplitTurn = true,
            TokensBefore = 100,
            FileOps = CompactionUtilities.CreateFileOps(),
            Settings = new CompactionSettings { Enabled = true, ReserveTokens = 2000, KeepRecentTokens = 20 },
        };

        var result = Result.GetOrThrow(await Compaction.CompactAsync(
            preparation,
            scripted.Models,
            scripted.Model,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(HarnessTestHelpers.Usage(6, 8, 10, 12), result.Usage);
    }

    [Fact(DisplayName = "passes reasoning through turn-prefix summaries when enabled")]
    public async Task Passes_reasoning_through_turn_prefix_summaries_when_enabled()
    {
        var messages = new[] { HarnessTestHelpers.User("Summarize this.") };
        var seenOptions = new List<SimpleStreamOptions?>();
        using var faux = HarnessTestHelpers.CreateFauxModel(true);
        faux.Registration.SetResponses(
        [
            FauxResponseStep.FromFactory((_, options, _, _) =>
            {
                seenOptions.Add(options);
                return Task.FromResult(FauxMessages.FauxAssistantMessage("## Original Request\nTest summary"));
            }),
        ]);
        var preparation = new CompactionPreparation
        {
            MessagesToSummarize = [],
            TurnPrefixMessages = messages,
            RetainedTail = messages,
            IsSplitTurn = true,
            TokensBefore = 100,
            FileOps = CompactionUtilities.CreateFileOps(),
            Settings = new CompactionSettings { Enabled = true, ReserveTokens = 2000, KeepRecentTokens = 20 },
        };

        Result.GetOrThrow(await Compaction.CompactAsync(
            preparation,
            faux.Models,
            faux.Model,
            thinkingLevel: ThinkingLevels.High,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ThinkingLevels.High, seenOptions[0]!.Reasoning);
    }

    [Fact(DisplayName = "returns turn-prefix compaction errors without throwing")]
    public async Task Returns_turn_prefix_compaction_errors_without_throwing()
    {
        var messages = new[] { HarnessTestHelpers.User("Summarize this.") };
        var preparation = new CompactionPreparation
        {
            TurnPrefixMessages = messages,
            RetainedTail = messages,
            IsSplitTurn = true,
            TokensBefore = 100,
            FileOps = CompactionUtilities.CreateFileOps(),
            Settings = new CompactionSettings { Enabled = true, ReserveTokens = 2000, KeepRecentTokens = 20 },
        };
        using var error = HarnessTestHelpers.CreateFauxModel(false);
        error.Registration.SetResponses(
        [FauxResponseStep.FromMessage(FauxMessages.FauxAssistantMessage(
            string.Empty,
            stopReason: StopReasons.Error,
            errorMessage: "prefix failed"))]);
        var errorResult = await Compaction.CompactAsync(
            preparation,
            error.Models,
            error.Model,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(errorResult.Ok);
        Assert.Equal(CompactionErrorCodes.SummarizationFailed, errorResult.Error!.Code);
        Assert.Equal("Turn prefix summarization failed: prefix failed", errorResult.Error.Message);

        using var aborted = HarnessTestHelpers.CreateFauxModel(false);
        aborted.Registration.SetResponses(
        [FauxResponseStep.FromMessage(FauxMessages.FauxAssistantMessage(
            string.Empty,
            stopReason: StopReasons.Aborted,
            errorMessage: "prefix stopped"))]);
        var abortedResult = await Compaction.CompactAsync(
            preparation,
            aborted.Models,
            aborted.Model,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(abortedResult.Ok);
        Assert.Equal(CompactionErrorCodes.Aborted, abortedResult.Error!.Code);
        Assert.Equal("prefix stopped", abortedResult.Error.Message);
    }

    [Fact(DisplayName = "returns a compaction result with file details")]
    public async Task Returns_a_compaction_result_with_file_details()
    {
        var u1 = MessageEntryOf("u1", HarnessTestHelpers.User("read a file"), 1);
        var assistant = HarnessTestHelpers.Assistant(
            [new ToolCall("tool-1", "read", new JsonObject { ["path"] = "src/index.ts" })],
            stopReason: StopReasons.ToolUse,
            usage: HarnessTestHelpers.Usage(1000, 200));
        var a1 = MessageEntryOf("a1", assistant, 2, u1.Id);
        var u2 = MessageEntryOf("u2", HarnessTestHelpers.User("continue"), 3, a1.Id);
        var a2 = MessageEntryOf("a2", HarnessTestHelpers.AssistantText("done", HarnessTestHelpers.Usage(4000, 500)), 4, u2.Id);
        var preparation = Result.GetOrThrow(Compaction.PrepareCompaction(
            [u1, a1, u2, a2],
            Compaction.DefaultCompactionSettings));
        Assert.NotNull(preparation);

        using var faux = HarnessTestHelpers.CreateFauxModel(false);
        faux.Registration.SetResponses(
        [FauxResponseStep.FromMessage(FauxMessages.FauxAssistantMessage("## Goal\nTest summary"))]);
        var result = Result.GetOrThrow(await Compaction.CompactAsync(
            preparation!,
            faux.Models,
            faux.Model,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.NotEmpty(result.Summary);
        Assert.True(result.Usage?.TotalTokens > 0);
        Assert.NotEmpty(result.RetainedTail);
        Assert.NotNull(result.Details);
    }

    [Fact(DisplayName = "preserves compaction boundaries, token accounting, and transcript codec round trips")]
    public async Task Preserves_compaction_boundaries_token_accounting_and_transcript_codec_round_trips()
    {
        var first = MessageEntryOf("first", HarnessTestHelpers.User("first"), 1);
        var second = MessageEntryOf("second", HarnessTestHelpers.AssistantText("second"), 2, first.Id);
        var third = MessageEntryOf("third", HarnessTestHelpers.User("third"), 3, second.Id);
        var fourth = MessageEntryOf("fourth", HarnessTestHelpers.AssistantText("fourth"), 4, third.Id);
        var entries = new Entry[] { first, second, third, fourth };
        var settings = new CompactionSettings { Enabled = true, ReserveTokens = 100, KeepRecentTokens = 1 };

        var preparation = Result.GetOrThrow(Compaction.PrepareCompaction(entries, settings));
        Assert.NotNull(preparation);
        var cut = Compaction.FindCutPoint(entries, 0, entries.Length, settings.KeepRecentTokens);
        var expectedFirstRetained = Assert.IsType<MessageEntry>(entries[cut.FirstKeptEntryIndex]).Message.Value.ToJsonString();
        Assert.Equal(expectedFirstRetained, preparation!.RetainedTail[0].Value.ToJsonString());
        Assert.Equal(
            Compaction.EstimateContextTokens(SessionContextBuilder.BuildSessionContext(entries).Messages).Tokens,
            preparation.TokensBefore);

        using var faux = HarnessTestHelpers.CreateFauxModel(false);
        faux.Registration.SetResponses(
        [
            FauxResponseStep.FromMessage(FauxMessages.FauxAssistantMessage("history summary")),
            FauxResponseStep.FromMessage(FauxMessages.FauxAssistantMessage("prefix summary")),
        ]);
        var compacted = Result.GetOrThrow(await Compaction.CompactAsync(
            preparation,
            faux.Models,
            faux.Model,
            cancellationToken: TestContext.Current.CancellationToken));
        var compactionEntry = new CompactionEntry
        {
            Id = "compacted",
            ParentId = fourth.Id,
            Seq = 5,
            Timestamp = 5,
            Summary = compacted.Summary,
            TokensBefore = compacted.TokensBefore,
            RetainedTail = compacted.RetainedTail.ToArray(),
        };
        var mutation = new EntryMutation
        {
            Entry = compactionEntry,
            Lane = "main",
        };
        var line = JsonlCodec.EncodeMutation(mutation);
        var parsed = JsonlCodec.ParseMutation(line);
        Assert.Null(parsed.Error);
        var parsedEntry = Assert.IsType<EntryMutation>(parsed.Value).Entry;
        Assert.Equal(compactionEntry.Id, parsedEntry.Id);
        Assert.Equal(compactionEntry.Summary, ((CompactionEntry)parsedEntry).Summary);
        Assert.Equal(compactionEntry.RetainedTail.Select(message => message.Value.ToJsonString()), ((CompactionEntry)parsedEntry).RetainedTail.Select(message => message.Value.ToJsonString()));
    }

    private static MessageEntry MessageEntryOf(string id, AgentMessage message, long seq, string? parentId = null) => new()
    {
        Id = id,
        ParentId = parentId,
        Seq = seq,
        Timestamp = seq,
        Message = message,
    };

    private static CompactionEntry CreateCompaction(
        string summary,
        string? parentId,
        long seq,
        IReadOnlyList<AgentMessage>? retainedTail = null) => new()
        {
            Id = $"compaction-{seq}",
            ParentId = parentId,
            Seq = seq,
            Timestamp = seq,
            Summary = summary,
            RetainedTail = retainedTail ?? [],
            TokensBefore = 1234,
        };
}

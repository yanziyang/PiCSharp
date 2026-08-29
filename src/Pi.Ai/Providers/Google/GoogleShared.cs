using System.Text.Json.Nodes;

namespace Pi.Ai;

/// <summary>Shared wire conversion and compatibility helpers for Gemini-family APIs.</summary>
public static class GoogleShared
{
    private const string _userImagePlaceholder = "(image omitted: model does not support images)";
    private const string _toolImagePlaceholder = "(tool image omitted: model does not support images)";

    /// <summary>Resolves a Pi thinking level to the Google level used for budgets.</summary>
    public static string ResolveThinkingLevel(Model model, string level)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrEmpty(level);

        if (string.Equals(level, ThinkingLevels.Off, StringComparison.Ordinal))
        {
            return ThinkingLevels.High;
        }

        var mapped = model.ThinkingLevelMap is not null && model.ThinkingLevelMap.TryGetValue(level, out var value)
            ? value
            : null;
        var resolved = mapped?.ToLowerInvariant() ?? level;
        return resolved is ThinkingLevels.Minimal or ThinkingLevels.Low or ThinkingLevels.Medium or ThinkingLevels.High
            ? resolved
            : throw new InvalidOperationException(
                $"Unsupported Google thinking level mapping for {model.Provider}/{model.Id}: {level} -> {mapped ?? "undefined"}");
    }

    /// <summary>Returns whether a Gemini part is a visible reasoning part.</summary>
    public static bool IsThinkingPart(JsonObject part)
    {
        ArgumentNullException.ThrowIfNull(part);
        return GetBool(part["thought"]) == true;
    }

    /// <summary>Retains a non-empty thought signature across streamed deltas.</summary>
    public static string? RetainThoughtSignature(string? existing, string? incoming) =>
        !string.IsNullOrEmpty(incoming) ? incoming : existing;

    /// <summary>Returns whether a model requires explicit Gemini function-call IDs.</summary>
    public static bool RequiresToolCallId(string modelId)
    {
        ArgumentNullException.ThrowIfNull(modelId);
        var major = GetGeminiMajorVersion(modelId);
        return modelId.StartsWith("claude-", StringComparison.Ordinal) ||
               modelId.StartsWith("gpt-oss-", StringComparison.Ordinal) ||
               major is >= 3;
    }

    /// <summary>Returns whether a model accepts images nested in a function response.</summary>
    public static bool SupportsMultimodalFunctionResponse(string modelId)
    {
        ArgumentNullException.ThrowIfNull(modelId);
        var major = GetGeminiMajorVersion(modelId);
        return major is null or >= 3;
    }

    /// <summary>Converts Pi messages to Gemini <c>contents</c> objects.</summary>
    public static JsonArray ConvertMessages(Model model, Context context)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(context);

        var transformed = TransformMessages(model, context.Messages);
        var contents = new JsonArray();
        foreach (var message in transformed)
        {
            switch (message)
            {
                case UserMessage user:
                    AddUserContent(contents, user);
                    break;
                case AssistantMessage assistant:
                    AddAssistantContent(contents, model, assistant);
                    break;
                case ToolResultMessage toolResult:
                    AddToolResultContent(contents, model, toolResult);
                    break;
            }
        }

        return contents;
    }

    /// <summary>Converts Pi tools to Gemini function-declaration groups.</summary>
    public static JsonArray? ConvertTools(
        IReadOnlyList<Tool> tools,
        bool useParameters = false,
        bool supportsStrictMode = true)
    {
        ArgumentNullException.ThrowIfNull(tools);
        if (tools.Count == 0)
        {
            return null;
        }

        var declarations = new JsonArray();
        foreach (var tool in tools)
        {
            var strict = ResolveJsonSchemaStrictSampling(tool, supportsStrictMode);
            var parameters = strict ? MakeStrictJsonSchema(tool.Parameters) : tool.Parameters.DeepClone();
            if (useParameters)
            {
                parameters = SanitizeForOpenApi(parameters);
            }

            declarations.Add((JsonNode?)new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                [useParameters ? "parameters" : "parametersJsonSchema"] = parameters,
            });
        }

        return new JsonArray
        {
            (JsonNode?)new JsonObject { ["functionDeclarations"] = declarations },
        };
    }

    /// <summary>Returns whether Gemini 3 supports validated strict tool sampling.</summary>
    public static bool SupportsGoogleStrictToolSampling(string modelId)
    {
        ArgumentNullException.ThrowIfNull(modelId);
        return GetGeminiMajorVersion(modelId) is >= 3;
    }

    /// <summary>Resolves the Gemini function-calling mode for the available tools.</summary>
    public static string? ResolveGoogleFunctionCallingMode(
        IReadOnlyList<Tool> tools,
        string? toolChoice,
        bool supportsStrictMode)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var strict = tools.Any(tool => ResolveJsonSchemaStrictSampling(tool, supportsStrictMode));
        if (toolChoice is "none" or "any")
        {
            return MapToolChoice(toolChoice);
        }

        if (strict)
        {
            return "VALIDATED";
        }

        return string.IsNullOrEmpty(toolChoice) ? null : MapToolChoice(toolChoice);
    }

    /// <summary>Maps a provider tool-choice value to a Gemini function-calling mode.</summary>
    public static string MapToolChoice(string choice) => choice switch
    {
        "auto" => "AUTO",
        "none" => "NONE",
        "any" => "ANY",
        _ => "AUTO",
    };

    /// <summary>Maps a raw Gemini finish reason to a Pi stop reason.</summary>
    public static string MapStopReason(string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        return reason switch
        {
            "STOP" => StopReasons.Stop,
            "MAX_TOKENS" => StopReasons.Length,
            "BLOCKLIST" or "PROHIBITED_CONTENT" or "SPII" or "SAFETY" or "IMAGE_SAFETY" or
            "IMAGE_PROHIBITED_CONTENT" or "IMAGE_RECITATION" or "IMAGE_OTHER" or "RECITATION" or
            "FINISH_REASON_UNSPECIFIED" or "OTHER" or "LANGUAGE" or "MALFORMED_FUNCTION_CALL" or
            "UNEXPECTED_TOOL_CALL" or "NO_IMAGE" => StopReasons.Error,
            _ => StopReasons.Error,
        };
    }

    /// <summary>Returns the standard Google thinking level for a resolved Pi level.</summary>
    public static string GetThinkingLevel(Model model, string resolvedLevel)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrEmpty(resolvedLevel);

        if (IsGemini3ProModel(model))
        {
            return resolvedLevel is ThinkingLevels.Minimal or ThinkingLevels.Low ? "LOW" : "HIGH";
        }

        if (IsGemma4Model(model))
        {
            return resolvedLevel is ThinkingLevels.Minimal or ThinkingLevels.Low ? "MINIMAL" : "HIGH";
        }

        return resolvedLevel switch
        {
            ThinkingLevels.Minimal => "MINIMAL",
            ThinkingLevels.Low => "LOW",
            ThinkingLevels.Medium => "MEDIUM",
            ThinkingLevels.High => "HIGH",
            _ => "HIGH",
        };
    }

    /// <summary>Returns the model-specific Google token budget for a thinking level.</summary>
    public static int GetGoogleBudget(Model model, string level, ThinkingBudgets? customBudgets = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrEmpty(level);
        var custom = level switch
        {
            ThinkingLevels.Minimal => customBudgets?.Minimal,
            ThinkingLevels.Low => customBudgets?.Low,
            ThinkingLevels.Medium => customBudgets?.Medium,
            ThinkingLevels.High => customBudgets?.High,
            _ => null,
        };
        if (custom is not null)
        {
            return custom.Value;
        }

        if (model.Id.Contains("2.5-pro", StringComparison.Ordinal))
        {
            return level switch
            {
                ThinkingLevels.Minimal => 128,
                ThinkingLevels.Low => 2048,
                ThinkingLevels.Medium => 8192,
                ThinkingLevels.High => 32768,
                _ => -1,
            };
        }

        if (model.Id.Contains("2.5-flash-lite", StringComparison.Ordinal))
        {
            return level switch
            {
                ThinkingLevels.Minimal => 512,
                ThinkingLevels.Low => 2048,
                ThinkingLevels.Medium => 8192,
                ThinkingLevels.High => 24576,
                _ => -1,
            };
        }

        if (model.Id.Contains("2.5-flash", StringComparison.Ordinal))
        {
            return level switch
            {
                ThinkingLevels.Minimal => 128,
                ThinkingLevels.Low => 2048,
                ThinkingLevels.Medium => 8192,
                ThinkingLevels.High => 24576,
                _ => -1,
            };
        }

        return -1;
    }

    /// <summary>Builds the provider's model-specific thinking-disabled configuration.</summary>
    public static JsonObject GetDisabledThinkingConfig(Model model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (IsGemini3ProModel(model))
        {
            return new JsonObject { ["thinkingLevel"] = "LOW" };
        }

        if (IsGemini3FlashModel(model) || IsGemma4Model(model))
        {
            return new JsonObject { ["thinkingLevel"] = "MINIMAL" };
        }

        return new JsonObject { ["thinkingBudget"] = 0 };
    }

    private static List<Message> TransformMessages(Model model, IReadOnlyList<Message> messages)
    {
        var idMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var firstPass = new List<Message>(messages.Count);
        foreach (var message in messages)
        {
            switch (message)
            {
                case UserMessage user:
                    firstPass.Add(DowngradeUserImages(model, user));
                    break;
                case ToolResultMessage toolResult:
                    var transformedToolResult = DowngradeToolImages(model, toolResult);
                    firstPass.Add(idMap.TryGetValue(toolResult.ToolCallId, out var normalized)
                        ? transformedToolResult with { ToolCallId = normalized }
                        : transformedToolResult);
                    break;
                case AssistantMessage assistant:
                    firstPass.Add(TransformAssistant(model, assistant, idMap));
                    break;
            }
        }

        var result = new List<Message>(firstPass.Count);
        var pendingToolCalls = new List<ToolCall>();
        var existingResultIds = new HashSet<string>(StringComparer.Ordinal);
        void InsertSyntheticResults()
        {
            foreach (var toolCall in pendingToolCalls)
            {
                if (!existingResultIds.Contains(toolCall.Id))
                {
                    result.Add(new ToolResultMessage
                    {
                        ToolCallId = toolCall.Id,
                        ToolName = toolCall.Name,
                        Content = [new TextContent("No result provided")],
                        IsError = true,
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    });
                }
            }

            pendingToolCalls.Clear();
            existingResultIds.Clear();
        }

        foreach (var message in firstPass)
        {
            if (message is AssistantMessage assistant)
            {
                InsertSyntheticResults();
                if (assistant.StopReason is StopReasons.Error or StopReasons.Aborted)
                {
                    continue;
                }

                pendingToolCalls.AddRange(assistant.Content.OfType<ToolCall>());
                result.Add(assistant);
            }
            else if (message is ToolResultMessage toolResult)
            {
                existingResultIds.Add(toolResult.ToolCallId);
                result.Add(toolResult);
            }
            else
            {
                InsertSyntheticResults();
                result.Add(message);
            }
        }

        InsertSyntheticResults();
        return result;
    }

    private static Message TransformAssistant(Model model, AssistantMessage message, Dictionary<string, string> idMap)
    {
        var sameModel = string.Equals(message.Provider, model.Provider, StringComparison.Ordinal) &&
                        string.Equals(message.Api, model.Api, StringComparison.Ordinal) &&
                        string.Equals(message.Model, model.Id, StringComparison.Ordinal);
        var blocks = new List<ContentBlock>();
        foreach (var block in message.Content)
        {
            switch (block)
            {
                case ThinkingContent thinking:
                    if (thinking.Redacted == true)
                    {
                        if (sameModel)
                        {
                            blocks.Add(thinking);
                        }

                        break;
                    }

                    if (sameModel && !string.IsNullOrEmpty(thinking.ThinkingSignature))
                    {
                        blocks.Add(thinking);
                    }
                    else if (!string.IsNullOrWhiteSpace(thinking.Thinking))
                    {
                        blocks.Add(sameModel ? thinking : new TextContent(thinking.Thinking));
                    }

                    break;
                case TextContent text:
                    blocks.Add(sameModel ? text : new TextContent(text.Text));
                    break;
                case ToolCall toolCall:
                    var id = toolCall.Id;
                    var signature = toolCall.ThoughtSignature;
                    if (!sameModel && RequiresToolCallId(model.Id))
                    {
                        var normalized = NormalizeToolCallId(id);
                        if (!string.Equals(normalized, id, StringComparison.Ordinal))
                        {
                            idMap[id] = normalized;
                        }

                        id = normalized;
                    }

                    blocks.Add(new ToolCall(
                        id,
                        toolCall.Name,
                        toolCall.Arguments.DeepClone() as JsonObject ?? new JsonObject(),
                        sameModel ? signature : null,
                        toolCall.Namespace));
                    break;
            }
        }

        return message with { Content = blocks };
    }

    private static UserMessage DowngradeUserImages(Model model, UserMessage message)
    {
        if (model.Input.Contains("image", StringComparer.OrdinalIgnoreCase) || message.Content is string)
        {
            return message;
        }

        var blocks = GetContentBlocks(message.Content);
        return blocks is null ? message : message with { Content = ReplaceImages(blocks, _userImagePlaceholder) };
    }

    private static ToolResultMessage DowngradeToolImages(Model model, ToolResultMessage message)
    {
        if (model.Input.Contains("image", StringComparer.OrdinalIgnoreCase))
        {
            return message;
        }

        return message with { Content = ReplaceImages(message.Content, _toolImagePlaceholder) };
    }

    private static List<ContentBlock> ReplaceImages(
        IEnumerable<ContentBlock> content,
        string placeholder)
    {
        var result = new List<ContentBlock>();
        var previousWasPlaceholder = false;
        foreach (var block in content)
        {
            if (block is ImageContent)
            {
                if (!previousWasPlaceholder)
                {
                    result.Add(new TextContent(placeholder));
                }

                previousWasPlaceholder = true;
                continue;
            }

            result.Add(block);
            previousWasPlaceholder = block is TextContent text && string.Equals(text.Text, placeholder, StringComparison.Ordinal);
        }

        return result;
    }

    private static void AddUserContent(JsonArray contents, UserMessage message)
    {
        if (message.Content is string text)
        {
            contents.Add((JsonNode?)new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray
                {
                    (JsonNode?)new JsonObject { ["text"] = UnicodeUtilities.SanitizeSurrogates(text) },
                },
            });
            return;
        }

        var blocks = GetContentBlocks(message.Content);
        if (blocks is null)
        {
            return;
        }

        var parts = new JsonArray();
        foreach (var block in blocks)
        {
            switch (block)
            {
                case TextContent textBlock:
                    parts.Add((JsonNode?)new JsonObject { ["text"] = UnicodeUtilities.SanitizeSurrogates(textBlock.Text) });
                    break;
                case ImageContent image:
                    parts.Add((JsonNode?)new JsonObject
                    {
                        ["inlineData"] = new JsonObject
                        {
                            ["mimeType"] = image.MimeType,
                            ["data"] = image.Data,
                        },
                    });
                    break;
            }
        }

        if (parts.Count > 0)
        {
            contents.Add((JsonNode?)new JsonObject { ["role"] = "user", ["parts"] = parts });
        }
    }

    private static void AddAssistantContent(JsonArray contents, Model model, AssistantMessage message)
    {
        var sameProviderAndModel = string.Equals(message.Provider, model.Provider, StringComparison.Ordinal) &&
                                   string.Equals(message.Model, model.Id, StringComparison.Ordinal);
        var parts = new JsonArray();
        foreach (var block in message.Content)
        {
            switch (block)
            {
                case TextContent text:
                    var textSignature = ResolveThoughtSignature(sameProviderAndModel, text.TextSignature);
                    if (string.IsNullOrWhiteSpace(text.Text) && textSignature is null)
                    {
                        continue;
                    }

                    var textPart = new JsonObject { ["text"] = UnicodeUtilities.SanitizeSurrogates(text.Text) };
                    if (textSignature is not null)
                    {
                        textPart["thoughtSignature"] = textSignature;
                    }

                    parts.Add((JsonNode?)textPart);
                    break;
                case ThinkingContent thinking:
                    if (!sameProviderAndModel && string.IsNullOrWhiteSpace(thinking.Thinking))
                    {
                        continue;
                    }

                    var thinkingText = UnicodeUtilities.SanitizeSurrogates(thinking.Thinking);
                    var thinkingPart = new JsonObject
                    {
                        ["text"] = thinkingText,
                    };
                    if (sameProviderAndModel)
                    {
                        thinkingPart["thought"] = true;
                        var thinkingSignature = ResolveThoughtSignature(true, thinking.ThinkingSignature);
                        if (thinkingSignature is not null)
                        {
                            thinkingPart["thoughtSignature"] = thinkingSignature;
                        }
                    }

                    parts.Add((JsonNode?)thinkingPart);
                    break;
                case ToolCall toolCall:
                    var functionCall = new JsonObject
                    {
                        ["name"] = toolCall.Name,
                        ["args"] = toolCall.Arguments.DeepClone(),
                    };
                    if (RequiresToolCallId(model.Id))
                    {
                        functionCall["id"] = toolCall.Id;
                    }

                    var toolPart = new JsonObject { ["functionCall"] = functionCall };
                    var toolSignature = ResolveThoughtSignature(sameProviderAndModel, toolCall.ThoughtSignature);
                    if (toolSignature is not null)
                    {
                        toolPart["thoughtSignature"] = toolSignature;
                    }

                    parts.Add((JsonNode?)toolPart);
                    break;
            }
        }

        if (parts.Count > 0)
        {
            contents.Add((JsonNode?)new JsonObject { ["role"] = "model", ["parts"] = parts });
        }
    }

    private static void AddToolResultContent(JsonArray contents, Model model, ToolResultMessage message)
    {
        var text = string.Join(
            "\n",
            message.Content.OfType<TextContent>().Select(static value => value.Text));
        var images = model.Input.Contains("image", StringComparer.OrdinalIgnoreCase)
            ? message.Content.OfType<ImageContent>().ToArray()
            : [];
        var hasText = text.Length > 0;
        var hasImages = images.Length > 0;
        var responseValue = hasText
            ? UnicodeUtilities.SanitizeSurrogates(text)
            : hasImages ? "(see attached image)" : string.Empty;
        var functionResponse = new JsonObject
        {
            ["name"] = message.ToolName,
            ["response"] = new JsonObject { [message.IsError ? "error" : "output"] = responseValue },
        };
        var imageParts = new JsonArray();
        foreach (var image in images)
        {
            imageParts.Add((JsonNode?)new JsonObject
            {
                ["inlineData"] = new JsonObject
                {
                    ["mimeType"] = image.MimeType,
                    ["data"] = image.Data,
                },
            });
        }

        if (hasImages && SupportsMultimodalFunctionResponse(model.Id))
        {
            functionResponse["parts"] = imageParts.DeepClone();
        }

        if (RequiresToolCallId(model.Id))
        {
            functionResponse["id"] = message.ToolCallId;
        }

        var responsePart = new JsonObject { ["functionResponse"] = functionResponse };
        var last = contents.LastOrDefault() as JsonObject;
        if (last is not null && StringValue(last["role"]) == "user" &&
            last["parts"] is JsonArray lastParts && lastParts.Any(part => (part as JsonObject)?["functionResponse"] is not null))
        {
            lastParts.Add((JsonNode?)responsePart);
        }
        else
        {
            contents.Add((JsonNode?)new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray((JsonNode?)responsePart),
            });
        }

        if (hasImages && !SupportsMultimodalFunctionResponse(model.Id))
        {
            var separateParts = new JsonArray { (JsonNode?)new JsonObject { ["text"] = "Tool result image:" } };
            foreach (var imagePart in imageParts)
            {
                separateParts.Add(imagePart?.DeepClone());
            }

            contents.Add((JsonNode?)new JsonObject { ["role"] = "user", ["parts"] = separateParts });
        }
    }

    private static bool? GetBool(JsonNode? value)
    {
        try
        {
            return value?.GetValue<bool>();
        }
        catch
        {
            return null;
        }
    }

    private static string? StringValue(JsonNode? value)
    {
        try
        {
            return value?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    private static ContentBlock[]? GetContentBlocks(object? content) => content switch
    {
        IEnumerable<ContentBlock> blocks => blocks.ToArray(),
        _ => null,
    };

    private static string NormalizeToolCallId(string id)
    {
        var normalized = new string(id.Select(static character =>
            char.IsLetterOrDigit(character) || character is '_' or '-' ? character : '_').ToArray());
        return normalized.Length > 64 ? normalized[..64] : normalized;
    }

    private static string? ResolveThoughtSignature(bool sameProviderAndModel, string? signature) =>
        sameProviderAndModel && IsValidThoughtSignature(signature) ? signature : null;

    private static bool IsValidThoughtSignature(string? signature)
    {
        if (string.IsNullOrEmpty(signature) || signature.Length % 4 != 0)
        {
            return false;
        }

        var paddingStarted = false;
        foreach (var character in signature)
        {
            if (character == '=')
            {
                paddingStarted = true;
                continue;
            }

            if (paddingStarted || !(char.IsLetterOrDigit(character) || character is '+' or '/'))
            {
                return false;
            }
        }

        return true;
    }

    private static int? GetGeminiMajorVersion(string modelId)
    {
        var lower = modelId.ToLowerInvariant();
        if (!lower.StartsWith("gemini-", StringComparison.Ordinal))
        {
            return null;
        }

        var remainder = lower[7..];
        if (remainder.StartsWith("live-", StringComparison.Ordinal))
        {
            remainder = remainder[5..];
        }

        var digits = new string(remainder.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var major) ? major : null;
    }

    private static bool IsGemini3ProModel(Model model) =>
        model.Id.Contains("gemini-3", StringComparison.OrdinalIgnoreCase) &&
        model.Id.Contains("-pro", StringComparison.OrdinalIgnoreCase);

    private static bool IsGemini3FlashModel(Model model)
    {
        var id = model.Id.ToLowerInvariant();
        return (id.Contains("gemini-3", StringComparison.Ordinal) && id.Contains("-flash", StringComparison.Ordinal)) ||
               id is "gemini-flash-latest" or "gemini-flash-lite-latest";
    }

    private static bool IsGemma4Model(Model model) =>
        model.Id.Contains("gemma-4", StringComparison.OrdinalIgnoreCase) ||
        model.Id.Contains("gemma4", StringComparison.OrdinalIgnoreCase);

    private static bool ResolveJsonSchemaStrictSampling(Tool tool, bool supportsStrictMode)
    {
        if (tool.ConstrainedSampling is not JsonSchemaSampling config)
        {
            return false;
        }

        if (!supportsStrictMode)
        {
            if (string.Equals(config.Strict, "require", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Tool \"{tool.Name}\" requires JSON-schema constrained sampling, but strict tools are unsupported.");
            }

            return false;
        }

        try
        {
            _ = MakeStrictJsonSchema(tool.Parameters);
            return true;
        }
        catch (UnsupportedStrictSchemaException error)
        {
            if (string.Equals(config.Strict, "require", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Tool \"{tool.Name}\" requires JSON-schema constrained sampling, but {error.Message}.", error);
            }

            return false;
        }
    }

    private static JsonObject MakeStrictJsonSchema(JsonNode schema)
    {
        var clone = schema.DeepClone();
        if (clone is not JsonObject root)
        {
            throw new UnsupportedStrictSchemaException("root schema must have type object");
        }

        MakeStrictJsonSchemaNode(root);
        if (StringValue(root["type"]) != "object")
        {
            throw new UnsupportedStrictSchemaException("root schema must have type object");
        }

        return root;
    }

    private static void MakeStrictJsonSchemaNode(JsonObject schema)
    {
        foreach (var key in new[]
                 {
                     "$ref", "$defs", "definitions", "allOf", "oneOf", "patternProperties",
                     "dependentSchemas", "dependencies", "unevaluatedProperties", "propertyNames",
                     "contains", "prefixItems", "not", "if", "then", "else",
                 })
        {
            if (schema.ContainsKey(key))
            {
                throw new UnsupportedStrictSchemaException($"{key} schemas are unsupported");
            }
        }

        if (schema["anyOf"] is JsonArray anyOf)
        {
            if (anyOf.Count == 0)
            {
                throw new UnsupportedStrictSchemaException("anyOf must contain at least one schema");
            }

            foreach (var variant in anyOf)
            {
                if (variant is not JsonObject variantObject)
                {
                    throw new UnsupportedStrictSchemaException("boolean schemas are unsupported");
                }

                if (IsStructuredSchema(variantObject))
                {
                    throw new UnsupportedStrictSchemaException("object and array unions are unsupported");
                }

                MakeStrictJsonSchemaNode(variantObject);
            }
        }
        else if (schema.ContainsKey("anyOf"))
        {
            throw new UnsupportedStrictSchemaException("anyOf must contain at least one schema");
        }

        if (schema["items"] is JsonArray)
        {
            throw new UnsupportedStrictSchemaException("tuple schemas are unsupported");
        }

        if (schema["items"] is JsonObject items)
        {
            MakeStrictJsonSchemaNode(items);
        }

        var isObject = StringValue(schema["type"]) == "object";
        if (schema.ContainsKey("properties") && !isObject)
        {
            throw new UnsupportedStrictSchemaException("properties require type object");
        }

        if (!isObject)
        {
            return;
        }

        if (schema["additionalProperties"] is JsonNode additional &&
            GetBool(additional) != false)
        {
            throw new UnsupportedStrictSchemaException("schema-valued or true additionalProperties is unsupported");
        }

        if (schema.ContainsKey("properties") && schema["properties"] is not JsonObject)
        {
            throw new UnsupportedStrictSchemaException("object properties must be a schema map");
        }

        var required = new HashSet<string>(StringComparer.Ordinal);
        if (schema["required"] is JsonArray requiredArray)
        {
            foreach (var value in requiredArray)
            {
                var name = StringValue(value);
                if (name is null)
                {
                    throw new UnsupportedStrictSchemaException("object required must be a string array");
                }

                required.Add(name);
            }
        }
        else if (schema.ContainsKey("required"))
        {
            throw new UnsupportedStrictSchemaException("object required must be a string array");
        }

        var properties = schema["properties"] as JsonObject ?? new JsonObject();
        foreach (var requiredName in required)
        {
            if (!properties.ContainsKey(requiredName))
            {
                throw new UnsupportedStrictSchemaException("required contains an unknown property");
            }
        }

        foreach (var (name, property) in properties.ToArray())
        {
            if (property is not JsonObject propertyObject)
            {
                throw new UnsupportedStrictSchemaException("boolean schemas are unsupported");
            }

            MakeStrictJsonSchemaNode(propertyObject);
            if (!required.Contains(name) && !SchemaAllowsNull(propertyObject))
            {
                properties[name] = new JsonObject
                {
                    ["anyOf"] = new JsonArray
                    {
                        propertyObject.DeepClone(),
                        (JsonNode?)new JsonObject { ["type"] = "null" },
                    },
                };
            }
        }

        var requiredNames = new JsonArray();
        foreach (var name in properties.Select(static pair => pair.Key))
        {
            requiredNames.Add((JsonNode?)name);
        }

        schema["required"] = requiredNames;
        schema["additionalProperties"] = false;
    }

    private static bool IsStructuredSchema(JsonObject schema) =>
        StringValue(schema["type"]) is "object" or "array" ||
        schema.ContainsKey("properties") || schema.ContainsKey("items");

    private static bool SchemaAllowsNull(JsonObject schema)
    {
        if (StringValue(schema["type"]) == "null")
        {
            return true;
        }

        if (schema["type"] is JsonArray types && types.Any(value => StringValue(value) == "null"))
        {
            return true;
        }

        if (schema.ContainsKey("const") && schema["const"] is null)
        {
            return true;
        }

        if (schema["enum"] is JsonArray values && values.Any(value => value is null))
        {
            return true;
        }

        return schema["anyOf"] is JsonArray anyOf && anyOf.OfType<JsonObject>().Any(SchemaAllowsNull);
    }

    private static JsonNode SanitizeForOpenApi(JsonNode node)
    {
        if (node is JsonObject value)
        {
            var result = new JsonObject();
            foreach (var (key, child) in value)
            {
                if (key is "$schema" or "$id" or "$anchor" or "$dynamicAnchor" or "$vocabulary" or "$comment" or "$defs" or "definitions")
                {
                    continue;
                }

                result[key] = child is null ? null : SanitizeForOpenApi(child);
            }

            return result;
        }

        if (node is JsonArray array)
        {
            var result = new JsonArray();
            foreach (var child in array)
            {
                result.Add(child is null ? null : SanitizeForOpenApi(child));
            }

            return result;
        }

        return node.DeepClone();
    }

    private sealed class UnsupportedStrictSchemaException(string message) : Exception(message);
}

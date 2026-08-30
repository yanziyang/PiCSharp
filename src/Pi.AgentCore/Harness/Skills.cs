using System.Text;
using Pi.AgentCore.Harness.Session;

namespace Pi.AgentCore.Harness;

/// <summary>Stable warning codes emitted while loading skills.</summary>
public static class SkillDiagnosticCodes
{
    /// <summary>Skill directory metadata could not be read.</summary>
    public const string FileInfoFailed = "file_info_failed";

    /// <summary>Skill directory listing failed.</summary>
    public const string ListFailed = "list_failed";

    /// <summary>Skill file content could not be read.</summary>
    public const string ReadFailed = "read_failed";

    /// <summary>Skill frontmatter could not be parsed.</summary>
    public const string ParseFailed = "parse_failed";

    /// <summary>Skill metadata did not satisfy the declared constraints.</summary>
    public const string InvalidMetadata = "invalid_metadata";
}

/// <summary>Warning produced while loading a skill.</summary>
public sealed record SkillDiagnostic
{
    /// <summary>Diagnostic severity. The loader currently emits warnings only.</summary>
    public string Type { get; init; } = "warning";

    /// <summary>Stable diagnostic code.</summary>
    public required string Code { get; init; }

    /// <summary>Human-readable diagnostic message.</summary>
    public required string Message { get; init; }

    /// <summary>Path associated with the diagnostic.</summary>
    public required string Path { get; init; }
}

/// <summary>Result of loading skills from one or more directories.</summary>
public sealed record SkillLoadResult(
    IReadOnlyList<Skill> Skills,
    IReadOnlyList<SkillDiagnostic> Diagnostics);

/// <summary>Directory input tagged with application-defined provenance.</summary>
public sealed record SourcedSkillInput<TSource>(string Path, TSource Source);

/// <summary>Loaded skill tagged with its source value.</summary>
public sealed record SourcedSkill<TSource, TSkill>(TSkill Skill, TSource Source);

/// <summary>Result of loading source-tagged skills.</summary>
public sealed record SourcedSkillLoadResult<TSource, TSkill>(
    IReadOnlyList<SourcedSkill<TSource, TSkill>> Skills,
    IReadOnlyList<SourcedSkillDiagnostic<TSource>> Diagnostics);

/// <summary>Skill diagnostic tagged with its source value.</summary>
public sealed record SourcedSkillDiagnostic<TSource>(
    string Type,
    string Code,
    string Message,
    string Path,
    TSource Source);

/// <summary>Skill discovery, frontmatter and invocation-prompt helpers.</summary>
public static class SkillLoader
{
    private const int _maxNameLength = 64;
    private const int _maxDescriptionLength = 1024;
    private static readonly string[] _ignoreFileNames = [".gitignore", ".ignore", ".fdignore"];

    /// <summary>Formats a skill invocation prompt and optional additional instructions.</summary>
    public static string FormatSkillInvocation(Skill skill, string? additionalInstructions = null)
    {
        ArgumentNullException.ThrowIfNull(skill);
        var skillBlock =
            $"<skill name=\"{skill.Name}\" location=\"{skill.FilePath}\">\nReferences are relative to {DirnameEnvPath(skill.FilePath)}.\n\n{skill.Content}\n</skill>";
        return string.IsNullOrEmpty(additionalInstructions)
            ? skillBlock
            : $"{skillBlock}\n\n{additionalInstructions}";
    }

    /// <summary>Loads skills from one directory.</summary>
    public static Task<SkillLoadResult> LoadSkillsAsync(
        ExecutionEnv env,
        string dir,
        CancellationToken cancellationToken = default) =>
        LoadSkillsAsync(env, [dir], cancellationToken);

    /// <summary>
    /// Traverses skill directories, honoring .gitignore, .ignore and .fdignore files.
    /// Missing input directories are skipped; malformed declared skills produce diagnostics.
    /// </summary>
    public static async Task<SkillLoadResult> LoadSkillsAsync(
        ExecutionEnv env,
        IReadOnlyList<string> dirs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(dirs);
        var skills = new List<Skill>();
        var diagnostics = new List<SkillDiagnostic>();

        foreach (var dir in dirs)
        {
            var rootInfoResult = await env.FileInfoAsync(dir, cancellationToken).ConfigureAwait(false);
            if (!rootInfoResult.Ok)
            {
                if (rootInfoResult.Error?.Code != FileErrorCodes.NotFound)
                {
                    diagnostics.Add(new SkillDiagnostic
                    {
                        Code = SkillDiagnosticCodes.FileInfoFailed,
                        Message = rootInfoResult.Error?.Message ?? "Unknown filesystem failure.",
                        Path = dir,
                    });
                }

                continue;
            }

            var rootInfo = rootInfoResult.Value!;
            if (await ResolveKindAsync(env, rootInfo, diagnostics, cancellationToken).ConfigureAwait(false) != FileKinds.Directory)
            {
                continue;
            }

            var result = await LoadSkillsFromDirectoryAsync(
                    env,
                    rootInfo.Path,
                    includeRootFiles: true,
                    new GitIgnoreMatcher(),
                    rootInfo.Path,
                    diagnostics,
                    cancellationToken)
                .ConfigureAwait(false);
            skills.AddRange(result.Skills);
            diagnostics.AddRange(result.Diagnostics);
        }

        return new SkillLoadResult(skills, diagnostics);
    }

    /// <summary>Loads source-tagged skills without mapping the skill type.</summary>
    public static Task<SourcedSkillLoadResult<TSource, Skill>> LoadSourcedSkillsAsync<TSource>(
        ExecutionEnv env,
        IReadOnlyList<SourcedSkillInput<TSource>> inputs,
        CancellationToken cancellationToken = default) =>
        LoadSourcedSkillsAsync<TSource, Skill>(env, inputs, mapSkill: null, cancellationToken);

    /// <summary>Loads source-tagged skills and maps each skill to an application type.</summary>
    public static async Task<SourcedSkillLoadResult<TSource, TSkill>> LoadSourcedSkillsAsync<TSource, TSkill>(
        ExecutionEnv env,
        IReadOnlyList<SourcedSkillInput<TSource>> inputs,
        Func<Skill, TSource, TSkill>? mapSkill = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var skills = new List<SourcedSkill<TSource, TSkill>>();
        var diagnostics = new List<SourcedSkillDiagnostic<TSource>>();
        foreach (var input in inputs)
        {
            var result = await LoadSkillsAsync(env, input.Path, cancellationToken).ConfigureAwait(false);
            foreach (var skill in result.Skills)
            {
                var mapped = mapSkill is null ? (TSkill)(object)skill : mapSkill(skill, input.Source);
                skills.Add(new SourcedSkill<TSource, TSkill>(mapped, input.Source));
            }

            diagnostics.AddRange(result.Diagnostics.Select(diagnostic => new SourcedSkillDiagnostic<TSource>(
                diagnostic.Type,
                diagnostic.Code,
                diagnostic.Message,
                diagnostic.Path,
                input.Source)));
        }

        return new SourcedSkillLoadResult<TSource, TSkill>(skills, diagnostics);
    }

    /// <summary>Compatibility name matching the TypeScript module function.</summary>
    public static Task<SkillLoadResult> LoadSkills(
        ExecutionEnv env,
        string dir,
        CancellationToken cancellationToken = default) =>
        LoadSkillsAsync(env, dir, cancellationToken);

    private static async Task<SkillLoadResult> LoadSkillsFromDirectoryAsync(
        ExecutionEnv env,
        string dir,
        bool includeRootFiles,
        GitIgnoreMatcher ignoreMatcher,
        string rootDir,
        List<SkillDiagnostic> parentDiagnostics,
        CancellationToken cancellationToken)
    {
        var skills = new List<Skill>();
        var diagnostics = new List<SkillDiagnostic>();
        var dirInfoResult = await env.FileInfoAsync(dir, cancellationToken).ConfigureAwait(false);
        if (!dirInfoResult.Ok)
        {
            if (dirInfoResult.Error?.Code != FileErrorCodes.NotFound)
            {
                diagnostics.Add(Diagnostic(
                    SkillDiagnosticCodes.FileInfoFailed,
                    dirInfoResult.Error?.Message ?? "Unknown filesystem failure.",
                    dir));
            }

            return new SkillLoadResult(skills, diagnostics);
        }

        var dirInfo = dirInfoResult.Value!;
        if (await ResolveKindAsync(env, dirInfo, diagnostics, cancellationToken).ConfigureAwait(false) != FileKinds.Directory)
        {
            return new SkillLoadResult(skills, diagnostics);
        }

        await AddIgnoreRulesAsync(env, ignoreMatcher, dir, rootDir, diagnostics, cancellationToken).ConfigureAwait(false);
        var entriesResult = await env.ListDirAsync(dir, cancellationToken).ConfigureAwait(false);
        if (!entriesResult.Ok)
        {
            diagnostics.Add(Diagnostic(
                SkillDiagnosticCodes.ListFailed,
                entriesResult.Error?.Message ?? "Unknown filesystem failure.",
                dir));
            return new SkillLoadResult(skills, diagnostics);
        }

        var entries = entriesResult.Value!;
        foreach (var entry in entries)
        {
            if (!string.Equals(entry.Name, "SKILL.md", StringComparison.Ordinal))
            {
                continue;
            }

            var kind = await ResolveKindAsync(env, entry, diagnostics, cancellationToken).ConfigureAwait(false);
            if (kind != FileKinds.File)
            {
                continue;
            }

            var relativePath = RelativeEnvPath(rootDir, entry.Path);
            if (ignoreMatcher.Ignores(relativePath))
            {
                continue;
            }

            var loaded = await LoadSkillFromFileAsync(env, entry.Path, dirInfo.Name, cancellationToken).ConfigureAwait(false);
            if (loaded.Skill is not null)
            {
                skills.Add(loaded.Skill);
            }

            diagnostics.AddRange(loaded.Diagnostics);
            return new SkillLoadResult(skills, diagnostics);
        }

        foreach (var entry in entries.OrderBy(static entry => entry.Name, StringComparer.CurrentCulture))
        {
            if (entry.Name.StartsWith('.') ||
                string.Equals(entry.Name, "node_modules", StringComparison.Ordinal))
            {
                continue;
            }

            var fullPath = entry.Path;
            var kind = await ResolveKindAsync(env, entry, diagnostics, cancellationToken).ConfigureAwait(false);
            if (kind is null)
            {
                continue;
            }

            var relativePath = RelativeEnvPath(rootDir, fullPath);
            var ignorePath = kind == FileKinds.Directory ? $"{relativePath}/" : relativePath;
            if (ignoreMatcher.Ignores(ignorePath))
            {
                continue;
            }

            if (kind == FileKinds.Directory)
            {
                var result = await LoadSkillsFromDirectoryAsync(
                        env,
                        fullPath,
                        includeRootFiles: false,
                        ignoreMatcher,
                        rootDir,
                        diagnostics,
                        cancellationToken)
                    .ConfigureAwait(false);
                skills.AddRange(result.Skills);
                diagnostics.AddRange(result.Diagnostics);
                continue;
            }

            if (kind != FileKinds.File || !includeRootFiles || !entry.Name.EndsWith(".md", StringComparison.Ordinal))
            {
                continue;
            }

            var resultForFile = await LoadSkillFromFileAsync(env, fullPath, dirInfo.Name, cancellationToken).ConfigureAwait(false);
            if (resultForFile.Skill is not null)
            {
                skills.Add(resultForFile.Skill);
            }

            diagnostics.AddRange(resultForFile.Diagnostics);
        }

        return new SkillLoadResult(skills, diagnostics);
    }

    private static async Task AddIgnoreRulesAsync(
        ExecutionEnv env,
        GitIgnoreMatcher matcher,
        string dir,
        string rootDir,
        List<SkillDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var relativeDir = RelativeEnvPath(rootDir, dir);
        var prefix = string.IsNullOrEmpty(relativeDir) ? string.Empty : $"{relativeDir}/";
        foreach (var filename in _ignoreFileNames)
        {
            var ignorePathResult = await env.JoinPathAsync([dir, filename], cancellationToken).ConfigureAwait(false);
            if (!ignorePathResult.Ok)
            {
                diagnostics.Add(Diagnostic(
                    SkillDiagnosticCodes.FileInfoFailed,
                    ignorePathResult.Error?.Message ?? "Unknown filesystem failure.",
                    dir));
                continue;
            }

            var ignorePath = ignorePathResult.Value!;
            var info = await env.FileInfoAsync(ignorePath, cancellationToken).ConfigureAwait(false);
            if (!info.Ok)
            {
                if (info.Error?.Code != FileErrorCodes.NotFound)
                {
                    diagnostics.Add(Diagnostic(
                        SkillDiagnosticCodes.FileInfoFailed,
                        info.Error?.Message ?? "Unknown filesystem failure.",
                        ignorePath));
                }

                continue;
            }

            if (info.Value!.Kind != FileKinds.File)
            {
                continue;
            }

            var content = await env.ReadTextFileAsync(ignorePath, cancellationToken).ConfigureAwait(false);
            if (!content.Ok)
            {
                diagnostics.Add(Diagnostic(
                    SkillDiagnosticCodes.ReadFailed,
                    content.Error?.Message ?? "Unknown filesystem failure.",
                    ignorePath));
                continue;
            }

            var patterns = content.Value!
                .Split(["\r\n", "\n", "\r"], StringSplitOptions.None)
                .Select(line => PrefixIgnorePattern(line, prefix))
                .Where(static line => line is not null)
                .Select(static line => line!)
                .ToArray();
            if (patterns.Length > 0)
            {
                matcher.Add(patterns);
            }
        }
    }

    private static string? PrefixIgnorePattern(string line, string prefix)
    {
        var trimmed = line.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed.StartsWith('#') && !trimmed.StartsWith("\\#", StringComparison.Ordinal))
        {
            return null;
        }

        var pattern = line;
        var negated = false;
        if (pattern.StartsWith('!'))
        {
            negated = true;
            pattern = pattern[1..];
        }
        else if (pattern.StartsWith("\\!", StringComparison.Ordinal))
        {
            pattern = pattern[1..];
        }

        if (pattern.StartsWith('/'))
        {
            pattern = pattern[1..];
        }

        var prefixed = string.IsNullOrEmpty(prefix) ? pattern : prefix + pattern;
        return negated ? "!" + prefixed : prefixed;
    }

    private static async Task<LoadedSkill> LoadSkillFromFileAsync(
        ExecutionEnv env,
        string filePath,
        string parentDirectoryName,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<SkillDiagnostic>();
        var isDeclaredSkill = string.Equals(
            filePath.TrimEnd('/', '\\').Split(['/', '\\']).LastOrDefault(),
            "SKILL.md",
            StringComparison.Ordinal);
        var rawContent = await env.ReadTextFileAsync(filePath, cancellationToken).ConfigureAwait(false);
        if (!rawContent.Ok)
        {
            diagnostics.Add(Diagnostic(
                SkillDiagnosticCodes.ReadFailed,
                rawContent.Error?.Message ?? "Unknown filesystem failure.",
                filePath));
            return new LoadedSkill(null, diagnostics);
        }

        var parsed = ParseFrontmatter(rawContent.Value!);
        if (!parsed.Ok)
        {
            if (isDeclaredSkill)
            {
                diagnostics.Add(Diagnostic(
                    SkillDiagnosticCodes.ParseFailed,
                    parsed.Error?.Message ?? "Unable to parse YAML frontmatter.",
                    filePath));
            }

            return new LoadedSkill(null, diagnostics);
        }

        var frontmatter = parsed.Value!.Frontmatter;
        var description = frontmatter.Description;
        if (!isDeclaredSkill && string.IsNullOrWhiteSpace(description))
        {
            return new LoadedSkill(null, diagnostics);
        }

        foreach (var error in ValidateDescription(description))
        {
            diagnostics.Add(Diagnostic(SkillDiagnosticCodes.InvalidMetadata, error, filePath));
        }

        var name = frontmatter.Name ?? parentDirectoryName;
        foreach (var error in ValidateName(name, parentDirectoryName))
        {
            diagnostics.Add(Diagnostic(SkillDiagnosticCodes.InvalidMetadata, error, filePath));
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            return new LoadedSkill(null, diagnostics);
        }

        return new LoadedSkill(
            new Skill
            {
                Name = name,
                Description = description,
                Content = parsed.Value.Body,
                FilePath = filePath,
                DisableModelInvocation = frontmatter.DisableModelInvocation,
            },
            diagnostics);
    }

    private static List<string> ValidateName(string name, string parentDirectoryName)
    {
        var errors = new List<string>();
        if (!string.Equals(name, parentDirectoryName, StringComparison.Ordinal))
        {
            errors.Add($"name \"{name}\" does not match parent directory \"{parentDirectoryName}\"");
        }

        if (name.Length > _maxNameLength)
        {
            errors.Add($"name exceeds {_maxNameLength} characters ({name.Length})");
        }

        if (!name.All(static character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
        {
            errors.Add("name contains invalid characters (must be lowercase a-z, 0-9, hyphens only)");
        }

        if (name.StartsWith('-') || name.EndsWith('-'))
        {
            errors.Add("name must not start or end with a hyphen");
        }

        if (name.Contains("--", StringComparison.Ordinal))
        {
            errors.Add("name must not contain consecutive hyphens");
        }

        return errors;
    }

    private static List<string> ValidateDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return ["description is required"];
        }

        return description.Length > _maxDescriptionLength
            ? [$"description exceeds {_maxDescriptionLength} characters ({description.Length})"]
            : [];
    }

    private static Result<ParsedFrontmatter, Exception> ParseFrontmatter(string content)
    {
        try
        {
            var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            if (!normalized.StartsWith("---", StringComparison.Ordinal))
            {
                return Result<ParsedFrontmatter, Exception>.Success(new ParsedFrontmatter(new SkillFrontmatter(), normalized));
            }

            var endIndex = normalized.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (endIndex == -1)
            {
                return Result<ParsedFrontmatter, Exception>.Success(new ParsedFrontmatter(new SkillFrontmatter(), normalized));
            }

            var yamlString = normalized[4..endIndex];
            var body = normalized[(endIndex + 4)..].Trim();
            var parsed = ParseYamlFrontmatter(yamlString);
            return parsed.Ok
                ? Result<ParsedFrontmatter, Exception>.Success(new ParsedFrontmatter(parsed.Value!, body))
                : Result<ParsedFrontmatter, Exception>.Failure(parsed.Error!);
        }
        catch (Exception error)
        {
            return Result<ParsedFrontmatter, Exception>.Failure(ResultHelpers.ToError(error));
        }
    }

    private static Result<SkillFrontmatter, Exception> ParseYamlFrontmatter(string yamlString)
    {
        try
        {
            var frontmatter = new SkillFrontmatter();
            var lines = yamlString.Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
                {
                    continue;
                }

                var indentation = CountIndentation(line);
                if (indentation != 0)
                {
                    continue;
                }

                var separator = FindMappingSeparator(line);
                if (separator < 0)
                {
                    continue;
                }

                var key = ParseYamlKey(line[..separator].Trim());
                var rawValue = line[(separator + 1)..].Trim();
                if (IsBlockScalar(rawValue))
                {
                    var block = ReadBlockScalar(lines, ref index, indentation, rawValue);
                    ApplyStringValue(frontmatter, key, block);
                    continue;
                }

                var scalar = ParseYamlScalar(rawValue);
                switch (key)
                {
                    case "name" when scalar.IsString:
                        frontmatter.Name = scalar.StringValue;
                        break;
                    case "description" when scalar.IsString:
                        frontmatter.Description = scalar.StringValue;
                        break;
                    case "disable-model-invocation":
                        frontmatter.DisableModelInvocation = scalar.IsTrue;
                        break;
                }
            }

            return Result<SkillFrontmatter, Exception>.Success(frontmatter);
        }
        catch (Exception error)
        {
            return Result<SkillFrontmatter, Exception>.Failure(ResultHelpers.ToError(error));
        }
    }

    private static void ApplyStringValue(SkillFrontmatter frontmatter, string key, string value)
    {
        switch (key)
        {
            case "name":
                frontmatter.Name = value;
                break;
            case "description":
                frontmatter.Description = value;
                break;
        }
    }

    private static string ParseYamlKey(string value)
    {
        var scalar = ParseYamlScalar(value);
        return scalar.IsString ? scalar.StringValue! : value;
    }

    private static ParsedYamlScalar ParseYamlScalar(string value)
    {
        var trimmed = StripInlineComment(value).Trim();
        if (trimmed.Length == 0 || trimmed is "~" or "null" or "Null" or "NULL")
        {
            return new ParsedYamlScalar(false, null, false);
        }

        ValidateBalancedYamlSyntax(trimmed);
        if (trimmed.StartsWith('[') || trimmed.StartsWith('{'))
        {
            return new ParsedYamlScalar(false, null, false);
        }

        if (trimmed.Length >= 2 && trimmed[0] == '\'' && trimmed[^1] == '\'')
        {
            return new ParsedYamlScalar(true, trimmed[1..^1].Replace("''", "'", StringComparison.Ordinal), false);
        }

        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
        {
            return new ParsedYamlScalar(true, UnescapeDoubleQuoted(trimmed[1..^1]), false);
        }

        if (string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase))
        {
            return new ParsedYamlScalar(false, null, true);
        }

        if (string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase) || IsYamlNumber(trimmed))
        {
            return new ParsedYamlScalar(false, null, false);
        }

        return new ParsedYamlScalar(true, trimmed, false);
    }

    private static string ReadBlockScalar(
        string[] lines,
        ref int index,
        int parentIndentation,
        string header)
    {
        var folded = header.StartsWith('>');
        var strip = header.Contains('-', StringComparison.Ordinal);
        var keep = header.Contains('+', StringComparison.Ordinal);
        var content = new List<string>();
        var contentIndentation = -1;
        for (var next = index + 1; next < lines.Length; next++)
        {
            var line = lines[next];
            if (string.IsNullOrWhiteSpace(line))
            {
                content.Add(string.Empty);
                index = next;
                continue;
            }

            var indentation = CountIndentation(line);
            if (indentation <= parentIndentation)
            {
                break;
            }

            contentIndentation = contentIndentation < 0
                ? indentation
                : Math.Min(contentIndentation, indentation);
            content.Add(line);
            index = next;
        }

        if (contentIndentation > 0)
        {
            for (var item = 0; item < content.Count; item++)
            {
                if (content[item].Length >= contentIndentation)
                {
                    content[item] = content[item][contentIndentation..];
                }
            }
        }

        var result = folded ? FoldBlockLines(content) : string.Join('\n', content);
        if (!strip && (keep || result.Length > 0))
        {
            result += '\n';
        }

        return strip ? result.TrimEnd('\n') : result;
    }

    private static string FoldBlockLines(List<string> lines)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            builder.Append(line);
            if (index + 1 >= lines.Count)
            {
                continue;
            }

            builder.Append(string.IsNullOrEmpty(line) || string.IsNullOrEmpty(lines[index + 1]) ? '\n' : ' ');
        }

        return builder.ToString();
    }

    private static int FindMappingSeparator(string line)
    {
        var quote = '\0';
        var escaped = false;
        var squareDepth = 0;
        var curlyDepth = 0;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (quote != '\0')
            {
                if (quote == '"' && escaped)
                {
                    escaped = false;
                }
                else if (quote == '"' && character == '\\')
                {
                    escaped = true;
                }
                else if (character == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
            }
            else if (character == '[')
            {
                squareDepth++;
            }
            else if (character == ']')
            {
                squareDepth--;
            }
            else if (character == '{')
            {
                curlyDepth++;
            }
            else if (character == '}')
            {
                curlyDepth--;
            }
            else if (character == ':' && squareDepth == 0 && curlyDepth == 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static void ValidateBalancedYamlSyntax(string value)
    {
        var quote = '\0';
        var escaped = false;
        var squareDepth = 0;
        var curlyDepth = 0;
        foreach (var character in value)
        {
            if (quote != '\0')
            {
                if (quote == '"' && escaped)
                {
                    escaped = false;
                }
                else if (quote == '"' && character == '\\')
                {
                    escaped = true;
                }
                else if (character == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
            }
            else if (character == '[')
            {
                squareDepth++;
            }
            else if (character == ']')
            {
                squareDepth--;
            }
            else if (character == '{')
            {
                curlyDepth++;
            }
            else if (character == '}')
            {
                curlyDepth--;
            }

            if (squareDepth < 0 || curlyDepth < 0)
            {
                throw new FormatException("Invalid YAML frontmatter.");
            }
        }

        if (quote != '\0' || squareDepth != 0 || curlyDepth != 0 || escaped)
        {
            throw new FormatException("Invalid YAML frontmatter.");
        }
    }

    private static string StripInlineComment(string value)
    {
        var quote = '\0';
        var escaped = false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (quote != '\0')
            {
                if (quote == '"' && escaped)
                {
                    escaped = false;
                }
                else if (quote == '"' && character == '\\')
                {
                    escaped = true;
                }
                else if (character == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
            }
            else if (character == '#' && (index == 0 || char.IsWhiteSpace(value[index - 1])))
            {
                return value[..index];
            }
        }

        return value;
    }

    private static string UnescapeDoubleQuoted(string value)
    {
        var builder = new StringBuilder(value.Length);
        var escaped = false;
        foreach (var character in value)
        {
            if (!escaped)
            {
                if (character == '\\')
                {
                    escaped = true;
                }
                else
                {
                    builder.Append(character);
                }

                continue;
            }

            builder.Append(character switch
            {
                '0' => '\0',
                'a' => '\a',
                'b' => '\b',
                'f' => '\f',
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                'v' => '\v',
                '\\' => '\\',
                '"' => '"',
                '/' => '/',
                _ => throw new FormatException("Invalid YAML escape sequence."),
            });
            escaped = false;
        }

        if (escaped)
        {
            throw new FormatException("Invalid YAML escape sequence.");
        }

        return builder.ToString();
    }

    private static bool IsYamlNumber(string value) =>
        double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _);

    private static int CountIndentation(string value)
    {
        var count = 0;
        foreach (var character in value)
        {
            if (character == ' ')
            {
                count++;
                continue;
            }

            if (character == '\t')
            {
                throw new FormatException("Tabs are not valid YAML indentation.");
            }

            break;
        }

        return count;
    }

    private static bool IsBlockScalar(string value) =>
        value.StartsWith('|') || value.StartsWith('>');

    private static async Task<string?> ResolveKindAsync(
        ExecutionEnv env,
        FileInfo info,
        List<SkillDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        if (info.Kind is FileKinds.File or FileKinds.Directory)
        {
            return info.Kind;
        }

        var canonicalPath = await env.CanonicalPathAsync(info.Path, cancellationToken).ConfigureAwait(false);
        if (!canonicalPath.Ok)
        {
            if (canonicalPath.Error?.Code != FileErrorCodes.NotFound)
            {
                diagnostics.Add(Diagnostic(
                    SkillDiagnosticCodes.FileInfoFailed,
                    canonicalPath.Error?.Message ?? "Unknown filesystem failure.",
                    info.Path));
            }

            return null;
        }

        var target = await env.FileInfoAsync(canonicalPath.Value!, cancellationToken).ConfigureAwait(false);
        if (!target.Ok)
        {
            if (target.Error?.Code != FileErrorCodes.NotFound)
            {
                diagnostics.Add(Diagnostic(
                    SkillDiagnosticCodes.FileInfoFailed,
                    target.Error?.Message ?? "Unknown filesystem failure.",
                    info.Path));
            }

            return null;
        }

        return target.Value!.Kind is FileKinds.File or FileKinds.Directory ? target.Value.Kind : null;
    }

    private static SkillDiagnostic Diagnostic(string code, string message, string path) => new()
    {
        Code = code,
        Message = message,
        Path = path,
    };

    private static string DirnameEnvPath(string path)
    {
        var normalized = path.TrimEnd('/', '\\');
        var separatorIndex = Math.Max(normalized.LastIndexOf('/'), normalized.LastIndexOf('\\'));
        if (separatorIndex == 2 && normalized.Length > 1 && normalized[1] == ':')
        {
            return normalized[..3];
        }

        return separatorIndex <= 0 ? "/" : normalized[..separatorIndex];
    }

    private static string RelativeEnvPath(string root, string path)
    {
        var normalizedRoot = root.Replace('\\', '/').TrimEnd('/');
        var normalizedPath = path.Replace('\\', '/').TrimEnd('/');
        if (string.Equals(normalizedPath, normalizedRoot, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var prefix = normalizedRoot + "/";
        return normalizedPath.StartsWith(prefix, StringComparison.Ordinal)
            ? normalizedPath[(normalizedRoot.Length + 1)..]
            : normalizedPath.TrimStart('/');
    }

    private sealed record LoadedSkill(Skill? Skill, IReadOnlyList<SkillDiagnostic> Diagnostics);

    private sealed record ParsedFrontmatter(SkillFrontmatter Frontmatter, string Body);

    private sealed record ParsedYamlScalar(bool IsString, string? StringValue, bool IsTrue);

    private sealed class SkillFrontmatter
    {
        public string? Name { get; set; }

        public string? Description { get; set; }

        public bool DisableModelInvocation { get; set; }
    }
}

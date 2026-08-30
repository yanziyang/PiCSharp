using System.Diagnostics;

namespace Pi.Tui;

/// <summary>One completion candidate presented to the terminal editor.</summary>
public sealed record AutocompleteItem
{
    /// <summary>Text inserted when the candidate is selected.</summary>
    public required string Value { get; init; }

    /// <summary>Short text shown in the completion list.</summary>
    public required string Label { get; init; }

    /// <summary>Optional explanatory text shown alongside the label.</summary>
    public string? Description { get; init; }
}

/// <summary>A completion set and the source prefix it replaces.</summary>
public sealed record AutocompleteSuggestions
{
    /// <summary>Completion candidates in display order.</summary>
    public required IReadOnlyList<AutocompleteItem> Items { get; init; }

    /// <summary>Text matched by the candidates.</summary>
    public required string Prefix { get; init; }
}

/// <summary>Text and cursor position returned after applying a completion.</summary>
public sealed record AutocompleteCompletion(
    IReadOnlyList<string> Lines,
    int CursorLine,
    int CursorCol);

/// <summary>Options supplied while querying an autocomplete provider.</summary>
public sealed class AutocompleteOptions
{
    /// <summary>Cancellation token corresponding to the upstream AbortSignal.</summary>
    public CancellationToken Signal { get; init; }

    /// <summary>Forces extraction even when the text is not a natural trigger.</summary>
    public bool Force { get; init; }

}

/// <summary>Slash command metadata and optional argument completion callback.</summary>
public sealed class SlashCommand
{
    /// <summary>Command name without the leading slash.</summary>
    public required string Name { get; init; }

    /// <summary>Optional command description.</summary>
    public string? Description { get; init; }

    /// <summary>Optional argument hint displayed after the command name.</summary>
    public string? ArgumentHint { get; init; }

    /// <summary>
    /// Gets argument completions. A completed ValueTask represents synchronous TypeScript values;
    /// a non-completed ValueTask represents the Promise form.
    /// </summary>
    public Func<string, ValueTask<AutocompleteItem[]?>>? GetArgumentCompletions { get; init; }
}

/// <summary>Public autocomplete contract implemented by built-in and extension providers.</summary>
public interface IAutocompleteProvider
{
    /// <summary>Characters that naturally trigger this provider at token boundaries.</summary>
    IReadOnlyList<string>? TriggerCharacters => null;

    /// <summary>
    /// Gets suggestions for the text and cursor position. A null result means no suggestions are
    /// available and is distinct from a non-null result with an empty item collection.
    /// </summary>
    ValueTask<AutocompleteSuggestions?> GetSuggestions(
        IReadOnlyList<string> lines,
        int cursorLine,
        int cursorCol,
        AutocompleteOptions options);

    /// <summary>Applies a selected item and returns the updated lines and cursor position.</summary>
    AutocompleteCompletion ApplyCompletion(
        IReadOnlyList<string> lines,
        int cursorLine,
        int cursorCol,
        AutocompleteItem item,
        string prefix);

    /// <summary>
    /// Checks whether explicit file completion should trigger. The TypeScript member is optional;
    /// the default true preserves the upstream behavior when an extension omits it.
    /// </summary>
    bool ShouldTriggerFileCompletion(
        IReadOnlyList<string> lines,
        int cursorLine,
        int cursorCol) => true;
}

/// <summary>Factory used by extensions to wrap the current autocomplete provider.</summary>
public delegate IAutocompleteProvider AutocompleteProviderFactory(IAutocompleteProvider current);

/// <summary>Combined slash-command, direct-path, and fd-backed @-file autocomplete provider.</summary>
public sealed class CombinedAutocompleteProvider : IAutocompleteProvider
{
    private static readonly char[] _pathDelimiters = [' ', '\t', '"', '\'', '='];
    private readonly IReadOnlyList<object> _commands;
    private readonly string _basePath;
    private readonly string? _fdPath;

    /// <summary>Initializes a provider with no slash commands.</summary>
    public CombinedAutocompleteProvider(string basePath, string? fdPath = null)
        : this(Array.Empty<object>(), basePath, fdPath)
    {
    }

    /// <summary>Initializes a provider with a heterogeneous slash-command/item list.</summary>
    public CombinedAutocompleteProvider(IEnumerable<object>? commands, string basePath, string? fdPath = null)
    {
        ArgumentNullException.ThrowIfNull(basePath);
        _commands = (commands ?? Array.Empty<object>()).ToArray();
        _basePath = basePath;
        _fdPath = fdPath;
    }

    /// <inheritdoc />
    public async ValueTask<AutocompleteSuggestions?> GetSuggestions(
        IReadOnlyList<string> lines,
        int cursorLine,
        int cursorCol,
        AutocompleteOptions options)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(options);

        var currentLine = GetLine(lines, cursorLine);
        var textBeforeCursor = Slice(currentLine, 0, cursorCol);

        var atPrefix = ExtractAtPrefix(textBeforeCursor);
        if (atPrefix is not null)
        {
            var parsed = ParsePathPrefix(atPrefix);
            var suggestions = await GetFuzzyFileSuggestions(
                parsed.RawPrefix,
                new FuzzyFileOptions(parsed.IsQuotedPrefix, options.Signal)).ConfigureAwait(false);
            if (suggestions.Count == 0)
            {
                return null;
            }

            return new AutocompleteSuggestions { Items = suggestions, Prefix = atPrefix };
        }

        if (!options.Force && textBeforeCursor.StartsWith('/'))
        {
            var spaceIndex = textBeforeCursor.IndexOf(' ');
            if (spaceIndex < 0)
            {
                var prefix = textBeforeCursor[1..];
                var commandItems = _commands.Select(CreateCommandCandidate).ToArray();
                var filtered = Fuzzy.Filter(commandItems, prefix, static item => item.Name)
                    .Select(static item => new AutocompleteItem
                    {
                        Value = item.Name,
                        Label = item.Label,
                        Description = item.Description,
                    })
                    .ToArray();
                if (filtered.Length == 0)
                {
                    return null;
                }

                return new AutocompleteSuggestions { Items = filtered, Prefix = textBeforeCursor };
            }

            var commandName = textBeforeCursor[1..spaceIndex];
            var argumentText = textBeforeCursor[(spaceIndex + 1)..];
            var command = _commands.FirstOrDefault(candidate => GetCommandName(candidate) == commandName);
            if (command is not SlashCommand slashCommand || slashCommand.GetArgumentCompletions is null)
            {
                return null;
            }

            var argumentSuggestions = await slashCommand.GetArgumentCompletions(argumentText).ConfigureAwait(false);
            if (argumentSuggestions is null || argumentSuggestions.Length == 0)
            {
                return null;
            }

            return new AutocompleteSuggestions { Items = argumentSuggestions, Prefix = argumentText };
        }

        var pathMatch = ExtractPathPrefix(textBeforeCursor, options.Force);
        if (pathMatch is null)
        {
            return null;
        }

        var pathSuggestions = GetFileSuggestions(pathMatch);
        return pathSuggestions.Count == 0
            ? null
            : new AutocompleteSuggestions { Items = pathSuggestions, Prefix = pathMatch };
    }

    /// <inheritdoc />
    public AutocompleteCompletion ApplyCompletion(
        IReadOnlyList<string> lines,
        int cursorLine,
        int cursorCol,
        AutocompleteItem item,
        string prefix)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(prefix);

        var currentLine = GetLine(lines, cursorLine);
        var beforePrefix = Slice(currentLine, 0, cursorCol - prefix.Length);
        var afterCursor = Slice(currentLine, cursorCol);
        var isQuotedPrefix = prefix.StartsWith('"') || prefix.Length >= 2 && prefix[0] == '@' && prefix[1] == '"';
        var hasLeadingQuoteAfterCursor = afterCursor.StartsWith('"');
        var hasTrailingQuoteInItem = item.Value.EndsWith('"');
        var adjustedAfterCursor = isQuotedPrefix && hasTrailingQuoteInItem && hasLeadingQuoteAfterCursor
            ? afterCursor[1..]
            : afterCursor;

        var isSlashCommand = prefix.StartsWith('/') &&
            string.IsNullOrWhiteSpace(beforePrefix) &&
            !prefix[1..].Contains('/');
        var newLines = lines.ToArray();
        if (isSlashCommand)
        {
            newLines[cursorLine] = $"{beforePrefix}/{item.Value} {adjustedAfterCursor}";
            return new AutocompleteCompletion(newLines, cursorLine, beforePrefix.Length + item.Value.Length + 2);
        }

        if (prefix.StartsWith('@'))
        {
            var isDirectory = item.Label.EndsWith('/');
            var suffix = isDirectory ? string.Empty : " ";
            newLines[cursorLine] = beforePrefix + item.Value + suffix + adjustedAfterCursor;
            var hasTrailingQuote = item.Value.EndsWith('"');
            var cursorOffset = isDirectory && hasTrailingQuote ? item.Value.Length - 1 : item.Value.Length;
            return new AutocompleteCompletion(
                newLines,
                cursorLine,
                beforePrefix.Length + cursorOffset + suffix.Length);
        }

        var textBeforeCursor = Slice(currentLine, 0, cursorCol);
        if (textBeforeCursor.Contains('/') && textBeforeCursor.Contains(' '))
        {
            newLines[cursorLine] = beforePrefix + item.Value + adjustedAfterCursor;
            var isDirectory = item.Label.EndsWith('/');
            var hasTrailingQuote = item.Value.EndsWith('"');
            var cursorOffset = isDirectory && hasTrailingQuote ? item.Value.Length - 1 : item.Value.Length;
            return new AutocompleteCompletion(newLines, cursorLine, beforePrefix.Length + cursorOffset);
        }

        newLines[cursorLine] = beforePrefix + item.Value + adjustedAfterCursor;
        var pathIsDirectory = item.Label.EndsWith('/');
        var pathHasTrailingQuote = item.Value.EndsWith('"');
        var pathCursorOffset = pathIsDirectory && pathHasTrailingQuote ? item.Value.Length - 1 : item.Value.Length;
        return new AutocompleteCompletion(newLines, cursorLine, beforePrefix.Length + pathCursorOffset);
    }

    /// <inheritdoc />
    public bool ShouldTriggerFileCompletion(IReadOnlyList<string> lines, int cursorLine, int cursorCol)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var currentLine = GetLine(lines, cursorLine);
        var textBeforeCursor = Slice(currentLine, 0, cursorCol);
        var trimmed = textBeforeCursor.Trim();
        return !(trimmed.StartsWith('/') && !trimmed.Contains(' '));
    }

    private async ValueTask<IReadOnlyList<AutocompleteItem>> GetFuzzyFileSuggestions(
        string query,
        FuzzyFileOptions options)
    {
        if (string.IsNullOrEmpty(_fdPath) || options.Signal.IsCancellationRequested)
        {
            return Array.Empty<AutocompleteItem>();
        }

        try
        {
            var scopedQuery = ResolveScopedFuzzyQuery(query);
            var fdBaseDir = scopedQuery?.BaseDir ?? _basePath;
            var fdQuery = scopedQuery?.Query ?? query;
            var baseDirEntries = await GetBaseDirSuggestions(fdBaseDir, fdQuery, options.Signal).ConfigureAwait(false);
            var recursiveEntries = await WalkDirectoryWithFd(
                fdBaseDir,
                _fdPath!,
                fdQuery,
                100,
                options.Signal).ConfigureAwait(false);

            var seenPaths = new HashSet<string>(baseDirEntries.Select(entry => entry.Path), StringComparer.Ordinal);
            var entries = new List<FdEntry>(baseDirEntries.Count + recursiveEntries.Count);
            entries.AddRange(baseDirEntries);
            foreach (var entry in recursiveEntries)
            {
                if (seenPaths.Add(entry.Path))
                {
                    entries.Add(entry);
                }
            }

            if (options.Signal.IsCancellationRequested)
            {
                return Array.Empty<AutocompleteItem>();
            }

            var scoredEntries = entries
                .Select(entry => new ScoredFdEntry(
                    entry,
                    string.IsNullOrEmpty(fdQuery) ? 1 : ScoreEntry(entry.Path, fdQuery, entry.IsDirectory)))
                .Where(entry => entry.Score > 0)
                .ToList();

            scoredEntries.Sort(static (left, right) =>
            {
                var scoreComparison = right.Score.CompareTo(left.Score);
                if (scoreComparison != 0)
                {
                    return scoreComparison;
                }

                var leftDepth = PathDepth(left.Entry.Path);
                var rightDepth = PathDepth(right.Entry.Path);
                var depthComparison = leftDepth.CompareTo(rightDepth);
                if (depthComparison != 0)
                {
                    return depthComparison;
                }

                var lengthComparison = left.Entry.Path.Length.CompareTo(right.Entry.Path.Length);
                return lengthComparison != 0
                    ? lengthComparison
                    : StringComparer.CurrentCulture.Compare(left.Entry.Path, right.Entry.Path);
            });

            var suggestions = new List<AutocompleteItem>(Math.Min(20, scoredEntries.Count));
            foreach (var scoredEntry in scoredEntries.Take(20))
            {
                var entryPath = scoredEntry.Entry.Path;
                var pathWithoutSlash = scoredEntry.Entry.IsDirectory
                    ? entryPath[..^1]
                    : entryPath;
                var displayPath = scopedQuery is null
                    ? pathWithoutSlash
                    : ScopedPathForDisplay(scopedQuery.Value.DisplayBase, pathWithoutSlash);
                var entryName = BaseName(pathWithoutSlash);
                var completionPath = scoredEntry.Entry.IsDirectory ? displayPath + "/" : displayPath;
                var value = BuildCompletionValue(completionPath, true, options.IsQuotedPrefix);
                suggestions.Add(new AutocompleteItem
                {
                    Value = value,
                    Label = entryName + (scoredEntry.Entry.IsDirectory ? "/" : string.Empty),
                    Description = displayPath,
                });
            }

            return suggestions;
        }
        catch
        {
            return Array.Empty<AutocompleteItem>();
        }
    }

    private async ValueTask<IReadOnlyList<FdEntry>> GetBaseDirSuggestions(
        string baseDir,
        string query,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_fdPath) || cancellationToken.IsCancellationRequested)
        {
            return Array.Empty<FdEntry>();
        }

        return await WalkDirectoryWithFd(baseDir, _fdPath!, query, 100, cancellationToken, 1).ConfigureAwait(false);
    }

    private static async ValueTask<IReadOnlyList<FdEntry>> WalkDirectoryWithFd(
        string baseDir,
        string fdPath,
        string query,
        int maxResults,
        CancellationToken cancellationToken,
        int? maxDepth = null)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Array.Empty<FdEntry>();
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fdPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };

        process.StartInfo.ArgumentList.Add("--base-directory");
        process.StartInfo.ArgumentList.Add(baseDir);
        process.StartInfo.ArgumentList.Add("--max-results");
        process.StartInfo.ArgumentList.Add(maxResults.ToString(System.Globalization.CultureInfo.InvariantCulture));
        process.StartInfo.ArgumentList.Add("--type");
        process.StartInfo.ArgumentList.Add("f");
        process.StartInfo.ArgumentList.Add("--type");
        process.StartInfo.ArgumentList.Add("d");
        process.StartInfo.ArgumentList.Add("--follow");
        process.StartInfo.ArgumentList.Add("--hidden");
        process.StartInfo.ArgumentList.Add("--exclude");
        process.StartInfo.ArgumentList.Add(".git");
        process.StartInfo.ArgumentList.Add("--exclude");
        process.StartInfo.ArgumentList.Add(".git/*");
        process.StartInfo.ArgumentList.Add("--exclude");
        process.StartInfo.ArgumentList.Add(".git/**");

        if (maxDepth is not null)
        {
            process.StartInfo.ArgumentList.Add("--max-depth");
            process.StartInfo.ArgumentList.Add(maxDepth.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (ToDisplayPath(query).Contains('/'))
        {
            process.StartInfo.ArgumentList.Add("--full-path");
        }

        if (query.Length > 0)
        {
            process.StartInfo.ArgumentList.Add(BuildFdPathQuery(query));
        }

        try
        {
            if (!process.Start())
            {
                return Array.Empty<FdEntry>();
            }
        }
        catch
        {
            return Array.Empty<FdEntry>();
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var cancellationRegistration = cancellationToken.Register(static state =>
        {
            var child = (Process)state!;
            try
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // The process may have exited between HasExited and Kill.
            }
        }, process);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // The cancellation path is already returning no suggestions.
            }

            try
            {
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            catch
            {
                // Ignore a process that cannot be waited on after cancellation.
            }

            return Array.Empty<FdEntry>();
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        _ = await stderrTask.ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested || process.ExitCode != 0 || stdout.Length == 0)
        {
            return Array.Empty<FdEntry>();
        }

        var results = new List<FdEntry>();
        foreach (var rawLine in stdout.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var displayLine = ToDisplayPath(rawLine.TrimEnd('\r'));
            var hasTrailingSeparator = displayLine.EndsWith('/');
            var normalizedPath = hasTrailingSeparator ? displayLine[..^1] : displayLine;
            if (normalizedPath == ".git" || normalizedPath.StartsWith(".git/", StringComparison.Ordinal) ||
                normalizedPath.Contains("/.git/", StringComparison.Ordinal))
            {
                continue;
            }

            results.Add(new FdEntry(displayLine, hasTrailingSeparator));
        }

        return results;
    }

    private IReadOnlyList<AutocompleteItem> GetFileSuggestions(string prefix)
    {
        try
        {
            var parsed = ParsePathPrefix(prefix);
            var rawPrefix = parsed.RawPrefix;
            var expandedPrefix = rawPrefix.StartsWith('~') ? ExpandHomePath(rawPrefix) : rawPrefix;
            string searchDir;
            string searchPrefix;

            var isRootPrefix = rawPrefix is "" or "./" or "../" or "~" or "~/" or "/" ||
                (parsed.IsAtPrefix && rawPrefix.Length == 0);
            if (isRootPrefix)
            {
                searchDir = rawPrefix.StartsWith('~') || expandedPrefix.StartsWith('/')
                    ? expandedPrefix
                    : Path.Combine(_basePath, expandedPrefix);
                searchPrefix = string.Empty;
            }
            else if (rawPrefix.EndsWith('/'))
            {
                searchDir = rawPrefix.StartsWith('~') || expandedPrefix.StartsWith('/')
                    ? expandedPrefix
                    : Path.Combine(_basePath, expandedPrefix);
                searchPrefix = string.Empty;
            }
            else
            {
                var directory = DirectoryName(expandedPrefix);
                var file = BaseName(expandedPrefix);
                searchDir = rawPrefix.StartsWith('~') || expandedPrefix.StartsWith('/')
                    ? directory
                    : Path.Combine(_basePath, directory);
                searchPrefix = file;
            }

            var suggestions = new List<AutocompleteItem>();
            foreach (var entry in new DirectoryInfo(searchDir).EnumerateFileSystemInfos())
            {
                if (!entry.Name.StartsWith(searchPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var isDirectory = IsDirectory(entry);
                var name = entry.Name;
                var displayPrefix = rawPrefix;
                string relativePath;
                if (displayPrefix.EndsWith('/'))
                {
                    relativePath = displayPrefix + name;
                }
                else if (displayPrefix.Contains('/') || displayPrefix.Contains('\\'))
                {
                    if (displayPrefix.StartsWith("~/", StringComparison.Ordinal))
                    {
                        var homeRelativeDirectory = displayPrefix[2..];
                        var directory = DirectoryName(homeRelativeDirectory);
                        relativePath = $"~/{(directory == "." ? name : CombineForDisplay(directory, name))}";
                    }
                    else if (displayPrefix.StartsWith('/'))
                    {
                        var directory = ToDisplayPath(DirectoryName(displayPrefix));
                        relativePath = directory == "/" ? "/" + name : $"{directory}/{name}";
                    }
                    else
                    {
                        relativePath = CombineForDisplay(DirectoryName(displayPrefix), name);
                        if (displayPrefix.StartsWith("./", StringComparison.Ordinal) &&
                            !relativePath.StartsWith("./", StringComparison.Ordinal))
                        {
                            relativePath = "./" + relativePath;
                        }
                    }
                }
                else
                {
                    relativePath = displayPrefix.StartsWith('~') ? $"~/{name}" : name;
                }

                relativePath = ToDisplayPath(relativePath);
                var pathValue = isDirectory ? relativePath + "/" : relativePath;
                var value = BuildCompletionValue(pathValue, parsed.IsAtPrefix, parsed.IsQuotedPrefix);
                suggestions.Add(new AutocompleteItem
                {
                    Value = value,
                    Label = name + (isDirectory ? "/" : string.Empty),
                });
            }

            suggestions.Sort(static (left, right) =>
            {
                var leftIsDirectory = left.Value.EndsWith('/');
                var rightIsDirectory = right.Value.EndsWith('/');
                if (leftIsDirectory != rightIsDirectory)
                {
                    return leftIsDirectory ? -1 : 1;
                }

                return StringComparer.CurrentCulture.Compare(left.Label, right.Label);
            });
            return suggestions;
        }
        catch
        {
            return Array.Empty<AutocompleteItem>();
        }
    }

    private ScopedFuzzyQuery? ResolveScopedFuzzyQuery(string rawQuery)
    {
        var normalizedQuery = ToDisplayPath(rawQuery);
        var slashIndex = normalizedQuery.LastIndexOf('/');
        if (slashIndex < 0)
        {
            return null;
        }

        var displayBase = normalizedQuery[..(slashIndex + 1)];
        var query = normalizedQuery[(slashIndex + 1)..];
        string baseDir;
        if (displayBase.StartsWith("~/", StringComparison.Ordinal))
        {
            baseDir = ExpandHomePath(displayBase);
        }
        else if (displayBase.StartsWith('/'))
        {
            baseDir = displayBase;
        }
        else
        {
            baseDir = Path.Combine(_basePath, displayBase);
        }

        return Directory.Exists(baseDir) ? new ScopedFuzzyQuery(baseDir, query, displayBase) : null;
    }

    private static string ScopedPathForDisplay(string displayBase, string relativePath)
    {
        var normalizedRelativePath = ToDisplayPath(relativePath);
        return displayBase == "/"
            ? "/" + normalizedRelativePath
            : ToDisplayPath(displayBase) + normalizedRelativePath;
    }

    private static int ScoreEntry(string filePath, string query, bool isDirectory)
    {
        var fileName = BaseName(filePath).ToLowerInvariant();
        var lowerQuery = query.ToLowerInvariant();
        var score = fileName == lowerQuery
            ? 100
            : fileName.StartsWith(lowerQuery, StringComparison.Ordinal)
                ? 80
                : fileName.Contains(lowerQuery, StringComparison.Ordinal)
                    ? 50
                    : filePath.ToLowerInvariant().Contains(lowerQuery, StringComparison.Ordinal)
                        ? 30
                        : 0;

        return isDirectory && score > 0 ? score + 10 : score;
    }

    private static CommandCandidate CreateCommandCandidate(object command)
    {
        var name = GetCommandName(command);
        var hint = command is SlashCommand slashCommand && !string.IsNullOrEmpty(slashCommand.ArgumentHint)
            ? slashCommand.ArgumentHint
            : null;
        var description = command switch
        {
            SlashCommand slash => slash.Description ?? string.Empty,
            AutocompleteItem item => item.Description ?? string.Empty,
            _ => throw new ArgumentException("commands must contain SlashCommand or AutocompleteItem instances", nameof(command)),
        };
        var fullDescription = hint is null
            ? description
            : description.Length > 0 ? $"{hint} — {description}" : hint;
        return new CommandCandidate(name, name, fullDescription.Length == 0 ? null : fullDescription);
    }

    private static string GetCommandName(object command) => command switch
    {
        SlashCommand slashCommand => slashCommand.Name,
        AutocompleteItem item => item.Value,
        _ => throw new ArgumentException("commands must contain SlashCommand or AutocompleteItem instances", nameof(command)),
    };

    private static string BuildCompletionValue(string path, bool isAtPrefix, bool isQuotedPrefix)
    {
        var needsQuotes = isQuotedPrefix || path.Contains(' ');
        var prefix = isAtPrefix ? "@" : string.Empty;
        return needsQuotes ? $"{prefix}\"{path}\"" : prefix + path;
    }

    private static PathPrefix ParsePathPrefix(string prefix)
    {
        if (prefix.StartsWith("@\"", StringComparison.Ordinal))
        {
            return new PathPrefix(prefix[2..], true, true);
        }

        if (prefix.StartsWith('"'))
        {
            return new PathPrefix(prefix[1..], false, true);
        }

        if (prefix.StartsWith('@'))
        {
            return new PathPrefix(prefix[1..], true, false);
        }

        return new PathPrefix(prefix, false, false);
    }

    private static string? ExtractAtPrefix(string text)
    {
        var quotedPrefix = ExtractQuotedPrefix(text);
        if (quotedPrefix?.StartsWith("@\"", StringComparison.Ordinal) == true)
        {
            return quotedPrefix;
        }

        var lastDelimiterIndex = FindLastDelimiter(text);
        var tokenStart = lastDelimiterIndex < 0 ? 0 : lastDelimiterIndex + 1;
        return tokenStart < text.Length && text[tokenStart] == '@' ? text[tokenStart..] : null;
    }

    private static string? ExtractPathPrefix(string text, bool forceExtract = false)
    {
        var quotedPrefix = ExtractQuotedPrefix(text);
        if (quotedPrefix is not null)
        {
            return quotedPrefix;
        }

        var lastDelimiterIndex = FindLastDelimiter(text);
        var pathPrefix = lastDelimiterIndex < 0 ? text : text[(lastDelimiterIndex + 1)..];
        if (forceExtract)
        {
            return pathPrefix;
        }

        if (pathPrefix.Contains('/') || pathPrefix.StartsWith('.') || pathPrefix.StartsWith("~/", StringComparison.Ordinal))
        {
            return pathPrefix;
        }

        return pathPrefix.Length == 0 && text.EndsWith(' ') ? string.Empty : null;
    }

    private static string? ExtractQuotedPrefix(string text)
    {
        var quoteStart = FindUnclosedQuoteStart(text);
        if (quoteStart is null)
        {
            return null;
        }

        var quoteIndex = quoteStart.Value;
        if (quoteIndex > 0 && text[quoteIndex - 1] == '@')
        {
            return IsTokenStart(text, quoteIndex - 1) ? text[(quoteIndex - 1)..] : null;
        }

        return IsTokenStart(text, quoteIndex) ? text[quoteIndex..] : null;
    }

    private static int? FindUnclosedQuoteStart(string text)
    {
        var inQuotes = false;
        var quoteStart = -1;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '"')
            {
                continue;
            }

            inQuotes = !inQuotes;
            if (inQuotes)
            {
                quoteStart = index;
            }
        }

        return inQuotes ? quoteStart : null;
    }

    private static bool IsTokenStart(string text, int index) =>
        index == 0 || Array.IndexOf(_pathDelimiters, text[index - 1]) >= 0;

    private static int FindLastDelimiter(string text)
    {
        for (var index = text.Length - 1; index >= 0; index--)
        {
            if (Array.IndexOf(_pathDelimiters, text[index]) >= 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static string ExpandHomePath(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.StartsWith("~/", StringComparison.Ordinal))
        {
            var expandedPath = Path.Combine(home, path[2..]);
            return path.EndsWith('/') && !expandedPath.EndsWith(Path.DirectorySeparatorChar)
                ? expandedPath + "/"
                : expandedPath;
        }

        return path == "~" ? home : path;
    }

    private static string BuildFdPathQuery(string query)
    {
        var normalized = ToDisplayPath(query);
        if (!normalized.Contains('/'))
        {
            return normalized;
        }

        var hasTrailingSeparator = normalized.EndsWith('/');
        var trimmed = normalized.Trim('/');
        if (trimmed.Length == 0)
        {
            return normalized;
        }

        var segments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(EscapeRegex)
            .ToArray();
        if (segments.Length == 0)
        {
            return normalized;
        }

        var pattern = string.Join("[\\\\/]", segments);
        return hasTrailingSeparator ? pattern + "[\\\\/]" : pattern;
    }

    private static string EscapeRegex(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character is '.' or '*' or '+' or '?' or '^' or '$' or '{' or '}' or '(' or ')' or '|' or '[' or ']' or '\\')
            {
                builder.Append('\\');
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static bool IsDirectory(FileSystemInfo entry)
    {
        if ((entry.Attributes & FileAttributes.Directory) != 0)
        {
            return true;
        }

        return (entry.Attributes & FileAttributes.ReparsePoint) != 0 && Directory.Exists(entry.FullName);
    }

    private static int PathDepth(string path) =>
        ToDisplayPath(path).Split('/', StringSplitOptions.RemoveEmptyEntries).Length;

    private static string DirectoryName(string path)
    {
        var directory = Path.GetDirectoryName(path);
        return string.IsNullOrEmpty(directory) ? "." : directory;
    }

    private static string BaseName(string path)
    {
        var withoutTrailingSeparators = path.TrimEnd('/', '\\');
        return Path.GetFileName(withoutTrailingSeparators);
    }

    private static string CombineForDisplay(string directory, string name) =>
        ToDisplayPath(Path.Combine(directory, name));

    private static string ToDisplayPath(string value) => value.Replace('\\', '/');

    private static string GetLine(IReadOnlyList<string> lines, int line)
    {
        return line >= 0 && line < lines.Count ? lines[line] ?? string.Empty : string.Empty;
    }

    private static string Slice(string value, int start, int? end = null)
    {
        var length = value.Length;
        var normalizedStart = start < 0 ? Math.Max(length + start, 0) : Math.Min(start, length);
        if (end is null)
        {
            return value[normalizedStart..];
        }

        var normalizedEnd = end.Value < 0 ? Math.Max(length + end.Value, 0) : Math.Min(end.Value, length);
        return normalizedEnd <= normalizedStart ? string.Empty : value[normalizedStart..normalizedEnd];
    }

    private readonly record struct FdEntry(string Path, bool IsDirectory);

    private readonly record struct ScoredFdEntry(FdEntry Entry, int Score);

    private readonly record struct PathPrefix(string RawPrefix, bool IsAtPrefix, bool IsQuotedPrefix);

    private readonly record struct ScopedFuzzyQuery(string BaseDir, string Query, string DisplayBase);

    private readonly record struct FuzzyFileOptions(bool IsQuotedPrefix, CancellationToken Signal);

    private readonly record struct CommandCandidate(string Name, string Label, string? Description);
}

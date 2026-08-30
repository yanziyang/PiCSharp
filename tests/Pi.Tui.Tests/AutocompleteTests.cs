using System.Diagnostics;

using Pi.Tui;

using Xunit;

namespace Pi.Tui.Tests;

/// <summary>Ports the upstream CombinedAutocompleteProvider cases.</summary>
public sealed class AutocompleteTests
{
    private static readonly string[] _emptyAtExpected = ["@README.md", "@src/"];

    [Fact(DisplayName = "extracts / from 'hey /' when forced")]
    public async Task ExtractPathPrefix_ExtractsRootAfterTextWhenForced()
    {
        var provider = new CombinedAutocompleteProvider([], "/tmp");

        var result = await GetSuggestionsAsync(provider, "hey /", 5, force: true);

        Assert.NotNull(result);
        Assert.Equal("/", result!.Prefix);
    }

    [Fact(DisplayName = "extracts /A from '/A' when forced")]
    public async Task ExtractPathPrefix_ExtractsAbsolutePrefixWhenForced()
    {
        var provider = new CombinedAutocompleteProvider([], "/tmp");

        var result = await GetSuggestionsAsync(provider, "/A", 2, force: true);

        if (result is not null)
        {
            Assert.Equal("/A", result.Prefix);
        }
    }

    [Fact(DisplayName = "does not trigger for slash commands")]
    public async Task GetSuggestions_DoesNotTriggerForSlashCommands()
    {
        var provider = new CombinedAutocompleteProvider([], "/tmp");

        var result = await GetSuggestionsAsync(provider, "/model", 6, force: true);

        Assert.Null(result);
    }

    [Fact(DisplayName = "triggers for absolute paths after slash command argument")]
    public async Task ExtractPathPrefix_TriggersForAbsolutePathAfterCommandArgument()
    {
        var provider = new CombinedAutocompleteProvider([], "/tmp");

        var result = await GetSuggestionsAsync(provider, "/command /", 10, force: true);

        Assert.NotNull(result);
        Assert.Equal("/", result!.Prefix);
    }

    [Fact(DisplayName = "returns all files and folders for empty @ query")]
    public async Task FdSuggestions_ReturnsFilesAndFoldersForEmptyQuery()
    {
        using var fixture = new AutocompleteFixture();
        fixture.SetupFolder(fixture.BaseDirectory, dirs: ["src"], files: new Dictionary<string, string>
        {
            ["README.md"] = "readme",
        });
        var provider = fixture.CreateFdProvider();

        var result = await GetSuggestionsAsync(provider, "@", 1);

        var values = result?.Items.Select(item => item.Value).OrderBy(value => value).ToArray();
        Assert.Equal(_emptyAtExpected.OrderBy(value => value), values);
    }

    [Fact(DisplayName = "matches file with extension in query")]
    public async Task FdSuggestions_MatchesFileWithExtensionInQuery()
    {
        using var fixture = new AutocompleteFixture();
        fixture.SetupFolder(fixture.BaseDirectory, files: new Dictionary<string, string>
        {
            ["file.txt"] = "content",
        });
        var provider = fixture.CreateFdProvider();

        var result = await GetSuggestionsAsync(provider, "@file.txt", "@file.txt".Length);

        Assert.Contains(result?.Items ?? [], item => item.Value == "@file.txt");
    }

    [Fact(DisplayName = "filters are case insensitive")]
    public async Task FdSuggestions_FiltersCaseInsensitively()
    {
        using var fixture = new AutocompleteFixture();
        fixture.SetupFolder(fixture.BaseDirectory, dirs: ["src"], files: new Dictionary<string, string>
        {
            ["README.md"] = "readme",
        });
        var provider = fixture.CreateFdProvider();

        var result = await GetSuggestionsAsync(provider, "@re", 3);

        var values = result?.Items.Select(item => item.Value).OrderBy(value => value).ToArray();
        Assert.Equal(["@README.md"], values ?? Array.Empty<string>());
    }

    [Fact(DisplayName = "ranks directories before files")]
    public async Task FdSuggestions_RanksDirectoriesBeforeFiles()
    {
        using var fixture = new AutocompleteFixture();
        fixture.SetupFolder(fixture.BaseDirectory, dirs: ["src"], files: new Dictionary<string, string>
        {
            ["src.txt"] = "text",
        });
        var provider = fixture.CreateFdProvider();

        var result = await GetSuggestionsAsync(provider, "@src", 4);

        Assert.Equal("@src/", result?.Items[0].Value);
        Assert.Contains(result?.Items ?? [], item => item.Value == "@src.txt");
    }

    [Fact(DisplayName = "returns nested file paths")]
    public async Task FdSuggestions_ReturnsNestedFilePaths()
    {
        using var fixture = new AutocompleteFixture();
        fixture.SetupFolder(fixture.BaseDirectory, files: new Dictionary<string, string>
        {
            ["src/index.ts"] = "export {};\n",
        });
        var provider = fixture.CreateFdProvider();

        var result = await GetSuggestionsAsync(provider, "@index", 6);

        Assert.Contains(result?.Items ?? [], item => item.Value == "@src/index.ts");
    }

    [Fact(DisplayName = "matches deeply nested paths")]
    public async Task FdSuggestions_MatchesDeeplyNestedPaths()
    {
        using var fixture = new AutocompleteFixture();
        fixture.SetupFolder(fixture.BaseDirectory, files: new Dictionary<string, string>
        {
            ["packages/tui/src/autocomplete.ts"] = "export {};",
            ["packages/ai/src/autocomplete.ts"] = "export {};",
        });
        var provider = fixture.CreateFdProvider();

        var result = await GetSuggestionsAsync(provider, "@tui/src/auto", 12);

        Assert.Contains(result?.Items ?? [], item => item.Value == "@packages/tui/src/autocomplete.ts");
        Assert.DoesNotContain(result?.Items ?? [], item => item.Value == "@packages/ai/src/autocomplete.ts");
    }

    [Fact(DisplayName = "matches directory in middle of path with --full-path")]
    public async Task FdSuggestions_MatchesDirectoryInMiddleOfPath()
    {
        using var fixture = new AutocompleteFixture();
        fixture.SetupFolder(fixture.BaseDirectory, files: new Dictionary<string, string>
        {
            ["src/components/Button.tsx"] = "export {};",
            ["src/utils/helpers.ts"] = "export {};",
        });
        var provider = fixture.CreateFdProvider();

        var result = await GetSuggestionsAsync(provider, "@components/", 11);

        Assert.Contains(result?.Items ?? [], item => item.Value == "@src/components/Button.tsx");
        Assert.DoesNotContain(result?.Items ?? [], item => item.Value == "@src/utils/helpers.ts");
    }

    [Fact(DisplayName = "scopes fuzzy search to relative directories and searches recursively")]
    public async Task FdSuggestions_ScopesSearchToRelativeDirectory()
    {
        using var fixture = new AutocompleteFixture();
        fixture.SetupFolder(fixture.OutsideDirectory, files: new Dictionary<string, string>
        {
            ["nested/alpha.ts"] = "export {};",
            ["nested/deeper/also-alpha.ts"] = "export {};",
            ["nested/deeper/zzz.ts"] = "export {};",
        });
        var provider = fixture.CreateFdProvider();

        var result = await GetSuggestionsAsync(provider, "@../outside/a", 14);

        Assert.Contains(result?.Items ?? [], item => item.Value == "@../outside/nested/alpha.ts");
        Assert.Contains(result?.Items ?? [], item => item.Value == "@../outside/nested/deeper/also-alpha.ts");
        Assert.DoesNotContain(result?.Items ?? [], item => item.Value == "@../outside/nested/deeper/zzz.ts");
    }

    [Fact(DisplayName = "ranks shallower same-score @ matches before deeper matches")]
    public async Task FdSuggestions_RanksShallowerSameScoreMatchesFirst()
    {
        using var fixture = new AutocompleteFixture();
        fixture.SetupFolder(fixture.BaseDirectory, dirs:
        [
            "scope/aaa/venv/lib/python3.12/site-packages/pkg/core/profile",
            "scope/projects",
        ]);
        var provider = fixture.CreateFdProvider();

        var result = await GetSuggestionsAsync(provider, "@scope/pro", 9);

        var values = result?.Items.Select(item => item.Value).ToArray() ?? [];
        Assert.Equal("@scope/projects/", values[0]);
        Assert.Contains("@scope/aaa/venv/lib/python3.12/site-packages/pkg/core/profile/", values);
    }

    [Fact(DisplayName = "includes scoped direct children when recursive @ matches are flooded")]
    public async Task FdSuggestions_IncludesScopedDirectChildrenWhenRecursiveMatchesAreFlooded()
    {
        using var fixture = new AutocompleteFixture();
        var floodedDirectories = Enumerable.Range(1, 250)
            .Select(index => $"scope/a{index:000}/venv/lib/python3.12/site-packages/pkg/core/profile")
            .ToList();
        fixture.SetupFolder(fixture.BaseDirectory, dirs: ["scope/projects", .. floodedDirectories]);
        var provider = fixture.CreateFdProvider();

        var result = await GetSuggestionsAsync(provider, "@scope/pro", 9);

        var values = result?.Items.Select(item => item.Value).ToArray() ?? [];
        Assert.Equal("@scope/projects/", values[0]);
        Assert.Contains(values, value => value.Contains("/profile/", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "quotes paths with spaces for @ suggestions")]
    public async Task FdSuggestions_QuotesPathsWithSpaces()
    {
        using var fixture = new AutocompleteFixture();
        fixture.SetupFolder(fixture.BaseDirectory, dirs: ["my folder"], files: new Dictionary<string, string>
        {
            ["my folder/test.txt"] = "content",
        });
        var provider = fixture.CreateFdProvider();

        var result = await GetSuggestionsAsync(provider, "@my", 3);

        Assert.Contains(result?.Items ?? [], item => item.Value == "@\"my folder/\"");
    }

    [Fact(DisplayName = "includes hidden paths but excludes .git")]
    public async Task FdSuggestions_IncludesHiddenPathsButExcludesGit()
    {
        using var fixture = new AutocompleteFixture();
        fixture.SetupFolder(fixture.BaseDirectory, dirs: [".pi", ".github", ".git"], files: new Dictionary<string, string>
        {
            [".pi/config.json"] = "{}",
            [".github/workflows/ci.yml"] = "name: ci",
            [".git/config"] = "[core]",
        });
        var provider = fixture.CreateFdProvider();

        var result = await GetSuggestionsAsync(provider, "@", 1);

        var values = result?.Items.Select(item => item.Value).ToArray() ?? [];
        Assert.Contains("@.pi/", values);
        Assert.Contains("@.github/", values);
        Assert.DoesNotContain(values, value => value == "@.git" || value.StartsWith("@.git/", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "follows symlinked directories for fuzzy @ search")]
    public async Task FdSuggestions_FollowsSymlinkedDirectories()
    {
        using var fixture = new AutocompleteFixture();
        fixture.SetupFolder(fixture.BaseDirectory, files: new Dictionary<string, string>
        {
            ["dir/some_file.txt"] = "real",
        });
        fixture.SetupFolder(fixture.OutsideDirectory, files: new Dictionary<string, string>
        {
            ["some_file.txt"] = "symlinked",
        });
        CreateDirectoryLink(
            Path.Combine(fixture.BaseDirectory, "symlinked_dir"),
            fixture.OutsideDirectory);
        var provider = fixture.CreateFdProvider();

        var result = await GetSuggestionsAsync(provider, "@some", 5);

        var values = result?.Items.Select(item => item.Value).ToArray() ?? [];
        Assert.Contains("@dir/some_file.txt", values);
        Assert.Contains("@symlinked_dir/some_file.txt", values);
    }

    [Fact(DisplayName = "returns symlinked directories when matching their name")]
    public async Task FdSuggestions_ReturnsSymlinkedDirectories()
    {
        using var fixture = new AutocompleteFixture();
        fixture.SetupFolder(fixture.OutsideDirectory, files: new Dictionary<string, string>
        {
            ["nested/file.txt"] = "symlinked",
        });
        CreateDirectoryLink(
            Path.Combine(fixture.BaseDirectory, "symlinked_dir"),
            fixture.OutsideDirectory);
        var provider = fixture.CreateFdProvider();

        var result = await GetSuggestionsAsync(provider, "@symlinked", 9);

        Assert.Contains(result?.Items ?? [], item => item.Value == "@symlinked_dir/");
    }

    [Fact(DisplayName = "returns symlinked files without requiring type l")]
    public async Task FdSuggestions_ReturnsSymlinkedFiles()
    {
        using var fixture = new AutocompleteFixture();
        fixture.SetupFolder(fixture.BaseDirectory, files: new Dictionary<string, string>
        {
            ["original.txt"] = "content",
        });
        CreateFileLink(
            Path.Combine(fixture.BaseDirectory, "link.txt"),
            Path.Combine(fixture.BaseDirectory, "original.txt"));
        var provider = fixture.CreateFdProvider();

        var result = await GetSuggestionsAsync(provider, "@link", 5);

        Assert.Contains(result?.Items ?? [], item => item.Value == "@link.txt");
    }

    [Fact(DisplayName = "returns the same @ suggestions when the cwd path contains the query")]
    public async Task FdSuggestions_IsIndependentOfCwdPathName()
    {
        using var fixture = new AutocompleteFixture();
        var normalBaseDirectory = Path.Combine(fixture.RootDirectory, "cwd-normal");
        var queryInPathBaseDirectory = Path.Combine(fixture.RootDirectory, "cwd-plan-repro");
        Directory.CreateDirectory(normalBaseDirectory);
        Directory.CreateDirectory(queryInPathBaseDirectory);
        var directories = new[]
        {
            "packages/coding-agent/examples/extensions/plan-mode",
        };
        var files = new Dictionary<string, string>
        {
            ["packages/coding-agent/examples/extensions/plan-mode/README.md"] = "readme",
            ["packages/tui/docs/plan.md"] = "plan",
        };
        SetupFolder(normalBaseDirectory, directories, files);
        SetupFolder(queryInPathBaseDirectory, directories, files);

        var normalProvider = new CombinedAutocompleteProvider([], normalBaseDirectory, FakeFdPath());
        var queryInPathProvider = new CombinedAutocompleteProvider([], queryInPathBaseDirectory, FakeFdPath());
        var normalResult = await GetSuggestionsAsync(normalProvider, "@plan", 5);
        var queryInPathResult = await GetSuggestionsAsync(queryInPathProvider, "@plan", 5);

        Assert.Equal(NormalizeDescriptions(normalResult), NormalizeDescriptions(queryInPathResult));
        Assert.Contains("plan-mode/ :: packages/coding-agent/examples/extensions/plan-mode", NormalizeDescriptions(normalResult));
        Assert.Contains("plan.md :: packages/tui/docs/plan.md", NormalizeDescriptions(normalResult));
    }

    [Fact(DisplayName = "continues autocomplete inside quoted @ paths")]
    public async Task FdSuggestions_ContinuesInsideQuotedAtPath()
    {
        using var fixture = new AutocompleteFixture();
        fixture.SetupFolder(fixture.BaseDirectory, files: new Dictionary<string, string>
        {
            ["my folder/test.txt"] = "content",
            ["my folder/other.txt"] = "content",
        });
        var provider = fixture.CreateFdProvider();
        var line = "@\"my folder/\"";

        var result = await GetSuggestionsAsync(provider, line, line.Length - 1);

        Assert.NotNull(result);
        Assert.Contains(result!.Items, item => item.Value == "@\"my folder/test.txt\"");
        Assert.Contains(result.Items, item => item.Value == "@\"my folder/other.txt\"");
    }

    [Fact(DisplayName = "applies quoted @ completion without duplicating closing quote")]
    public async Task FdSuggestions_AppliesQuotedAtCompletionWithoutDuplicatingQuote()
    {
        using var fixture = new AutocompleteFixture();
        fixture.SetupFolder(fixture.BaseDirectory, files: new Dictionary<string, string>
        {
            ["my folder/test.txt"] = "content",
        });
        var provider = fixture.CreateFdProvider();
        var line = "@\"my folder/te\"";
        var cursorCol = line.Length - 1;

        var result = await GetSuggestionsAsync(provider, line, cursorCol);

        Assert.NotNull(result);
        var item = Assert.Single(result!.Items, entry => entry.Value == "@\"my folder/test.txt\"");
        var applied = provider.ApplyCompletion([line], 0, cursorCol, item, result.Prefix);
        Assert.Equal("@\"my folder/test.txt\" ", applied.Lines[0]);
    }

    [Fact(DisplayName = "preserves ./ prefix when completing paths")]
    public async Task DirectPathCompletion_PreservesDotSlashPrefix()
    {
        using var fixture = new AutocompleteFixture();
        fixture.SetupFolder(fixture.BaseDirectory, files: new Dictionary<string, string>
        {
            ["update.sh"] = "#!/bin/bash",
            ["utils.ts"] = "export {};",
        });
        var provider = fixture.CreateProvider();

        var result = await GetSuggestionsAsync(provider, "./up", 4, force: true);

        Assert.NotNull(result);
        Assert.Contains(result!.Items, item => item.Value == "./update.sh");
    }

    [Fact(DisplayName = "preserves ./ prefix for directory completions")]
    public async Task DirectPathCompletion_PreservesDotSlashDirectoryPrefix()
    {
        using var fixture = new AutocompleteFixture();
        fixture.SetupFolder(fixture.BaseDirectory, dirs: ["src"], files: new Dictionary<string, string>
        {
            ["src/index.ts"] = "export {};",
        });
        var provider = fixture.CreateProvider();

        var result = await GetSuggestionsAsync(provider, "./sr", 4, force: true);

        Assert.NotNull(result);
        Assert.Contains(result!.Items, item => item.Value == "./src/");
    }

    [Fact(DisplayName = "quotes paths with spaces for direct completion")]
    public async Task DirectPathCompletion_QuotesPathsWithSpaces()
    {
        using var fixture = new AutocompleteFixture();
        fixture.SetupFolder(fixture.BaseDirectory, dirs: ["my folder"], files: new Dictionary<string, string>
        {
            ["my folder/test.txt"] = "content",
        });
        var provider = fixture.CreateProvider();

        var result = await GetSuggestionsAsync(provider, "my", 2, force: true);

        Assert.NotNull(result);
        Assert.Contains(result!.Items, item => item.Value == "\"my folder/\"");
    }

    [Fact(DisplayName = "continues completion inside quoted paths")]
    public async Task DirectPathCompletion_ContinuesInsideQuotedPath()
    {
        using var fixture = new AutocompleteFixture();
        fixture.SetupFolder(fixture.BaseDirectory, files: new Dictionary<string, string>
        {
            ["my folder/test.txt"] = "content",
            ["my folder/other.txt"] = "content",
        });
        var provider = fixture.CreateProvider();
        var line = "\"my folder/\"";

        var result = await GetSuggestionsAsync(provider, line, line.Length - 1, force: true);

        Assert.NotNull(result);
        Assert.Contains(result!.Items, item => item.Value == "\"my folder/test.txt\"");
        Assert.Contains(result.Items, item => item.Value == "\"my folder/other.txt\"");
    }

    [Fact(DisplayName = "applies quoted completion without duplicating closing quote")]
    public async Task DirectPathCompletion_AppliesQuotedCompletionWithoutDuplicatingQuote()
    {
        using var fixture = new AutocompleteFixture();
        fixture.SetupFolder(fixture.BaseDirectory, files: new Dictionary<string, string>
        {
            ["my folder/test.txt"] = "content",
        });
        var provider = fixture.CreateProvider();
        var line = "\"my folder/te\"";
        var cursorCol = line.Length - 1;

        var result = await GetSuggestionsAsync(provider, line, cursorCol, force: true);

        Assert.NotNull(result);
        var item = Assert.Single(result!.Items, entry => entry.Value == "\"my folder/test.txt\"");
        var applied = provider.ApplyCompletion([line], 0, cursorCol, item, result.Prefix);
        Assert.Equal("\"my folder/test.txt\"", applied.Lines[0]);
    }

    [Fact(DisplayName = "extension provider can wrap the built-in provider")]
    public async Task ExtensionProvider_CanWrapBuiltInProvider()
    {
        using var fixture = new AutocompleteFixture();
        File.WriteAllText(Path.Combine(fixture.BaseDirectory, "file.txt"), "content");
        var provider = new CombinedAutocompleteProvider([], fixture.BaseDirectory);
        AutocompleteProviderFactory factory = current => new DelegatingAutocompleteProvider(current);
        IAutocompleteProvider wrapped = factory(provider);

        var result = await wrapped.GetSuggestions(
            ["./fi"],
            0,
            4,
            new AutocompleteOptions { Force = true });

        Assert.NotNull(result);
        Assert.Null(wrapped.TriggerCharacters);
        Assert.True(wrapped.ShouldTriggerFileCompletion(["text"], 0, 4));
    }

    private static async Task<AutocompleteSuggestions?> GetSuggestionsAsync(
        CombinedAutocompleteProvider provider,
        string line,
        int cursorCol,
        bool force = false)
    {
        return await provider.GetSuggestions(
            [line],
            0,
            cursorCol,
            new AutocompleteOptions { Force = force });
    }

    private static string FakeFdPath()
    {
        var fakeFdRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "FakeFd", "bin"));
        var executable = Directory.EnumerateFiles(fakeFdRoot, "FakeFd*", SearchOption.AllDirectories)
            .FirstOrDefault(path =>
                !path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
        return executable ?? throw new InvalidOperationException($"Fake fd executable not found below {fakeFdRoot}.");
    }

    private static string[] NormalizeDescriptions(AutocompleteSuggestions? result) =>
        (result?.Items ?? [])
            .Select(item => $"{item.Label} :: {item.Description ?? string.Empty}")
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

    private static void CreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (IOException) when (OperatingSystem.IsWindows())
        {
            RunMklink("/J", linkPath, targetPath);
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        {
            RunMklink("/J", linkPath, targetPath);
        }
    }

    private static void CreateFileLink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (IOException) when (OperatingSystem.IsWindows())
        {
            RunMklink("/H", linkPath, targetPath);
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        {
            RunMklink("/H", linkPath, targetPath);
        }
    }

    private static void RunMklink(string linkType, string linkPath, string targetPath)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add("/c");
        process.StartInfo.ArgumentList.Add("mklink");
        process.StartInfo.ArgumentList.Add(linkType);
        process.StartInfo.ArgumentList.Add(linkPath);
        process.StartInfo.ArgumentList.Add(targetPath);
        if (!process.Start())
        {
            throw new InvalidOperationException("Unable to start the Windows link helper.");
        }

        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            var error = process.StandardError.ReadToEnd();
            throw new IOException($"Unable to create test link: {error}");
        }
    }

    private static void SetupFolder(
        string baseDirectory,
        IEnumerable<string>? directories = null,
        IReadOnlyDictionary<string, string>? files = null)
    {
        foreach (var directory in directories ?? [])
        {
            Directory.CreateDirectory(Path.Combine(baseDirectory, directory));
        }

        foreach (var file in files ?? new Dictionary<string, string>())
        {
            var fullPath = Path.Combine(baseDirectory, file.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, file.Value);
        }
    }

    private sealed class AutocompleteFixture : IDisposable
    {
        public AutocompleteFixture()
        {
            RootDirectory = Path.Combine(Path.GetTempPath(), "pi-autocomplete-root-" + Guid.NewGuid().ToString("N"));
            BaseDirectory = Path.Combine(RootDirectory, "cwd");
            OutsideDirectory = Path.Combine(RootDirectory, "outside");
            Directory.CreateDirectory(BaseDirectory);
            Directory.CreateDirectory(OutsideDirectory);
        }

        public string RootDirectory { get; }

        public string BaseDirectory { get; }

        public string OutsideDirectory { get; }

        public CombinedAutocompleteProvider CreateProvider() => new([], BaseDirectory);

        public CombinedAutocompleteProvider CreateFdProvider() => new([], BaseDirectory, FakeFdPath());

        public void SetupFolder(
            string directory,
            IEnumerable<string>? dirs = null,
            IReadOnlyDictionary<string, string>? files = null)
        {
            if (!Directory.Exists(RootDirectory))
            {
                throw new InvalidOperationException("Fixture root directory is unavailable.");
            }

            AutocompleteTests.SetupFolder(directory, dirs, files);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(RootDirectory))
                {
                    Directory.Delete(RootDirectory, recursive: true);
                }
            }
            catch
            {
                // Test cleanup must not hide the assertion that already ran.
            }
        }
    }

    private sealed class DelegatingAutocompleteProvider(IAutocompleteProvider inner) : IAutocompleteProvider
    {
        public ValueTask<AutocompleteSuggestions?> GetSuggestions(
            IReadOnlyList<string> lines,
            int cursorLine,
            int cursorCol,
            AutocompleteOptions options) =>
            inner.GetSuggestions(lines, cursorLine, cursorCol, options);

        public AutocompleteCompletion ApplyCompletion(
            IReadOnlyList<string> lines,
            int cursorLine,
            int cursorCol,
            AutocompleteItem item,
            string prefix) =>
            inner.ApplyCompletion(lines, cursorLine, cursorCol, item, prefix);
    }
}

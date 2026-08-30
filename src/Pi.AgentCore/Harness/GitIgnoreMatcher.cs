using System.Text;
using System.Text.RegularExpressions;

namespace Pi.AgentCore.Harness;

/// <summary>
/// Small, dependency-free implementation of the gitignore pattern rules used by skill discovery.
/// Rules are evaluated in insertion order and the last matching rule wins.
/// </summary>
public sealed class GitIgnoreMatcher
{
    private readonly List<IgnoreRule> _rules = [];

    /// <summary>Adds gitignore-compatible patterns to the matcher.</summary>
    public void Add(IEnumerable<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        foreach (var pattern in patterns)
        {
            if (TryCreateRule(pattern, out var rule))
            {
                _rules.Add(rule);
            }
        }
    }

    /// <summary>Returns whether a relative path is ignored by the current rules.</summary>
    public bool Ignores(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var isDirectory = path.EndsWith('/') || path.EndsWith('\\');
        var normalized = NormalizePath(path);
        var ignored = false;
        foreach (var rule in _rules)
        {
            if (rule.Matches(normalized, isDirectory))
            {
                ignored = !rule.Negated;
            }
        }

        return ignored;
    }

    private static bool TryCreateRule(string line, out IgnoreRule rule)
    {
        rule = default!;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var trimmed = line.Trim();
        if (trimmed.StartsWith('#') && !trimmed.StartsWith("\\#", StringComparison.Ordinal))
        {
            return false;
        }

        var pattern = line.TrimEnd('\r');
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

        var directoryOnly = pattern.EndsWith('/');
        if (directoryOnly)
        {
            pattern = pattern.TrimEnd('/');
        }

        if (pattern.Length == 0)
        {
            return false;
        }

        var hasSlash = ContainsUnescapedSlash(pattern);
        var expression = BuildExpression(pattern, hasSlash, directoryOnly);
        rule = new IgnoreRule(negated, directoryOnly, new Regex(expression, RegexOptions.CultureInvariant));
        return true;
    }

    private static string NormalizePath(string path)
    {
        var normalized = path.Replace('\\', '/').Trim('/');
        return normalized == "." ? string.Empty : normalized;
    }

    private static bool ContainsUnescapedSlash(string pattern)
    {
        var escaped = false;
        foreach (var character in pattern)
        {
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (character == '\\')
            {
                escaped = true;
            }
            else if (character == '/')
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildExpression(string pattern, bool hasSlash, bool directoryOnly)
    {
        var glob = GlobToRegex(pattern);
        var prefix = hasSlash ? "^" : "(?:^|/)";
        var suffix = directoryOnly ? "(?:/.*)?$" : "(?:/.*)?$";
        return prefix + glob + suffix;
    }

    private static string GlobToRegex(string pattern)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < pattern.Length; index++)
        {
            var character = pattern[index];
            if (character == '\\' && index + 1 < pattern.Length)
            {
                builder.Append(Regex.Escape(pattern[++index].ToString()));
                continue;
            }

            if (character == '*')
            {
                if (index + 1 < pattern.Length && pattern[index + 1] == '*')
                {
                    index++;
                    if (index + 1 < pattern.Length && pattern[index + 1] == '/')
                    {
                        index++;
                        builder.Append("(?:.*/)?");
                    }
                    else
                    {
                        builder.Append(".*");
                    }
                }
                else
                {
                    builder.Append("[^/]*");
                }

                continue;
            }

            if (character == '?')
            {
                builder.Append("[^/]");
                continue;
            }

            if (character == '[')
            {
                var closing = pattern.IndexOf(']', index + 1);
                if (closing > index + 1)
                {
                    var characterClass = pattern[index..(closing + 1)];
                    builder.Append(characterClass);
                    index = closing;
                    continue;
                }
            }

            builder.Append(Regex.Escape(character.ToString()));
        }

        return builder.ToString();
    }

    private sealed record IgnoreRule(bool Negated, bool DirectoryOnly, Regex Regex)
    {
        public bool Matches(string path, bool isDirectory) =>
            (!DirectoryOnly || isDirectory || path.Contains('/', StringComparison.Ordinal)) &&
            Regex.IsMatch(path);
    }
}

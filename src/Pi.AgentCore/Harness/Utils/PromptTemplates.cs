using System.Text;
using System.Text.RegularExpressions;
using Pi.AgentCore.Harness;

namespace Pi.AgentCore.Harness.Utils;

/// <summary>Prompt-template argument parsing and substitution helpers.</summary>
public static partial class PromptTemplateUtilities
{
    /// <summary>Parses a command argument string using simple shell-style quoting.</summary>
    public static IReadOnlyList<string> ParseCommandArgs(string argsString)
    {
        ArgumentNullException.ThrowIfNull(argsString);
        var args = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        foreach (var character in argsString)
        {
            if (quote is not null)
            {
                if (character == quote.Value)
                {
                    quote = null;
                }
                else
                {
                    current.Append(character);
                }
            }
            else if (character is '"' or '\'')
            {
                quote = character;
            }
            else if (character is ' ' or '\t')
            {
                if (current.Length > 0)
                {
                    args.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(character);
            }
        }

        if (current.Length > 0)
        {
            args.Add(current.ToString());
        }

        return args;
    }

    /// <summary>Substitutes positional and shell-style argument placeholders.</summary>
    public static string SubstituteArgs(string content, IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(args);
        var result = Regex.Replace(content, @"\$(\d+)", match =>
        {
            return int.TryParse(match.Groups[1].Value, out var number) && number > 0 && number <= args.Count
                ? args[number - 1]
                : string.Empty;
        }, RegexOptions.CultureInvariant);
        result = Regex.Replace(result, @"\$\{@:(\d+)(?::(\d+))?\}", match =>
        {
            var start = int.TryParse(match.Groups[1].Value, out var value) ? Math.Max(0, value - 1) : 0;
            if (match.Groups[2].Success && int.TryParse(match.Groups[2].Value, out var length))
            {
                return string.Join(' ', args.Skip(start).Take(Math.Max(0, length)));
            }

            return string.Join(' ', args.Skip(start));
        }, RegexOptions.CultureInvariant);
        var allArgs = string.Join(' ', args);
        result = result.Replace("$ARGUMENTS", allArgs, StringComparison.Ordinal);
        return result.Replace("$@", allArgs, StringComparison.Ordinal);
    }

    /// <summary>Formats a prompt-template invocation with positional arguments.</summary>
    public static string FormatPromptTemplateInvocation(
        PromptTemplate template,
        IReadOnlyList<string>? args = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        return SubstituteArgs(template.Content, args ?? []);
    }
}

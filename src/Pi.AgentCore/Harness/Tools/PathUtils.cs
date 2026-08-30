using System.Text;
using Pi.AgentCore.Harness;

namespace Pi.AgentCore.Harness.Tools;

/// <summary>Path resolution helpers shared by built-in tools.</summary>
public static class ToolPathUtilities
{
    private const char _narrowNoBreakSpace = '\u202F';

    /// <summary>Resolves a tool path after normalizing model-generated Unicode spaces and @ prefixes.</summary>
    public static async Task<string> ResolveToolPathAsync(
        ExecutionEnv env,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(path);
        var result = await env.AbsolutePathAsync(NormalizeToolPath(path), cancellationToken).ConfigureAwait(false);
        return Result.GetOrThrow(result);
    }

    /// <summary>Resolves a read path while trying the filesystem spellings used by macOS and model output.</summary>
    public static async Task<string> ResolveReadToolPathAsync(
        ExecutionEnv env,
        string path,
        CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveToolPathAsync(env, path, cancellationToken).ConfigureAwait(false);
        var variants = new[]
        {
            resolved,
            ReplaceAmPmDot(resolved),
            resolved.Normalize(NormalizationForm.FormD),
            resolved.Replace('\'', '\u2019'),
            resolved.Normalize(NormalizationForm.FormD).Replace('\'', '\u2019'),
        };

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var variant in variants)
        {
            if (!seen.Add(variant))
            {
                continue;
            }

            var exists = await env.ExistsAsync(variant, cancellationToken).ConfigureAwait(false);
            if (Result.GetOrThrow(exists))
            {
                return variant;
            }
        }

        return resolved;
    }

    private static string NormalizeToolPath(string path)
    {
        var builder = new StringBuilder(path.Length);
        foreach (var character in path)
        {
            builder.Append(character switch
            {
                '\u00A0' or >= '\u2000' and <= '\u200A' or '\u202F' or '\u205F' or '\u3000' => ' ',
                _ => character,
            });
        }

        var normalized = builder.ToString();
        return normalized.StartsWith('@') ? normalized[1..] : normalized;
    }

    private static string ReplaceAmPmDot(string path)
    {
        var builder = new StringBuilder(path.Length);
        for (var index = 0; index < path.Length; index++)
        {
            if (path[index] == ' ' && index + 3 < path.Length &&
                (path[index + 1] is 'A' or 'a' or 'P' or 'p') &&
                (path[index + 2] is 'M' or 'm') && path[index + 3] == '.')
            {
                builder.Append(_narrowNoBreakSpace);
                builder.Append(path, index + 1, 3);
                index += 3;
                continue;
            }

            builder.Append(path[index]);
        }

        return builder.ToString();
    }
}

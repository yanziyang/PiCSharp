using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace Pi.AgentCore.Harness.Utils;

/// <summary>Default and configurable limits for tool-output truncation.</summary>
public sealed record TruncationOptions
{
    /// <summary>Maximum number of complete lines to return.</summary>
    public int? MaxLines { get; init; }

    /// <summary>Maximum number of UTF-8 bytes to return.</summary>
    public int? MaxBytes { get; init; }
}

/// <summary>Outcome and accounting information for a truncation operation.</summary>
public sealed record TruncationResult
{
    /// <summary>The returned content.</summary>
    [JsonPropertyName("content")]
    public required string Content { get; init; }

    /// <summary>Whether content was truncated.</summary>
    [JsonPropertyName("truncated")]
    public bool Truncated { get; init; }

    /// <summary>The limit that caused truncation, or null when no limit was reached.</summary>
    [JsonPropertyName("truncatedBy")]
    public string? TruncatedBy { get; init; }

    /// <summary>Total number of complete lines in the source.</summary>
    [JsonPropertyName("totalLines")]
    public int TotalLines { get; init; }

    /// <summary>Total UTF-8 byte count of the source.</summary>
    [JsonPropertyName("totalBytes")]
    public int TotalBytes { get; init; }

    /// <summary>Number of complete lines in the returned content.</summary>
    [JsonPropertyName("outputLines")]
    public int OutputLines { get; init; }

    /// <summary>UTF-8 byte count of the returned content.</summary>
    [JsonPropertyName("outputBytes")]
    public int OutputBytes { get; init; }

    /// <summary>Whether the returned tail begins part way through a line.</summary>
    [JsonPropertyName("lastLinePartial")]
    public bool LastLinePartial { get; init; }

    /// <summary>Whether the first source line itself exceeded the head byte limit.</summary>
    [JsonPropertyName("firstLineExceedsLimit")]
    public bool FirstLineExceedsLimit { get; init; }

    /// <summary>Maximum line limit used for the operation.</summary>
    [JsonPropertyName("maxLines")]
    public int MaxLines { get; init; }

    /// <summary>Maximum byte limit used for the operation.</summary>
    [JsonPropertyName("maxBytes")]
    public int MaxBytes { get; init; }
}

/// <summary>Port of Pi's exact head, tail and single-line truncation helpers.</summary>
public static class Truncate
{
    /// <summary>Default maximum number of lines.</summary>
    public const int DefaultMaxLines = 2000;

    /// <summary>Default maximum number of UTF-8 bytes.</summary>
    public const int DefaultMaxBytes = 50 * 1024;

    /// <summary>Maximum number of characters displayed for a grep match line.</summary>
    public const int GrepMaxLineLength = 500;

    private static readonly UTF8Encoding _utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>Formats a byte count using Pi's human-readable units.</summary>
    public static string FormatSize(int bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes}B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{(bytes / 1024d).ToString("F1", CultureInfo.InvariantCulture)}KB";
        }

        return $"{(bytes / (1024d * 1024d)).ToString("F1", CultureInfo.InvariantCulture)}MB";
    }

    /// <summary>Truncates content from the head without returning partial lines.</summary>
    public static TruncationResult TruncateHead(string content, TruncationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        var maxLines = options?.MaxLines ?? DefaultMaxLines;
        var maxBytes = options?.MaxBytes ?? DefaultMaxBytes;
        var totalBytes = _utf8.GetByteCount(content);
        var lines = SplitLinesForCounting(content);
        var totalLines = lines.Count;

        if (totalLines <= maxLines && totalBytes <= maxBytes)
        {
            return Complete(content, totalLines, totalBytes, maxLines, maxBytes);
        }

        var firstLineBytes = _utf8.GetByteCount(lines[0]);
        if (firstLineBytes > maxBytes)
        {
            return new TruncationResult
            {
                Content = string.Empty,
                Truncated = true,
                TruncatedBy = "bytes",
                TotalLines = totalLines,
                TotalBytes = totalBytes,
                OutputLines = 0,
                OutputBytes = 0,
                LastLinePartial = false,
                FirstLineExceedsLimit = true,
                MaxLines = maxLines,
                MaxBytes = maxBytes,
            };
        }

        var output = new List<string>();
        var outputBytes = 0;
        var truncatedBy = "lines";
        for (var index = 0; index < lines.Count && index < maxLines; index++)
        {
            var line = lines[index];
            var lineBytes = _utf8.GetByteCount(line) + (index > 0 ? 1 : 0);
            if (outputBytes + lineBytes > maxBytes)
            {
                truncatedBy = "bytes";
                break;
            }

            output.Add(line);
            outputBytes += lineBytes;
        }

        if (output.Count >= maxLines && outputBytes <= maxBytes)
        {
            truncatedBy = "lines";
        }

        var outputContent = string.Join('\n', output);
        return new TruncationResult
        {
            Content = outputContent,
            Truncated = true,
            TruncatedBy = truncatedBy,
            TotalLines = totalLines,
            TotalBytes = totalBytes,
            OutputLines = output.Count,
            OutputBytes = _utf8.GetByteCount(outputContent),
            LastLinePartial = false,
            FirstLineExceedsLimit = false,
            MaxLines = maxLines,
            MaxBytes = maxBytes,
        };
    }

    /// <summary>Truncates content from the tail, allowing a partial final line when required.</summary>
    public static TruncationResult TruncateTail(string content, TruncationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        var maxLines = options?.MaxLines ?? DefaultMaxLines;
        var maxBytes = options?.MaxBytes ?? DefaultMaxBytes;
        var totalBytes = _utf8.GetByteCount(content);
        var lines = SplitLinesForCounting(content);
        var totalLines = lines.Count;

        if (totalLines <= maxLines && totalBytes <= maxBytes)
        {
            return Complete(content, totalLines, totalBytes, maxLines, maxBytes);
        }

        var output = new List<string>();
        var outputBytes = 0;
        var truncatedBy = "lines";
        var lastLinePartial = false;

        for (var index = lines.Count - 1; index >= 0 && output.Count < maxLines; index--)
        {
            var line = lines[index];
            var lineBytes = _utf8.GetByteCount(line) + (output.Count > 0 ? 1 : 0);
            if (outputBytes + lineBytes > maxBytes)
            {
                truncatedBy = "bytes";
                if (output.Count == 0)
                {
                    var truncatedLine = TruncateStringToBytesFromEnd(line, maxBytes);
                    output.Insert(0, truncatedLine);
                    outputBytes = _utf8.GetByteCount(truncatedLine);
                    lastLinePartial = true;
                }

                break;
            }

            output.Insert(0, line);
            outputBytes += lineBytes;
        }

        if (output.Count >= maxLines && outputBytes <= maxBytes)
        {
            truncatedBy = "lines";
        }

        var outputContent = string.Join('\n', output);
        return new TruncationResult
        {
            Content = outputContent,
            Truncated = true,
            TruncatedBy = truncatedBy,
            TotalLines = totalLines,
            TotalBytes = totalBytes,
            OutputLines = output.Count,
            OutputBytes = _utf8.GetByteCount(outputContent),
            LastLinePartial = lastLinePartial,
            FirstLineExceedsLimit = false,
            MaxLines = maxLines,
            MaxBytes = maxBytes,
        };
    }

    /// <summary>Truncates one line to a character limit and appends Pi's marker.</summary>
    public static (string Text, bool WasTruncated) TruncateLine(
        string line,
        int maxChars = GrepMaxLineLength)
    {
        ArgumentNullException.ThrowIfNull(line);
        return line.Length <= maxChars
            ? (line, false)
            : ($"{line[..maxChars]}... [truncated]", true);
    }

    private static TruncationResult Complete(
        string content,
        int totalLines,
        int totalBytes,
        int maxLines,
        int maxBytes) => new()
        {
            Content = content,
            Truncated = false,
            TruncatedBy = null,
            TotalLines = totalLines,
            TotalBytes = totalBytes,
            OutputLines = totalLines,
            OutputBytes = totalBytes,
            LastLinePartial = false,
            FirstLineExceedsLimit = false,
            MaxLines = maxLines,
            MaxBytes = maxBytes,
        };

    private static List<string> SplitLinesForCounting(string content)
    {
        if (content.Length == 0)
        {
            return [];
        }

        var lines = content.Split('\n').ToList();
        if (content.EndsWith('\n'))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    private static string TruncateStringToBytesFromEnd(string value, int maxBytes)
    {
        if (maxBytes <= 0)
        {
            return string.Empty;
        }

        var outputBytes = 0;
        var start = value.Length;
        var needsReplacement = false;
        for (var index = value.Length; index > 0;)
        {
            var characterStart = index - 1;
            var code = value[characterStart];
            int characterBytes;
            var unpairedSurrogate = false;
            if (char.IsLowSurrogate(code) && characterStart > 0)
            {
                var previous = value[characterStart - 1];
                if (char.IsHighSurrogate(previous))
                {
                    characterStart--;
                    characterBytes = 4;
                }
                else
                {
                    characterBytes = 3;
                    unpairedSurrogate = true;
                }
            }
            else if (char.IsSurrogate(code))
            {
                characterBytes = 3;
                unpairedSurrogate = true;
            }
            else
            {
                characterBytes = code <= 0x7F ? 1 : code <= 0x7FF ? 2 : 3;
            }

            if (outputBytes + characterBytes > maxBytes)
            {
                break;
            }

            outputBytes += characterBytes;
            start = characterStart;
            needsReplacement |= unpairedSurrogate;
            index = characterStart;
        }

        var output = value[start..];
        return needsReplacement ? ReplaceUnpairedSurrogates(output) : output;
    }

    private static string ReplaceUnpairedSurrogates(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var code = value[index];
            if (char.IsHighSurrogate(code))
            {
                if (index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
                {
                    builder.Append(code);
                    builder.Append(value[++index]);
                }
                else
                {
                    builder.Append('\uFFFD');
                }
            }
            else if (char.IsLowSurrogate(code))
            {
                builder.Append('\uFFFD');
            }
            else
            {
                builder.Append(code);
            }
        }

        return builder.ToString();
    }
}

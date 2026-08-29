using System.Runtime.CompilerServices;
using System.Text;

namespace Pi.Ai;

/// <summary>One decoded Server-Sent Events record.</summary>
public sealed record SseEvent(
    string? Event,
    string Data,
    IReadOnlyList<string> RawLines);

/// <summary>Reads provider SSE streams using the pinned Pi framing rules.</summary>
public static class SseReader
{
    /// <summary>
    /// Decodes UTF-8 SSE records. Data fields are joined with LF, comments are ignored, and a
    /// final record without a blank line is flushed at EOF.
    /// </summary>
    public static async IAsyncEnumerable<SseEvent> ReadAsync(
        Stream body,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        using var reader = new StreamReader(
            body,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: true);
        var state = new DecoderState();

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            var decoded = DecodeLine(line, state);
            if (decoded is not null)
            {
                yield return decoded;
            }
        }

        var trailing = Flush(state);
        if (trailing is not null)
        {
            yield return trailing;
        }
    }

    private static SseEvent? DecodeLine(string line, DecoderState state)
    {
        if (line.Length == 0)
        {
            return Flush(state);
        }

        state.RawLines.Add(line);
        if (line[0] == ':')
        {
            return null;
        }

        var delimiterIndex = line.IndexOf(':');
        var fieldName = delimiterIndex < 0 ? line : line[..delimiterIndex];
        var value = delimiterIndex < 0 ? string.Empty : line[(delimiterIndex + 1)..];
        if (value.StartsWith(' '))
        {
            value = value[1..];
        }

        if (fieldName == "event")
        {
            state.Event = value;
        }
        else if (fieldName == "data")
        {
            state.Data.Add(value);
        }

        return null;
    }

    private static SseEvent? Flush(DecoderState state)
    {
        if (state.Event is null && state.Data.Count == 0)
        {
            return null;
        }

        var result = new SseEvent(state.Event, string.Join('\n', state.Data), state.RawLines.ToArray());
        state.Event = null;
        state.Data.Clear();
        state.RawLines.Clear();
        return result;
    }

    private sealed class DecoderState
    {
        public string? Event { get; set; }

        public List<string> Data { get; } = [];

        public List<string> RawLines { get; } = [];
    }
}

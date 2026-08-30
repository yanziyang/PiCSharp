using System.Text;
using Pi.AgentCore.Harness.Utils;
using Xunit;

namespace Pi.AgentCore.Tests.Harness;

public sealed class TruncateTests
{
    private static readonly UTF8Encoding _utf8 = new(false, false);

    [Fact(DisplayName = "counts UTF-8 bytes without Node Buffer")]
    public void Counts_utf8_bytes_without_node_buffer()
    {
        var result = Truncate.TruncateHead("aé🙂\nb", new TruncationOptions { MaxBytes = 100, MaxLines = 10 });

        Assert.False(result.Truncated);
        Assert.Equal(9, result.TotalBytes);
        Assert.Equal(9, result.OutputBytes);
    }

    [Fact(DisplayName = "does not count a trailing newline as an extra line")]
    public void Does_not_count_a_trailing_newline_as_an_extra_line()
    {
        var content = string.Join('\n', Enumerable.Repeat("line", 3)) + '\n';
        var head = Truncate.TruncateHead(content, new TruncationOptions { MaxBytes = 100, MaxLines = 3 });
        var tail = Truncate.TruncateTail(content, new TruncationOptions { MaxBytes = 100, MaxLines = 3 });

        Assert.False(head.Truncated);
        Assert.Equal(3, head.TotalLines);
        Assert.Equal(3, head.OutputLines);
        Assert.False(tail.Truncated);
        Assert.Equal(3, tail.TotalLines);
        Assert.Equal(3, tail.OutputLines);
    }

    [Fact(DisplayName = "truncates head on UTF-8 byte limits without partial lines")]
    public void Truncates_head_on_utf8_byte_limits_without_partial_lines()
    {
        var result = Truncate.TruncateHead("éé\nabc", new TruncationOptions { MaxBytes = 4, MaxLines = 10 });

        Assert.Equal("éé", result.Content);
        Assert.True(result.Truncated);
        Assert.Equal("bytes", result.TruncatedBy);
        Assert.Equal(4, result.OutputBytes);
        Assert.False(result.FirstLineExceedsLimit);
    }

    [Fact(DisplayName = "reports head truncation when the first line exceeds the byte limit")]
    public void Reports_head_truncation_when_the_first_line_exceeds_the_byte_limit()
    {
        var result = Truncate.TruncateHead("éé\nabc", new TruncationOptions { MaxBytes = 3, MaxLines = 10 });

        Assert.Equal(string.Empty, result.Content);
        Assert.True(result.Truncated);
        Assert.Equal("bytes", result.TruncatedBy);
        Assert.True(result.FirstLineExceedsLimit);
    }

    [Fact(DisplayName = "truncates tail on UTF-8 boundaries when only a partial last line fits")]
    public void Truncates_tail_on_utf8_boundaries_when_only_a_partial_last_line_fits()
    {
        var result = Truncate.TruncateTail("aé🙂b", new TruncationOptions { MaxBytes = 5, MaxLines = 10 });

        Assert.Equal("🙂b", result.Content);
        Assert.True(result.Truncated);
        Assert.Equal("bytes", result.TruncatedBy);
        Assert.True(result.LastLinePartial);
        Assert.Equal(5, result.OutputBytes);
    }

    [Fact(DisplayName = "truncates an oversized single line with a trailing newline")]
    public void Truncates_an_oversized_single_line_with_a_trailing_newline()
    {
        var result = Truncate.TruncateTail(
            new string('X', 300_000) + '\n',
            new TruncationOptions { MaxBytes = 1024, MaxLines = 100 });

        Assert.Equal(new string('X', 1024), result.Content);
        Assert.Equal(1024, result.OutputBytes);
        Assert.Equal(1, result.OutputLines);
        Assert.True(result.LastLinePartial);
        Assert.Equal("bytes", result.TruncatedBy);
    }

    [Fact(DisplayName = "drops an oversized trailing character when it cannot fit in tail byte limit")]
    public void Drops_an_oversized_trailing_character_when_it_cannot_fit_in_tail_byte_limit()
    {
        var result = Truncate.TruncateTail("abc🙂", new TruncationOptions { MaxBytes = 3, MaxLines = 10 });

        Assert.Equal(string.Empty, result.Content);
        Assert.True(result.Truncated);
        Assert.Equal("bytes", result.TruncatedBy);
        Assert.True(result.LastLinePartial);
        Assert.Equal(0, result.OutputBytes);
    }

    [Fact(DisplayName = "matches Buffer tail truncation semantics for surrogate edge cases")]
    public void Matches_buffer_tail_truncation_semantics_for_surrogate_edge_cases()
    {
        foreach (var input in new[] { "a\uD83D", "\uDE42b", "a\uDE42b", "\uD83D\uD83D\uDE42", "\uD83D\uDE42\uDE42", "👩‍💻" })
        {
            AssertMatchesBufferTail(input);
        }
    }

    [Fact(DisplayName = "matches Buffer tail truncation semantics across deterministic fuzz cases")]
    public void Matches_buffer_tail_truncation_semantics_across_deterministic_fuzz_cases()
    {
        var alphabet = new[]
        {
            "a", "\u007f", "\u0080", "é", "\u07ff", "\u0800", "中", "\uD7FF", "\uD800", "\uD83D",
            "\uDC00", "\uDE42", "🙂", "\uE000", "\uFFFF",
        };

        void CheckExhaustive(string prefix, int depth)
        {
            AssertMatchesBufferTail(prefix, SampledByteLimits(prefix));
            if (depth == 0)
            {
                return;
            }

            foreach (var character in alphabet)
            {
                CheckExhaustive(prefix + character, depth - 1);
            }
        }

        CheckExhaustive(string.Empty, 3);

        uint seed = 0x12345678;
        double Random()
        {
            seed = unchecked(seed * 1_664_525u + 1_013_904_223u);
            return seed / 4_294_967_296d;
        }

        for (var index = 0; index < 1_000; index++)
        {
            var builder = new StringBuilder();
            var length = (int)Math.Floor(Random() * 80);
            for (var characterIndex = 0; characterIndex < length; characterIndex++)
            {
                builder.Append(alphabet[(int)Math.Floor(Random() * alphabet.Length)]);
            }

            AssertMatchesBufferTail(builder.ToString(), SampledByteLimits(builder.ToString()));
        }
    }

    private static void AssertMatchesBufferTail(string input, int[]? maxByteValues = null)
    {
        var totalBytes = _utf8.GetByteCount(input);
        var values = maxByteValues ?? Enumerable.Range(0, totalBytes + 5).ToArray();
        foreach (var maxBytes in values)
        {
            var result = Truncate.TruncateTail(input, new TruncationOptions { MaxBytes = maxBytes, MaxLines = 10 });
            var expected = BufferTail(input, maxBytes);
            Assert.Equal(expected, result.Content);
            Assert.True(_utf8.GetByteCount(result.Content) <= maxBytes);
        }
    }

    private static int[] SampledByteLimits(string input)
    {
        var totalBytes = _utf8.GetByteCount(input);
        var candidates = new[]
        {
            0, 1, 2, 3, 4, 5, 8, totalBytes / 2 - 1, totalBytes / 2, totalBytes / 2 + 1,
            totalBytes - 8, totalBytes - 5, totalBytes - 4, totalBytes - 3, totalBytes - 2,
            totalBytes - 1, totalBytes, totalBytes + 1, totalBytes + 4,
        };
        return candidates.Where(value => value >= 0).Distinct().OrderBy(value => value).ToArray();
    }

    private static string BufferTail(string input, int maxBytes)
    {
        var bytes = _utf8.GetBytes(input);
        if (bytes.Length <= maxBytes)
        {
            return input;
        }

        var start = bytes.Length - maxBytes;
        while (start < bytes.Length && (bytes[start] & 0xC0) == 0x80)
        {
            start++;
        }

        return _utf8.GetString(bytes, start, bytes.Length - start);
    }
}

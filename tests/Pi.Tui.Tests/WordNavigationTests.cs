using Xunit;

namespace Pi.Tui.Tests;

/// <summary>Ports Pi's Intl word-segmentation navigation cases.</summary>
public sealed class WordNavigationTests
{
    [Fact(DisplayName = "basic words: hello world")]
    public void Backward_BasicWords()
    {
        const string text = "hello world";

        Assert.Equal(6, WordNavigation.FindWordBackward(text, 11));
        Assert.Equal(0, WordNavigation.FindWordBackward(text, 6));
    }

    [Fact(DisplayName = "dotted: foo.bar")]
    public void Backward_DottedWord()
    {
        const string text = "foo.bar";

        Assert.Equal(4, WordNavigation.FindWordBackward(text, 7));
        Assert.Equal(3, WordNavigation.FindWordBackward(text, 4));
        Assert.Equal(0, WordNavigation.FindWordBackward(text, 3));
    }

    [Fact(DisplayName = "colon: foo:bar")]
    public void Backward_ColonWord()
    {
        const string text = "foo:bar";

        Assert.Equal(4, WordNavigation.FindWordBackward(text, 7));
        Assert.Equal(3, WordNavigation.FindWordBackward(text, 4));
        Assert.Equal(0, WordNavigation.FindWordBackward(text, 3));
    }

    [Fact(DisplayName = "path: path/to/file")]
    public void Backward_Path()
    {
        const string text = "path/to/file";

        Assert.Equal(8, WordNavigation.FindWordBackward(text, 12));
        Assert.Equal(7, WordNavigation.FindWordBackward(text, 8));
        Assert.Equal(5, WordNavigation.FindWordBackward(text, 7));
        Assert.Equal(4, WordNavigation.FindWordBackward(text, 5));
        Assert.Equal(0, WordNavigation.FindWordBackward(text, 4));
    }

    [Fact(DisplayName = "CJK mixed")]
    public void Backward_CjkMixed()
    {
        const string text = "你好世界 test";

        Assert.Equal(5, WordNavigation.FindWordBackward(text, text.Length));
        Assert.Equal(2, WordNavigation.FindWordBackward(text, 5));
        Assert.Equal(0, WordNavigation.FindWordBackward(text, 2));
    }

    [Fact(DisplayName = "whitespace at boundaries")]
    public void Backward_WhitespaceAtBoundaries()
    {
        const string text = "  hello  ";

        Assert.Equal(2, WordNavigation.FindWordBackward(text, 9));
        Assert.Equal(0, WordNavigation.FindWordBackward(text, 2));
    }

    [Fact(DisplayName = "punctuation run: foo...bar")]
    public void Backward_PunctuationRun()
    {
        const string text = "foo...bar";

        Assert.Equal(6, WordNavigation.FindWordBackward(text, 9));
        Assert.Equal(3, WordNavigation.FindWordBackward(text, 6));
        Assert.Equal(0, WordNavigation.FindWordBackward(text, 3));
    }

    [Fact(DisplayName = "cursor at 0 returns 0")]
    public void Backward_CursorAtZero()
    {
        Assert.Equal(0, WordNavigation.FindWordBackward("hello", 0));
    }

    [Fact(DisplayName = "basic words: hello world")]
    public void Forward_BasicWords()
    {
        const string text = "hello world";

        Assert.Equal(5, WordNavigation.FindWordForward(text, 0));
        Assert.Equal(11, WordNavigation.FindWordForward(text, 5));
    }

    [Fact(DisplayName = "dotted: foo.bar")]
    public void Forward_DottedWord()
    {
        const string text = "foo.bar";

        Assert.Equal(3, WordNavigation.FindWordForward(text, 0));
        Assert.Equal(4, WordNavigation.FindWordForward(text, 3));
        Assert.Equal(7, WordNavigation.FindWordForward(text, 4));
    }

    [Fact(DisplayName = "colon: foo:bar")]
    public void Forward_ColonWord()
    {
        const string text = "foo:bar";

        Assert.Equal(3, WordNavigation.FindWordForward(text, 0));
        Assert.Equal(4, WordNavigation.FindWordForward(text, 3));
        Assert.Equal(7, WordNavigation.FindWordForward(text, 4));
    }

    [Fact(DisplayName = "path: path/to/file")]
    public void Forward_Path()
    {
        const string text = "path/to/file";

        Assert.Equal(4, WordNavigation.FindWordForward(text, 0));
        Assert.Equal(5, WordNavigation.FindWordForward(text, 4));
        Assert.Equal(7, WordNavigation.FindWordForward(text, 5));
        Assert.Equal(8, WordNavigation.FindWordForward(text, 7));
        Assert.Equal(12, WordNavigation.FindWordForward(text, 8));
    }

    [Fact(DisplayName = "CJK mixed")]
    public void Forward_CjkMixed()
    {
        const string text = "你好世界 test";
        var firstEnd = WordNavigation.FindWordForward(text, 0);

        Assert.True(firstEnd > 0);
        Assert.True(firstEnd <= 4);

        var position = 0;
        while (position < text.Length)
        {
            var next = WordNavigation.FindWordForward(text, position);
            if (next == position)
            {
                break;
            }

            position = next;
        }

        Assert.Equal(text.Length, position);
    }

    [Fact(DisplayName = "whitespace at boundaries")]
    public void Forward_WhitespaceAtBoundaries()
    {
        const string text = "  hello  ";

        Assert.Equal(7, WordNavigation.FindWordForward(text, 0));
        Assert.Equal(9, WordNavigation.FindWordForward(text, 7));
    }

    [Fact(DisplayName = "punctuation run: foo...bar")]
    public void Forward_PunctuationRun()
    {
        const string text = "foo...bar";

        Assert.Equal(3, WordNavigation.FindWordForward(text, 0));
        Assert.Equal(6, WordNavigation.FindWordForward(text, 3));
        Assert.Equal(9, WordNavigation.FindWordForward(text, 6));
    }

    [Fact(DisplayName = "cursor at end returns end")]
    public void Forward_CursorAtEnd()
    {
        Assert.Equal(5, WordNavigation.FindWordForward("hello", 5));
    }

    [Fact(DisplayName = "backward skips word then stops before atomic marker")]
    public void Atomic_BackwardSkipsWordBeforeMarker()
    {
        var (text, options) = CreateAtomicCase();

        Assert.Equal(26, WordNavigation.FindWordBackward(text, text.Length, options));
    }

    [Fact(DisplayName = "backward skips whitespace then atomic marker as one unit")]
    public void Atomic_BackwardSkipsMarkerAsOneUnit()
    {
        var (text, options) = CreateAtomicCase();

        Assert.Equal(6, WordNavigation.FindWordBackward(text, 26, options));
    }

    [Fact(DisplayName = "forward skips atomic marker as one unit")]
    public void Atomic_ForwardSkipsMarkerAsOneUnit()
    {
        var (text, options) = CreateAtomicCase();

        Assert.Equal(6 + "[paste #1 +5 lines]".Length, WordNavigation.FindWordForward(text, 6, options));
    }

    private static (string Text, WordNavigationOptions Options) CreateAtomicCase()
    {
        const string marker = "[paste #1 +5 lines]";
        var text = $"hello {marker} world";
        var segmentMap = new Dictionary<string, WordSegment[]>(StringComparer.Ordinal)
        {
            [text] =
            [
                new WordSegment("hello", true),
                new WordSegment(" ", false),
                new WordSegment(marker, true),
                new WordSegment(" ", false),
                new WordSegment("world", true),
            ],
            [text[..26]] =
            [
                new WordSegment("hello", true),
                new WordSegment(" ", false),
                new WordSegment(marker, true),
                new WordSegment(" ", false),
            ],
            [text[6..]] =
            [
                new WordSegment(marker, true),
                new WordSegment(" ", false),
                new WordSegment("world", true),
            ],
        };

        return (
            text,
            new WordNavigationOptions
            {
                Segment = input => segmentMap.TryGetValue(input, out var segments) ? segments : [],
                IsAtomicSegment = segment => segment == marker,
            });
    }
}

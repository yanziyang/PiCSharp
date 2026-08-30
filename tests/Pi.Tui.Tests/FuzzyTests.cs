using Pi.Tui;

using Xunit;

namespace Pi.Tui.Tests;

/// <summary>Ports the upstream fuzzyMatch and fuzzyFilter cases.</summary>
public sealed class FuzzyTests
{
    [Fact(DisplayName = "empty query matches everything with score 0")]
    public void Match_EmptyQueryMatchesEverythingWithScoreZero()
    {
        var result = Fuzzy.Match("", "anything");

        Assert.True(result.Matches);
        Assert.Equal(0, result.Score);
    }

    [Fact(DisplayName = "query longer than text does not match")]
    public void Match_QueryLongerThanTextDoesNotMatch()
    {
        var result = Fuzzy.Match("longquery", "short");

        Assert.False(result.Matches);
    }

    [Fact(DisplayName = "exact match has good score")]
    public void Match_ExactMatchHasGoodScore()
    {
        var result = Fuzzy.Match("test", "test");

        Assert.True(result.Matches);
        Assert.True(result.Score < 0);
    }

    [Fact(DisplayName = "characters must appear in order")]
    public void Match_CharactersMustAppearInOrder()
    {
        var matchInOrder = Fuzzy.Match("abc", "aXbXc");
        var matchOutOfOrder = Fuzzy.Match("abc", "cba");

        Assert.True(matchInOrder.Matches);
        Assert.False(matchOutOfOrder.Matches);
    }

    [Fact(DisplayName = "case insensitive matching")]
    public void Match_IsCaseInsensitive()
    {
        var result = Fuzzy.Match("ABC", "abc");
        var result2 = Fuzzy.Match("abc", "ABC");

        Assert.True(result.Matches);
        Assert.True(result2.Matches);
    }

    [Fact(DisplayName = "consecutive matches score better than scattered matches")]
    public void Match_ConsecutiveMatchesScoreBetterThanScatteredMatches()
    {
        var consecutive = Fuzzy.Match("foo", "foobar");
        var scattered = Fuzzy.Match("foo", "f_o_o_bar");

        Assert.True(consecutive.Matches);
        Assert.True(scattered.Matches);
        Assert.True(consecutive.Score < scattered.Score);
    }

    [Fact(DisplayName = "word boundary matches score better")]
    public void Match_WordBoundaryMatchesScoreBetter()
    {
        var atBoundary = Fuzzy.Match("fb", "foo-bar");
        var notAtBoundary = Fuzzy.Match("fb", "afbx");

        Assert.True(atBoundary.Matches);
        Assert.True(notAtBoundary.Matches);
        Assert.True(atBoundary.Score < notAtBoundary.Score);
    }

    [Fact(DisplayName = "matches swapped alpha numeric tokens")]
    public void Match_MatchesSwappedAlphaNumericTokens()
    {
        var result = Fuzzy.Match("codex52", "gpt-5.2-codex");

        Assert.True(result.Matches);
    }

    [Fact(DisplayName = "empty query returns all items unchanged")]
    public void Filter_EmptyQueryReturnsAllItemsUnchanged()
    {
        var items = new[] { "apple", "banana", "cherry" };

        var result = Fuzzy.Filter(items, "", static value => value);

        Assert.Same(items, result);
    }

    [Fact(DisplayName = "filters out non-matching items")]
    public void Filter_FiltersOutNonMatchingItems()
    {
        var items = new[] { "apple", "banana", "cherry" };

        var result = Fuzzy.Filter(items, "an", static value => value);

        Assert.Contains("banana", result);
        Assert.DoesNotContain("apple", result);
        Assert.DoesNotContain("cherry", result);
    }

    [Fact(DisplayName = "sorts results by match quality")]
    public void Filter_SortsResultsByMatchQuality()
    {
        var items = new[] { "a_p_p", "app", "application" };

        var result = Fuzzy.Filter(items, "app", static value => value);

        Assert.Equal("app", result[0]);
    }

    [Fact(DisplayName = "prioritizes exact matches over longer prefix matches")]
    public void Filter_PrioritizesExactMatchesOverLongerPrefixMatches()
    {
        var items = new[] { "clone", "cl" };

        var result = Fuzzy.Filter(items, "cl", static value => value);

        Assert.Equal(["cl", "clone"], result);
    }

    [Fact(DisplayName = "works with custom getText function")]
    public void Filter_WorksWithCustomGetTextFunction()
    {
        var items = new[]
        {
            new NamedItem("foo", 1),
            new NamedItem("bar", 2),
            new NamedItem("foobar", 3),
        };

        var result = Fuzzy.Filter(items, "foo", static item => item.Name);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, item => item.Name == "foo");
        Assert.Contains(result, item => item.Name == "foobar");
    }

    [Fact(DisplayName = "matches slash-separated provider/model queries against reordered text")]
    public void Filter_MatchesSlashSeparatedProviderModelQueriesAgainstReorderedText()
    {
        var item = new ModelItem("gpt-5.5", "openai-codex");

        var result = Fuzzy.Filter([item], "openai-codex/gpt-5.5", static model => $"{model.Id} {model.Provider}");

        Assert.Equal([item], result);
    }

    private sealed record NamedItem(string Name, int Id);

    private sealed record ModelItem(string Id, string Provider);
}

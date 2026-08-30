namespace Pi.Tui;

/// <summary>Result of matching a fuzzy query against text.</summary>
public readonly record struct FuzzyMatch(bool Matches, double Score);

/// <summary>Fuzzy matching helpers used by autocomplete providers.</summary>
public static class Fuzzy
{
    private static readonly char[] _tokenSeparators = ['/', ' ', '\t', '\r', '\n', '\f', '\v'];

    /// <summary>Matches the query against text and returns the upstream-compatible score.</summary>
    public static FuzzyMatch Match(string query, string text)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(text);

        var queryLower = query.ToLowerInvariant();
        var textLower = text.ToLowerInvariant();

        FuzzyMatch MatchQuery(string normalizedQuery)
        {
            if (normalizedQuery.Length == 0)
            {
                return new FuzzyMatch(true, 0);
            }

            if (normalizedQuery.Length > textLower.Length)
            {
                return new FuzzyMatch(false, 0);
            }

            var queryIndex = 0;
            var score = 0d;
            var lastMatchIndex = -1;
            var consecutiveMatches = 0;

            for (var textIndex = 0; textIndex < textLower.Length && queryIndex < normalizedQuery.Length; textIndex++)
            {
                if (textLower[textIndex] != normalizedQuery[queryIndex])
                {
                    continue;
                }

                var isWordBoundary = textIndex == 0 || IsWordBoundary(textLower[textIndex - 1]);
                if (lastMatchIndex == textIndex - 1)
                {
                    consecutiveMatches++;
                    score -= consecutiveMatches * 5;
                }
                else
                {
                    consecutiveMatches = 0;
                    if (lastMatchIndex >= 0)
                    {
                        score += (textIndex - lastMatchIndex - 1) * 2;
                    }
                }

                if (isWordBoundary)
                {
                    score -= 10;
                }

                score += textIndex * 0.1;
                lastMatchIndex = textIndex;
                queryIndex++;
            }

            if (queryIndex < normalizedQuery.Length)
            {
                return new FuzzyMatch(false, 0);
            }

            if (normalizedQuery == textLower)
            {
                score -= 100;
            }

            return new FuzzyMatch(true, score);
        }

        var primaryMatch = MatchQuery(queryLower);
        if (primaryMatch.Matches)
        {
            return primaryMatch;
        }

        if (!TryGetSwappedAlphaNumericQuery(queryLower, out var swappedQuery))
        {
            return primaryMatch;
        }

        var swappedMatch = MatchQuery(swappedQuery);
        return swappedMatch.Matches
            ? new FuzzyMatch(true, swappedMatch.Score + 5)
            : primaryMatch;
    }

    /// <summary>Filters and ranks items by fuzzy match quality.</summary>
    public static IReadOnlyList<T> Filter<T>(IReadOnlyList<T> items, string query, Func<T, string> getText)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(getText);

        var trimmedQuery = query.Trim();
        if (trimmedQuery.Length == 0)
        {
            return items;
        }

        var tokens = SplitTokens(trimmedQuery);
        if (tokens.Count == 0)
        {
            return items;
        }

        var results = new List<ScoredItem<T>>();
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var text = getText(item);
            ArgumentNullException.ThrowIfNull(text);

            var totalScore = 0d;
            var allMatch = true;
            foreach (var token in tokens)
            {
                var match = Match(token, text);
                if (!match.Matches)
                {
                    allMatch = false;
                    break;
                }

                totalScore += match.Score;
            }

            if (allMatch)
            {
                results.Add(new ScoredItem<T>(item, totalScore, index));
            }
        }

        results.Sort(static (left, right) =>
        {
            var scoreComparison = left.Score.CompareTo(right.Score);
            return scoreComparison != 0 ? scoreComparison : left.Index.CompareTo(right.Index);
        });

        return results.Select(result => result.Item).ToArray();
    }

    private static bool IsWordBoundary(char character) =>
        char.IsWhiteSpace(character) || character is '-' or '_' or '.' or '/' or ':';

    private static bool TryGetSwappedAlphaNumericQuery(string query, out string swappedQuery)
    {
        swappedQuery = string.Empty;
        if (query.Length < 2)
        {
            return false;
        }

        var firstDigitIndex = 0;
        while (firstDigitIndex < query.Length && query[firstDigitIndex] is >= 'a' and <= 'z')
        {
            firstDigitIndex++;
        }

        if (firstDigitIndex > 0 && firstDigitIndex < query.Length &&
            query[firstDigitIndex..].All(static character => character is >= '0' and <= '9'))
        {
            swappedQuery = query[firstDigitIndex..] + query[..firstDigitIndex];
            return true;
        }

        var firstLetterIndex = 0;
        while (firstLetterIndex < query.Length && query[firstLetterIndex] is >= '0' and <= '9')
        {
            firstLetterIndex++;
        }

        if (firstLetterIndex > 0 && firstLetterIndex < query.Length &&
            query[firstLetterIndex..].All(static character => character is >= 'a' and <= 'z'))
        {
            swappedQuery = query[firstLetterIndex..] + query[..firstLetterIndex];
            return true;
        }

        return false;
    }

    private static List<string> SplitTokens(string query)
    {
        var tokens = new List<string>();
        var tokenStart = 0;
        for (var index = 0; index <= query.Length; index++)
        {
            if (index < query.Length && Array.IndexOf(_tokenSeparators, query[index]) < 0)
            {
                continue;
            }

            if (index > tokenStart)
            {
                tokens.Add(query[tokenStart..index]);
            }

            tokenStart = index + 1;
        }

        return tokens;
    }

    private readonly record struct ScoredItem<T>(T Item, double Score, int Index);
}

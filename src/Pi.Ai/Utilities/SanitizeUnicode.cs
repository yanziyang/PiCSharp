namespace Pi.Ai;

/// <summary>Helpers for removing malformed UTF-16 code units before provider serialization.</summary>
public static class UnicodeUtilities
{
    /// <summary>
    /// Removes unpaired UTF-16 surrogates while preserving valid surrogate pairs.
    /// </summary>
    public static string SanitizeSurrogates(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var firstInvalid = -1;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            var valid = !char.IsSurrogate(character) ||
                        char.IsHighSurrogate(character) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]) ||
                        char.IsLowSurrogate(character) && index > 0 && char.IsHighSurrogate(text[index - 1]);
            if (!valid)
            {
                firstInvalid = index;
                break;
            }

            if (char.IsHighSurrogate(character))
            {
                index++;
            }
        }

        if (firstInvalid < 0)
        {
            return text;
        }

        var builder = new System.Text.StringBuilder(text.Length - 1);
        builder.Append(text, 0, firstInvalid);
        for (var index = firstInvalid + 1; index < text.Length; index++)
        {
            var character = text[index];
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
                {
                    builder.Append(character);
                    builder.Append(text[++index]);
                }

                continue;
            }

            if (!char.IsLowSurrogate(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }
}

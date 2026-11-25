namespace SearchEngine.Sorting;

/// <summary>
/// Builds the key that <see cref="SearchSortMode.NaturalSortAscending"/> compares ordinally, so that
/// <c>file2</c> sorts before <c>file10</c>.
/// </summary>
/// <remarks>
/// <para>The text is split into parts:</para>
/// <list type="number">
/// <item>The characters <c>-</c>, space, <c>_</c> and <c>/</c> separate parts and are dropped.</item>
/// <item>Each run of letters and each run of digits (<see cref="char.IsLetter(char)"/>,
/// <see cref="char.IsDigit(char)"/>) is a part of its own.</item>
/// <item>Any other character (punctuation, symbols) is a one-character text part.</item>
/// </list>
/// <para>The key joins the parts with <c>|</c>. A number part is written as <c>0:</c> followed by the digits
/// zero-padded to 12; a text part as <c>1:</c> followed by its invariant lowercase. So a number sorts before text at
/// the same position, numbers of up to 12 digits compare by value, text compares case-insensitively, and a text
/// whose parts all equal the start of a longer one sorts first. A digit run longer than 12 digits is kept in full and
/// is not ordered by value against numbers of a different length. Numbers that differ only in leading zeros get the
/// same key.</para>
/// </remarks>
public static class NaturalSortKeyBuilder
{
    private const int NumericPadding = 12;
    // Worst-case per input char: single unknown char → "1:x|" = 4 chars.
    // Digit runs are bounded by NumericPadding + 3 ("0:" prefix + padding + "|").
    // Use a conservative multiplier to cover all cases.
    private const int MaxCharsPerInputChar = NumericPadding + 3; // 15

    /// <summary>
    /// Builds the natural sort key of <paramref name="sortText"/>.
    /// </summary>
    /// <param name="sortText">The text to build the key for, usually <see cref="IndexedEntry.SortText"/>.</param>
    /// <returns>The key, or an empty string for a <see langword="null"/> or empty text.</returns>
    /// <remarks>For texts of up to 64 characters the only allocation is the returned string. The method is
    /// thread-safe.</remarks>
    public static string Build(string sortText)
    {
        if (string.IsNullOrEmpty(sortText))
            return string.Empty;

        int maxLen = sortText.Length * MaxCharsPerInputChar;
        // stackalloc for typical names (≤64 chars → buffer ≤960 chars)
        Span<char> buffer = maxLen <= 960
            ? stackalloc char[maxLen]
            : new char[maxLen];

        var span = sortText.AsSpan();
        int pos = 0;
        bool first = true;

        int i = 0;
        while (i < span.Length)
        {
            char c = span[i];

            // Skip separators
            if (IsSeparator(c))
            {
                i++;
                continue;
            }

            if (!first)
                buffer[pos++] = '|';
            first = false;

            if (char.IsDigit(c))
            {
                // Consume digit run
                int start = i;
                while (i < span.Length && char.IsDigit(span[i]))
                    i++;

                // Numeric token: prefix '0:', zero-padded
                buffer[pos++] = '0';
                buffer[pos++] = ':';
                int digitCount = i - start;
                for (int p = digitCount; p < NumericPadding; p++)
                    buffer[pos++] = '0';
                span.Slice(start, digitCount).CopyTo(buffer[pos..]);
                pos += digitCount;
            }
            else if (char.IsLetter(c))
            {
                // Consume letter run
                int start = i;
                while (i < span.Length && char.IsLetter(span[i]))
                    i++;

                // Text token: prefix '1:', lowercased
                buffer[pos++] = '1';
                buffer[pos++] = ':';
                for (int k = start; k < i; k++)
                    buffer[pos++] = char.ToLowerInvariant(span[k]);
            }
            else
            {
                // Unknown character - treat as text token
                buffer[pos++] = '1';
                buffer[pos++] = ':';
                buffer[pos++] = char.ToLowerInvariant(c);
                i++;
            }
        }

        return new string(buffer[..pos]);
    }

    private static bool IsSeparator(char c)
        => c is '-' or ' ' or '_' or '/';
}

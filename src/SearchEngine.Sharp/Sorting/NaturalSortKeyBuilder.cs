namespace SearchEngine.Sorting;

/// <summary>
/// Builds a natural sort key from a string so that ordinal comparison produces natural ordering.
/// </summary>
/// <remarks>
/// Rules:
/// <list type="number">
/// <item>The input is split into tokens at separators (-, space, _, /).</item>
/// <item>Consecutive letters and digits within a segment are split into separate tokens.</item>
/// <item>Numeric tokens sort before text tokens at the same position.</item>
/// <item>Numbers are compared numerically (via zero-padded representation).</item>
/// <item>Text tokens are compared case-insensitively.</item>
/// <item>Shorter token sequences sort before longer ones when all compared tokens are equal.</item>
/// </list>
///
/// Key format: tokens joined by '|', each prefixed with '0:' (number) or '1:' (text).
/// Numbers are zero-padded to 12 digits.
/// </remarks>
public static class NaturalSortKeyBuilder
{
    private const int NumericPadding = 12;
    // Worst-case per input char: single unknown char → "1:x|" = 4 chars.
    // Digit runs are bounded by NumericPadding + 3 ("0:" prefix + padding + "|").
    // Use a conservative multiplier to cover all cases.
    private const int MaxCharsPerInputChar = NumericPadding + 3; // 15

    /// <summary>
    /// Builds a natural sort key for the given sort text.
    /// Uses stackalloc for short inputs to avoid heap allocation.
    /// </summary>
    public static string Build(string sortText)
    {
        if (string.IsNullOrEmpty(sortText))
            return string.Empty;

        int maxLen = sortText.Length * MaxCharsPerInputChar;
        // stackalloc for typical model names (≤64 chars → buffer ≤960 chars)
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

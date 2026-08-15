namespace SearchEngine.CompetitorBenchmarks;

/// <summary>
/// Port of <see cref="SearchEngine.Sorting.NaturalSortKeyBuilder"/> for cross-language parity checks.
/// </summary>
internal static class NaturalSortKey
{
    private const int NumericPadding = 12;
    private const int MaxCharsPerInputChar = NumericPadding + 3;

    internal static string Build(string sortText)
    {
        if (string.IsNullOrEmpty(sortText))
            return string.Empty;

        int maxLen = sortText.Length * MaxCharsPerInputChar;
        Span<char> buffer = maxLen <= 960 ? stackalloc char[maxLen] : new char[maxLen];
        ReadOnlySpan<char> span = sortText.AsSpan();
        int pos = 0;
        bool first = true;
        int i = 0;

        while (i < span.Length)
        {
            char c = span[i];
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
                int start = i;
                while (i < span.Length && char.IsDigit(span[i]))
                    i++;

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
                int start = i;
                while (i < span.Length && char.IsLetter(span[i]))
                    i++;

                buffer[pos++] = '1';
                buffer[pos++] = ':';
                for (int k = start; k < i; k++)
                    buffer[pos++] = char.ToLowerInvariant(span[k]);
            }
            else
            {
                buffer[pos++] = '1';
                buffer[pos++] = ':';
                buffer[pos++] = char.ToLowerInvariant(c);
                i++;
            }
        }

        return new string(buffer[..pos]);
    }

    private static bool IsSeparator(char c) => c is '-' or ' ' or '_' or '/';
}

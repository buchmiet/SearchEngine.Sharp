namespace SearchEngine.Sorting;

/// <summary>
/// Builds the key that <see cref="SearchSortMode.NaturalSortAscending"/> compares ordinally, so that
/// <c>file2</c> sorts before <c>file10</c> and <c>a 2</c> before <c>ab</c>.
/// </summary>
/// <remarks>
/// <para>The text is split into parts:</para>
/// <list type="number">
/// <item>The characters <c>-</c>, space, <c>_</c> and <c>/</c> separate parts and are dropped.</item>
/// <item>Each run of letters and each run of digits (<see cref="char.IsLetter(char)"/>,
/// <see cref="char.IsDigit(char)"/>) is a part of its own.</item>
/// <item>Any other character (punctuation, symbols) is a one-character text part.</item>
/// </list>
/// <para>Two keys compare like their texts compared part by part, from the first part:</para>
/// <list type="bullet">
/// <item>A number part comes before a text part.</item>
/// <item>Two number parts compare by value, whatever their length; leading zeros do not count, so <c>007</c> and
/// <c>7</c> are equal. A digit of another script counts with its value (<c>٣</c> is 3).</item>
/// <item>Two text parts compare character by character after <see cref="char.ToLowerInvariant(char)"/>, by UTF-16
/// code unit, so the comparison ignores case and does not depend on the culture. A part that is the start of
/// the other comes first: <c>a</c> before <c>ab</c>, so <c>a 2</c> sorts before <c>ab</c>.</item>
/// <item>When all parts of one text equal the first parts of the other, the text with fewer parts comes first.</item>
/// </list>
/// <para>Texts that differ only in case, in leading zeros or in which separator they use get the same key.</para>
/// </remarks>
public static class NaturalSortKeyBuilder
{
    // Key layout, compared with string.CompareOrdinal:
    //   number part: NumberTag, the count of significant digits, the digits '0'..'9' without leading zeros
    //   text part:   TextTag, the lowercase characters, PartEnd
    // NumberTag < TextTag puts numbers before text. PartEnd is below every character, so a text part that is the
    // start of another ends first and sorts first. Equal counts compare the digits, so numbers compare by value.
    // A count of 0xFFFF digits or more is written as LongCount followed by the count in two code units.
    private const char PartEnd = '\0';
    private const char NumberTag = '\u0001';
    private const char TextTag = '\u0002';
    private const char LongCount = (char)0xFFFF;

    // A key has at most three code units per input character: a one-digit number or a one-character text part.
    private const int MaxKeyCharsPerInputChar = 3;
    private const int MaxStackKeyLength = 1024;

    /// <summary>
    /// Builds the natural sort key of <paramref name="sortText"/>.
    /// </summary>
    /// <param name="sortText">The text to build the key for, usually <see cref="IndexedEntry.SortText"/>.</param>
    /// <returns>The key, or an empty string for a <see langword="null"/> or empty text. The key is meant only for
    /// ordinal comparison with other keys; it is not readable text.</returns>
    /// <remarks>For texts of up to 341 characters the only allocation is the returned string. The method is
    /// thread-safe.</remarks>
    public static string Build(string sortText)
    {
        if (string.IsNullOrEmpty(sortText))
            return string.Empty;

        int maxLength = checked(sortText.Length * MaxKeyCharsPerInputChar);
        Span<char> buffer = maxLength <= MaxStackKeyLength
            ? stackalloc char[maxLength]
            : new char[maxLength];

        ReadOnlySpan<char> text = sortText;
        int pos = 0;
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (IsSeparator(c))
            {
                i++;
            }
            else if (char.IsDigit(c))
            {
                int start = i;
                while (i < text.Length && char.IsDigit(text[i]))
                    i++;

                pos = AppendNumber(text[start..i], buffer, pos);
            }
            else
            {
                buffer[pos++] = TextTag;
                if (char.IsLetter(c))
                {
                    while (i < text.Length && char.IsLetter(text[i]))
                        buffer[pos++] = char.ToLowerInvariant(text[i++]);
                }
                else
                {
                    buffer[pos++] = char.ToLowerInvariant(c);
                    i++;
                }

                buffer[pos++] = PartEnd;
            }
        }

        return new string(buffer[..pos]);
    }

    private static int AppendNumber(ReadOnlySpan<char> digits, Span<char> buffer, int pos)
    {
        int first = 0;
        while (first < digits.Length && DigitValue(digits[first]) == 0)
            first++;

        int count = digits.Length - first;
        buffer[pos++] = NumberTag;
        if (count < LongCount)
        {
            buffer[pos++] = (char)count;
        }
        else
        {
            buffer[pos++] = LongCount;
            buffer[pos++] = (char)(count >> 16);
            buffer[pos++] = (char)count;
        }

        for (int d = first; d < digits.Length; d++)
            buffer[pos++] = (char)('0' + DigitValue(digits[d]));

        return pos;
    }

    // char.IsDigit is true for decimal digits of every script; their numeric value is 0 to 9.
    private static int DigitValue(char c)
        => char.IsAsciiDigit(c) ? c - '0' : (int)char.GetNumericValue(c);

    private static bool IsSeparator(char c)
        => c is '-' or ' ' or '_' or '/';
}

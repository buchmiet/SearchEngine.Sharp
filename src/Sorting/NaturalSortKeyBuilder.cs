using SearchEngine.Tokenizer;

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
/// <item>Any other character (punctuation, symbols) is a one-character text part; so is each half of a surrogate
/// pair.</item>
/// </list>
/// <para>Two keys compare like their texts compared part by part, from the first part:</para>
/// <list type="bullet">
/// <item>A number part comes before a text part.</item>
/// <item>Two number parts compare by value, whatever their length; leading zeros do not count, so <c>007</c> and
/// <c>7</c> are equal. A digit of another script counts with its value (<c>٣</c> is 3).</item>
/// <item>Two text parts compare character by character after <see cref="TextNormalizer.ToLowerInvariantFast(char)"/>
/// (Unicode 17.0.0 simple lowercase), by UTF-16 code unit, so the comparison ignores case and depends neither on the
/// culture nor on the platform. A part that is the start of the other comes first: <c>a</c> before <c>ab</c>, so
/// <c>a 2</c> sorts before <c>ab</c>.</item>
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

    // The longest key Build writes on the stack; callers of BuildInto use it for their own stack buffers. Build keeps
    // texts of up to 341 characters on the stack (341 = MaxStackKeyLength / MaxKeyCharsPerInputChar, rounded down), a
    // number quoted in the XML doc of Build.
    internal const int MaxStackKeyLength = 1024;

    // The buffer length BuildInto needs for a text of this length.
    internal static int MaxKeyLength(int textLength) => checked(textLength * MaxKeyCharsPerInputChar);

    /// <summary>
    /// Builds the natural sort key of <paramref name="sortText"/>.
    /// </summary>
    /// <param name="sortText">The text to build the key for, usually <see cref="IndexedEntry.SortText"/>.</param>
    /// <returns>The key, or an empty string for an empty text. The key is meant only for ordinal comparison with
    /// other keys; it is not readable text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sortText"/> is <see langword="null"/>.</exception>
    /// <remarks>For texts of up to 341 characters the only allocation is the returned string. Safe to call from
    /// several threads at once.</remarks>
    public static string Build(string sortText)
    {
        ArgumentNullException.ThrowIfNull(sortText);
        if (sortText.Length == 0)
            return string.Empty;

        int maxLength = MaxKeyLength(sortText.Length);
        Span<char> buffer = maxLength <= MaxStackKeyLength
            ? stackalloc char[maxLength]
            : new char[maxLength];

        return new(buffer[..BuildInto(sortText, buffer)]);
    }

    // Writes the key of text to buffer, which holds at least MaxKeyLength(text.Length) characters, and returns
    // its length. Comparing two written keys with SequenceCompareTo orders them like string.CompareOrdinal on the
    // keys Build returns.
    internal static int BuildInto(ReadOnlySpan<char> text, Span<char> buffer)
    {
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
                        buffer[pos++] = TextNormalizer.ToLowerInvariantFast(text[i++]);
                }
                else
                {
                    buffer[pos++] = TextNormalizer.ToLowerInvariantFast(c);
                    i++;
                }

                buffer[pos++] = PartEnd;
            }
        }

        return pos;
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

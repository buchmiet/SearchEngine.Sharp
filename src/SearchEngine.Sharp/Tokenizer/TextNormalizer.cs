using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SearchEngine.Tokenizer;

public static class TextNormalizer
{
    public delegate void UniqueWordAction<TState>(ref TState state, string word);

    private static readonly SearchValues<char> DefaultSeparatorValues = SearchSeparators.IndexText;
    private static readonly char[] AsciiLowercaseMap = BuildAsciiLowercaseMap();
    private const int MaxStackWordLength = 256;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSeparator(char ch) => IsSeparator(ch, DefaultSeparatorValues);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSeparator(char ch, SearchValues<char> separators) => separators.Contains(ch);

    public static List<string> GetWordsFromString(string text)
        => GetWordsFromString(text, DefaultSeparatorValues);

    public static List<string> GetWordsFromString(string text, SearchValues<char> separators)
    {
        var words = new List<string>();
        var uniqueWords = new HashSet<string>(StringComparer.Ordinal);
        ForEachUniqueWord(text, uniqueWords, separators, ref words, static (ref List<string> list, string word) => list.Add(word));

        CollectionsMarshal.AsSpan(words).Sort(static (a, b) => b.Length.CompareTo(a.Length));

        return words;
    }

    public static void ForEachUniqueWord<TState>(
        string text,
        HashSet<string> uniqueWords,
        ref TState state,
        UniqueWordAction<TState> action)
        => ForEachUniqueWord(text, uniqueWords, DefaultSeparatorValues, ref state, action);

    public static void ForEachUniqueWord<TState>(
        string text,
        HashSet<string> uniqueWords,
        SearchValues<char> separators,
        ref TState state,
        UniqueWordAction<TState> action)
    {
        if (uniqueWords.Comparer != StringComparer.Ordinal && uniqueWords.Comparer != EqualityComparer<string>.Default)
            throw new ArgumentException("The set must compare words ordinally (StringComparer.Ordinal).", nameof(uniqueWords));

        uniqueWords.Clear();
        if (string.IsNullOrEmpty(text))
            return;

        var span = text.AsSpan();
        var lookup = uniqueWords.GetAlternateLookup<ReadOnlySpan<char>>();

        int wordStart = -1;

        for (int i = 0; i < span.Length; i++)
        {
            if (separators.Contains(span[i]))
            {
                EmitWordIfUnique(text, wordStart, i, uniqueWords, lookup, ref state, action);
                wordStart = -1;
            }
            else if (wordStart < 0)
            {
                wordStart = i;
            }
        }

        EmitWordIfUnique(text, wordStart, span.Length, uniqueWords, lookup, ref state, action);
    }

    private static void EmitWordIfUnique<TState>(
        string text,
        int wordStart,
        int wordEnd,
        HashSet<string> uniqueWords,
        HashSet<string>.AlternateLookup<ReadOnlySpan<char>> lookup,
        ref TState state,
        UniqueWordAction<TState> action)
    {
        if (wordStart < 0)
            return;

        // Look up the lowercase form, as it is stored: a case-insensitive comparison of the raw token would
        // treat tokens that stay distinct after lowercasing (final and medial sigma, micro sign and mu) as one,
        // and miss tokens that only become equal by lowercasing (ẞ and ß, Kelvin sign and k).
        var wordSpan = text.AsSpan(wordStart, wordEnd - wordStart);
        string word;
        if (IsNormalized(wordSpan))
        {
            if (lookup.Contains(wordSpan))
                return;

            // The whole text is one token (always so under SearchTokenization.FileMask) and is already
            // lowercase: reuse the caller's string instead of allocating an identical copy.
            word = wordStart == 0 && wordEnd == text.Length ? text : wordSpan.ToString();
        }
        else if (wordSpan.Length <= MaxStackWordLength)
        {
            Span<char> lower = stackalloc char[wordSpan.Length];
            ToLowerInvariantFast(wordSpan, lower);
            if (lookup.Contains(lower))
                return;

            word = lower.ToString();
        }
        else
        {
            word = CreateNormalizedWord(wordSpan);
            if (uniqueWords.Contains(word))
                return;
        }

        uniqueWords.Add(word);
        action(ref state, word);
    }

    public static string CreateNormalizedWord(ReadOnlySpan<char> source)
    {
        return string.Create(source.Length, source, static (destination, state) => ToLowerInvariantFast(state, destination));
    }

    // Per UTF-16 code unit, like ToLowerInvariantFast(char). MemoryExtensions.ToLowerInvariant and ToLowerOrdinal are not
    // equivalent: both also lowercase supplementary-plane letters, which only UnicodeMatching.CaseFold does here.
    internal static void ToLowerInvariantFast(ReadOnlySpan<char> source, Span<char> destination)
    {
        for (int i = 0; i < source.Length; i++)
            destination[i] = ToLowerInvariantFast(source[i]);
    }

    private static bool IsNormalized(ReadOnlySpan<char> word)
    {
        if (System.Text.Ascii.IsValid(word))
            return !word.ContainsAnyInRange('A', 'Z');

        foreach (char ch in word)
        {
            if (ToLowerInvariantFast(ch) != ch)
                return false;
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static char ToLowerInvariantFast(char ch)
    {
        return ch <= 0x7F ? AsciiLowercaseMap[ch] : char.ToLowerInvariant(ch);
    }

    private static char[] BuildAsciiLowercaseMap()
    {
        var map = new char[128];
        for (int i = 0; i < map.Length; i++)
            map[i] = char.ToLowerInvariant((char)i);

        return map;
    }
}

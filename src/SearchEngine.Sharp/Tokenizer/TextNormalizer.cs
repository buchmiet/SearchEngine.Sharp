using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SearchEngine.Tokenizer;

public static class TextNormalizer
{
    public delegate void UniqueWordAction<TState>(ref TState state, string word);

    private static readonly SearchValues<char> DefaultSeparatorValues = SearchSeparators.IndexText;
    private static readonly char[] AsciiLowercaseMap = BuildAsciiLowercaseMap();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSeparator(char ch) => IsSeparator(ch, DefaultSeparatorValues);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSeparator(char ch, SearchValues<char> separators) => separators.Contains(ch);

    public static List<string> GetWordsFromString(string text)
        => GetWordsFromString(text, DefaultSeparatorValues);

    public static List<string> GetWordsFromString(string text, SearchValues<char> separators)
    {
        var words = new List<string>();
        var uniqueWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                EmitWordIfUnique(span, wordStart, i, uniqueWords, lookup, ref state, action);
                wordStart = -1;
            }
            else if (wordStart < 0)
            {
                wordStart = i;
            }
        }

        EmitWordIfUnique(span, wordStart, span.Length, uniqueWords, lookup, ref state, action);
    }

    private static void EmitWordIfUnique<TState>(
        ReadOnlySpan<char> text,
        int wordStart,
        int wordEnd,
        HashSet<string> uniqueWords,
        HashSet<string>.AlternateLookup<ReadOnlySpan<char>> lookup,
        ref TState state,
        UniqueWordAction<TState> action)
    {
        if (wordStart < 0)
            return;

        var wordSpan = text[wordStart..wordEnd];
        if (lookup.Contains(wordSpan))
            return;

        var word = CreateNormalizedWord(wordSpan);
        uniqueWords.Add(word);
        action(ref state, word);
    }

    public static string CreateNormalizedWord(ReadOnlySpan<char> source)
    {
        return string.Create(source.Length, source, static (destination, state) =>
        {
            for (int i = 0; i < state.Length; i++)
                destination[i] = ToLowerInvariantFast(state[i]);
        });
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

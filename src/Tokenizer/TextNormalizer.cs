using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SearchEngine.Tokenizer;

/// <summary>
/// Splits text into tokens at separator characters and lowercases them character by character with
/// <see cref="ToLowerInvariantFast(char)"/>, the way the index builder does.
/// </summary>
/// <remarks>
/// The overloads without a separator set use the index separators of <see cref="SearchTokenization.Default"/>; for
/// another preset pass <c>SearchValues.Create(tokenization.IndexSeparators)</c>. The Unicode options of a preset are
/// not applied here. All members are safe to call from several threads at once.
/// </remarks>
public static class TextNormalizer
{
    /// <summary>Receives one distinct lowercase token from
    /// <see cref="ForEachUniqueWord{TState}(string, HashSet{string}, ref TState, UniqueWordAction{TState})"/>.
    /// </summary>
    /// <typeparam name="TState">The type of the caller's state.</typeparam>
    /// <param name="state">The caller's state, by reference.</param>
    /// <param name="word">The lowercase token.</param>
    public delegate void UniqueWordAction<TState>(ref TState state, string word);

    private static readonly SearchValues<char> DefaultSeparatorValues = SearchSeparators.IndexText;
    private static readonly char[] AsciiLowercaseMap = BuildAsciiLowercaseMap();
    private const int MaxStackWordLength = 256;

    /// <summary>Returns whether <paramref name="ch"/> is an index separator of
    /// <see cref="SearchTokenization.Default"/>.</summary>
    /// <param name="ch">The character to test.</param>
    /// <returns><see langword="true"/> when <paramref name="ch"/> splits tokens.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSeparator(char ch) => IsSeparator(ch, DefaultSeparatorValues);

    /// <summary>Returns whether <paramref name="ch"/> is one of <paramref name="separators"/>.</summary>
    /// <param name="ch">The character to test.</param>
    /// <param name="separators">The separator set.</param>
    /// <returns><see langword="true"/> when <paramref name="ch"/> splits tokens.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="separators"/> is <see langword="null"/>.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSeparator(char ch, SearchValues<char> separators)
    {
        ArgumentNullException.ThrowIfNull(separators);
        return separators.Contains(ch);
    }

    /// <summary>
    /// Returns the distinct lowercase tokens of <paramref name="text"/>, split at the index separators of
    /// <see cref="SearchTokenization.Default"/>, longest first.
    /// </summary>
    /// <param name="text">The text to split. An empty text gives an empty list.</param>
    /// <returns>A new list of tokens.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public static List<string> GetWordsFromString(string text)
        => GetWordsFromString(text, DefaultSeparatorValues);

    /// <summary>
    /// Returns the distinct lowercase tokens of <paramref name="text"/>, split at <paramref name="separators"/>,
    /// longest first.
    /// </summary>
    /// <param name="text">The text to split. An empty text gives an empty list.</param>
    /// <param name="separators">The characters that split tokens.</param>
    /// <returns>A new list of tokens.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> or <paramref name="separators"/> is
    /// <see langword="null"/>.</exception>
    public static List<string> GetWordsFromString(string text, SearchValues<char> separators)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(separators);
        var words = new List<string>();
        var uniqueWords = new HashSet<string>(StringComparer.Ordinal);
        ForEachUniqueWord(text, uniqueWords, separators, ref words, static (ref List<string> list, string word) => list.Add(word));

        CollectionsMarshal.AsSpan(words).Sort(static (a, b) => b.Length.CompareTo(a.Length));

        return words;
    }

    /// <summary>
    /// Calls <paramref name="action"/> once for each distinct lowercase token of <paramref name="text"/>, split at
    /// the index separators of <see cref="SearchTokenization.Default"/>, in order of first occurrence.
    /// </summary>
    /// <typeparam name="TState">The type of <paramref name="state"/>.</typeparam>
    /// <param name="text">The text to split. An empty text calls nothing.</param>
    /// <param name="uniqueWords">A set that compares ordinally. It is cleared first and holds the tokens
    /// afterwards.</param>
    /// <param name="state">State passed to every call of <paramref name="action"/>, by reference.</param>
    /// <param name="action">The callback.</param>
    /// <exception cref="ArgumentNullException"><paramref name="text"/>, <paramref name="uniqueWords"/> or
    /// <paramref name="action"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="uniqueWords"/> does not compare ordinally.</exception>
    public static void ForEachUniqueWord<TState>(
        string text,
        HashSet<string> uniqueWords,
        ref TState state,
        UniqueWordAction<TState> action)
        => ForEachUniqueWord(text, uniqueWords, DefaultSeparatorValues, ref state, action);

    /// <summary>
    /// Calls <paramref name="action"/> once for each distinct lowercase token of <paramref name="text"/>, split at
    /// <paramref name="separators"/>, in order of first occurrence.
    /// </summary>
    /// <typeparam name="TState">The type of <paramref name="state"/>.</typeparam>
    /// <param name="text">The text to split. An empty text calls nothing.</param>
    /// <param name="uniqueWords">A set that compares ordinally (<see cref="StringComparer.Ordinal"/> or the default
    /// comparer). It is cleared first and holds the tokens afterwards.</param>
    /// <param name="separators">The characters that split tokens.</param>
    /// <param name="state">State passed to every call of <paramref name="action"/>, by reference.</param>
    /// <param name="action">The callback.</param>
    /// <exception cref="ArgumentNullException"><paramref name="text"/>, <paramref name="uniqueWords"/> or
    /// <paramref name="action"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="uniqueWords"/> does not compare ordinally.</exception>
    /// <remarks>A token that is already lowercase and makes up the whole text is passed as <paramref name="text"/>
    /// itself; every other new token allocates one string.</remarks>
    public static void ForEachUniqueWord<TState>(
        string text,
        HashSet<string> uniqueWords,
        SearchValues<char> separators,
        ref TState state,
        UniqueWordAction<TState> action)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(uniqueWords);
        ArgumentNullException.ThrowIfNull(separators);
        ArgumentNullException.ThrowIfNull(action);
        if (uniqueWords.Comparer != StringComparer.Ordinal && uniqueWords.Comparer != EqualityComparer<string>.Default)
            throw new ArgumentException("The set must compare words ordinally (StringComparer.Ordinal).", nameof(uniqueWords));

        uniqueWords.Clear();
        if (text.Length == 0)
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

    /// <summary>Returns a lowercase copy of <paramref name="source"/>, converted character by character with
    /// <see cref="ToLowerInvariantFast(char)"/>.</summary>
    /// <param name="source">The characters to copy.</param>
    /// <returns>A new string of the same length.</returns>
    public static string CreateNormalizedWord(ReadOnlySpan<char> source)
    {
        return string.Create(source.Length, source, static (destination, state) => ToLowerInvariantFast(state, destination));
    }

    // Per UTF-16 code unit, like ToLowerInvariantFast(char). MemoryExtensions.ToLowerInvariant is not equivalent: it
    // follows the casing data of the runtime and also lowercases supplementary-plane letters, which only
    // UnicodeMatching.CaseFold does here.
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

    /// <summary>Returns the Unicode 17.0.0 simple lowercase (Simple_Lowercase_Mapping) of a UTF-16 code unit, the same
    /// on every platform and globalization mode, using a table for ASCII. A surrogate stays as it is.</summary>
    /// <param name="ch">The character to convert.</param>
    /// <returns>The lowercase character.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static char ToLowerInvariantFast(char ch)
    {
        return ch <= 0x7F ? AsciiLowercaseMap[ch] : (char)UnicodeCasing.ToLower(ch);
    }

    private static char[] BuildAsciiLowercaseMap()
    {
        var map = new char[128];
        for (int i = 0; i < map.Length; i++)
            map[i] = (char)UnicodeCasing.ToLower(i);

        return map;
    }
}

using System.Globalization;

namespace SearchEngine.Sharp.Tests;

// TextUnitBoundaries (src/UnicodeMatching.cs) walks a token once for its grapheme clusters and answers every
// boundary test from that walk; GraphemeBoundaries matching for Within and globs relies on it. It must answer exactly
// like the reference below, which walks the token from its start with StringInfo for every test.
public class TextUnitBoundariesTests
{
    private static readonly string[] Pool =
    [
        "a", "Z", "1", " ", "\r", "\n", "e\u0301", "\u0E01", "\u0E34", "\u0E48", "\u0E33", "\u1100", "\u1161", "\u11A8",
        "\uD83D\uDC68", "\u200D", "\uD83C\uDDF5", "\uD83C\uDDF1", "\uFE0F", "\uD83C\uDFFD", "\u0915", "\u094D", "\u0937",
        "\uD800", "\uDC00", "\u0600", "\u0903",
    ];

    private static bool ReferenceIsBoundary(string word, int position)
    {
        if (position == 0 || position >= word.Length)
            return true;

        char before = word[position - 1];
        char after = word[position];
        if (char.IsAscii(before) && char.IsAscii(after))
            return before != '\r' || after != '\n';

        int boundary = 0;
        while (boundary < position)
            boundary += StringInfo.GetNextTextElementLength(word.AsSpan(boundary));

        return boundary == position;
    }

    private static int ReferenceIndexOf(string word, string value)
    {
        for (int index = word.IndexOf(value, StringComparison.Ordinal); index >= 0; index = word.IndexOf(value, index + 1, StringComparison.Ordinal))
        {
            if (ReferenceIsBoundary(word, index) && ReferenceIsBoundary(word, index + value.Length))
                return index;
        }

        return -1;
    }

    private static string[] GenerateWords(int count, int maxParts, int seed)
    {
        var random = new Random(seed);
        var words = new string[count];
        for (int i = 0; i < count; i++)
            words[i] = string.Concat(Enumerable.Range(0, random.Next(1, maxParts)).Select(_ => Pool[random.Next(Pool.Length)]));

        return words;
    }

    // Short words get their table on the stack, words of 256 units and more rent it: both sizes are covered.
    [Test]
    [Arguments(2_000, 12)]
    [Arguments(60, 400)]
    public async Task IsBoundary_answers_like_the_walk_from_the_start(int count, int maxParts)
    {
        var mismatches = new List<string>();
        foreach (string word in GenerateWords(count, maxParts, seed: count))
        {
            bool[] actual = Boundaries(word);
            for (int position = 0; position <= word.Length; position++)
            {
                if (actual[position] != ReferenceIsBoundary(word, position))
                    mismatches.Add($"{Escape(word)} at {position}");
            }
        }

        await Assert.That(mismatches).IsEmpty().Because(string.Join("\n", mismatches.Take(20)));
    }

    // Positions are tested out of order, as a glob that backtracks does.
    private static bool[] Boundaries(string word)
    {
        var map = new TextUnitBoundaries(word, graphemeClusters: true, word.Length < TextUnitBoundaries.MaxStackLength ? stackalloc bool[word.Length + 1] : default);
        try
        {
            var result = new bool[word.Length + 1];
            for (int position = word.Length; position >= 0; position -= 2)
                result[position] = map.IsBoundary(position);
            for (int position = 1 - word.Length % 2; position <= word.Length; position += 2)
                result[position] = map.IsBoundary(position);
            return result;
        }
        finally
        {
            map.Dispose();
        }
    }

    [Test]
    public async Task IndexOf_with_clusters_finds_the_same_occurrence_as_the_walk_from_the_start()
    {
        var random = new Random(11);
        var mismatches = new List<string>();
        foreach (string word in GenerateWords(3_000, 40, seed: 3))
        {
            int start = random.Next(word.Length);
            string value = word.Substring(start, random.Next(1, Math.Min(6, word.Length - start) + 1));
            int expected = ReferenceIndexOf(word, value);
            int actual = TextUnitBoundaries.IndexOf(word, value, graphemeClusters: true);
            if (actual != expected)
                mismatches.Add($"{Escape(value)} in {Escape(word)}: {actual}, expected {expected}");
        }

        await Assert.That(mismatches).IsEmpty().Because(string.Join("\n", mismatches.Take(20)));
    }

    private static string Escape(string s) => string.Concat(s.Select(c => c < 128 && !char.IsControl(c) ? c.ToString() : $"\\u{(int)c:X4}"));
}

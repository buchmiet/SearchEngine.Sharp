using System.Text;

namespace SearchEngine.Sharp.Benchmarks;

public sealed record BenchScenario(string Name, int DocumentCount, int VocabularySize, int QueryCount);
public sealed record BenchDocument(int Id, string Text, string[] Terms, long SizeBytes = 0, long ModifiedTicks = 0);
public sealed record BenchData(
    IReadOnlyList<BenchDocument> Documents,
    IReadOnlyList<string> ExactQueries,
    IReadOnlyList<string> InfixQueries,
    IReadOnlyList<string> GlobQueries,
    IReadOnlyList<string> BooleanQueries);

public static class SyntheticDataFactory
{
    private static readonly char[] Vowels = ['a', 'e', 'i', 'o', 'u', 'y'];
    private static readonly char[] Consonants = ['b', 'c', 'd', 'f', 'g', 'h', 'j', 'k', 'l', 'm', 'n', 'p', 'r', 's', 't', 'v', 'w', 'z'];

    public static BenchData Create(BenchScenario scenario, int seed)
    {
        var rng = new Random(seed);
        var vocabulary = BuildVocabulary(scenario.VocabularySize, rng);
        Shuffle(vocabulary, rng);

        int hotCount = Math.Max(64, scenario.VocabularySize / 20);
        var documents = new List<BenchDocument>(scenario.DocumentCount);

        for (int id = 0; id < scenario.DocumentCount; id++)
        {
            var terms = new string[32];
            for (int i = 0; i < terms.Length; i++)
                terms[i] = PickWord(vocabulary, hotCount, rng);
            documents.Add(new BenchDocument(id, string.Join(' ', terms), terms));
        }

        return new BenchData(
            documents,
            BuildExactQueries(documents, scenario.QueryCount, rng),
            BuildInfixQueries(documents, scenario.QueryCount, rng),
            BuildGlobQueries(documents, scenario.QueryCount, rng),
            BuildBooleanQueries(documents, scenario.QueryCount, rng));
    }

    public static BenchData CreateWithFacets(BenchScenario scenario, int seed)
    {
        var rng = new Random(seed ^ 0x5FAC);
        var vocabulary = BuildVocabulary(scenario.VocabularySize, rng);
        Shuffle(vocabulary, rng);

        int hotCount = Math.Max(64, scenario.VocabularySize / 20);
        var documents = new List<BenchDocument>(scenario.DocumentCount);
        long nowTicks = DateTime.UtcNow.Ticks;

        for (int id = 0; id < scenario.DocumentCount; id++)
        {
            var terms = new string[32];
            for (int i = 0; i < terms.Length; i++)
                terms[i] = PickWord(vocabulary, hotCount, rng);

            long sizeBytes = rng.Next(256, 4_194_304);
            long modifiedTicks = nowTicks - rng.Next(0, 90) * TimeSpan.TicksPerDay;
            documents.Add(new BenchDocument(id, string.Join(' ', terms), terms, sizeBytes, modifiedTicks));
        }

        return new BenchData(
            documents,
            BuildExactQueries(documents, scenario.QueryCount, rng),
            BuildInfixQueries(documents, scenario.QueryCount, rng),
            BuildGlobQueries(documents, scenario.QueryCount, rng),
            BuildBooleanQueries(documents, scenario.QueryCount, rng));
    }

    private static string[] BuildVocabulary(int size, Random rng)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        while (set.Count < size)
            set.Add(MakeWord(rng));
        return [.. set];
    }

    private static string MakeWord(Random rng)
    {
        int len = rng.Next(5, 11);
        var sb = new StringBuilder(len);
        bool vowel = rng.Next(2) == 0;
        for (int i = 0; i < len; i++)
        {
            var src = vowel ? Vowels : Consonants;
            sb.Append(src[rng.Next(src.Length)]);
            vowel = !vowel;
        }
        return sb.ToString();
    }

    private static string PickWord(string[] vocab, int hotCount, Random rng)
        => rng.NextDouble() < 0.70 ? vocab[rng.Next(hotCount)] : vocab[rng.Next(vocab.Length)];

    private static IReadOnlyList<string> BuildExactQueries(IReadOnlyList<BenchDocument> docs, int count, Random rng)
    {
        var result = new List<string>(count);
        while (result.Count < count)
        {
            var doc = docs[rng.Next(docs.Count)];
            result.Add(doc.Terms[rng.Next(doc.Terms.Length)]);
        }
        return result;
    }

    private static IReadOnlyList<string> BuildInfixQueries(IReadOnlyList<BenchDocument> docs, int count, Random rng)
    {
        var result = new List<string>(count);
        while (result.Count < count)
        {
            var doc = docs[rng.Next(docs.Count)];
            var word = doc.Terms[rng.Next(doc.Terms.Length)];
            if (word.Length < 5) continue;
            int start = rng.Next(1, word.Length - 3);
            int maxLen = Math.Min(4, word.Length - start - 1);
            int len = rng.Next(2, maxLen + 1);
            result.Add(word.Substring(start, len));
        }
        return result;
    }

    private static IReadOnlyList<string> BuildGlobQueries(IReadOnlyList<BenchDocument> docs, int count, Random rng)
    {
        var result = new List<string>(count);
        while (result.Count < count)
        {
            var doc = docs[rng.Next(docs.Count)];
            var word = doc.Terms[rng.Next(doc.Terms.Length)];
            if (word.Length < 4) continue;

            result.Add(rng.Next(3) switch
            {
                0 => $"{word[..3]}*",
                1 => $"*{word[^3..]}",
                _ => $"{word[0]}?{word[^1]}"
            });
        }

        return result;
    }

    private static IReadOnlyList<string> BuildBooleanQueries(IReadOnlyList<BenchDocument> docs, int count, Random rng)
    {
        var result = new List<string>(count);
        while (result.Count < count)
        {
            var doc = docs[rng.Next(docs.Count)];
            var a = doc.Terms[rng.Next(doc.Terms.Length)];
            var b = doc.Terms[rng.Next(doc.Terms.Length)];
            var c = doc.Terms[rng.Next(doc.Terms.Length)];
            result.Add(rng.Next(3) switch
            {
                0 => $"{a} AND {b}",
                1 => $"{a} OR {b}",
                _ => $"{a} AND NOT {c}"
            });
        }
        return result;
    }

    private static void Shuffle(string[] arr, Random rng)
    {
        for (int i = arr.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (arr[i], arr[j]) = (arr[j], arr[i]);
        }
    }
}

namespace SearchEngine.Text;

/// <summary>
/// Pool for canonicalizing word strings.
/// Ensures the same word across multiple documents shares a single string instance.
/// </summary>
/// <remarks>
/// During index build, thousands of documents may contain the same word. Without
/// canonicalization each occurrence produces a separate string object on the heap.
/// The pool returns the first-seen instance for any given word, so all references
/// to, say, "apple" point to the same object — halving dictionary memory for
/// vocabularies with high document coverage.
/// </remarks>
internal sealed class WordStringPool
{
    private readonly Dictionary<string, string> _pool = new(StringComparer.Ordinal);

    public string Canonicalize(string word)
    {
        if (_pool.TryGetValue(word, out var existing))
            return existing;

        _pool[word] = word;
        return word;
    }

    public int Count => _pool.Count;
}

namespace SearchEngine.Text;

// Canonicalizes token strings, so the same token in many documents shares one string instance.
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
}

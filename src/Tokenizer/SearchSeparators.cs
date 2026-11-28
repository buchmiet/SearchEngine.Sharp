using System.Buffers;

namespace SearchEngine.Tokenizer;

// The index separator set of SearchTokenization.Default.
internal static class SearchSeparators
{
    public static SearchValues<char> IndexText => SearchTokenization.Default.IndexSeparatorValues;
}

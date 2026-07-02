using System.Buffers;

namespace SearchEngine.Tokenizer;

/// <summary>
/// Precomputed separator sets for the <see cref="SearchTokenization.Default"/> preset.
/// </summary>
internal static class SearchSeparators
{
    /// <summary>Separators used when tokenizing indexed document text (Default preset).</summary>
    public static SearchValues<char> IndexText => SearchTokenization.Default.IndexSeparatorValues;

    /// <summary>Separators used when parsing search query expressions (Default preset).</summary>
    public static SearchValues<char> QueryExpression => SearchTokenization.Default.QuerySeparatorValues;
}

using System.Buffers;

namespace SearchEngine.Tokenizer;

/// <summary>
/// Precomputed separator sets for index tokenization and query expression parsing.
/// </summary>
internal static class SearchSeparators
{
    /// <summary>Separators used when tokenizing indexed document text.</summary>
    public static SearchValues<char> IndexText { get; } =
        SearchValues.Create(" .,;:/\\\r\n\t|-#[]{}()~£$€");

    /// <summary>Separators used when parsing search query expressions.</summary>
    public static SearchValues<char> QueryExpression { get; } =
        SearchValues.Create("| -./\n\r\\,#[]{}()~:;$£€");
}

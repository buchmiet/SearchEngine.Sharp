using System.Buffers;
using System.Text.RegularExpressions;
using SearchEngine.Query;
using SearchEngine.Tokenizer;

namespace SearchEngine;

/// <summary>
/// Matches one text (typically a file name) against a query without building an index.
/// <see cref="IsMatch"/> returns the same answer as <see cref="ISearchEngine.Find(string, WordMatchMethod, bool)"/>
/// on an index that contains only that text, built with the same <see cref="SearchTokenization"/>:
/// the same boolean grammar, glob rules, exact / within / regex semantics and Unicode options.
/// </summary>
/// <remarks>
/// Use it for one-shot searches that read every name once anyway (a directory scan): filtering names while
/// they are enumerated is cheaper than indexing them (see docs/file-search-guide.md). Instances are immutable
/// and safe for concurrent use. Facet criteria are not part of the expression; apply them to the metadata
/// you already have.
/// </remarks>
public sealed class NameMatcher
{
    private readonly SearchTokenization _tokenization;
    private readonly WordMatchMethod _method;
    private readonly QueryExpressionEvaluator.QueryToken[]? _postfix;
    private readonly Regex? _regex;

    // Glob terms compiled once, by position in _postfix; null for operators and for terms without metacharacters.
    private readonly GlobMatcher.Shape?[]? _globs;

    private NameMatcher(SearchTokenization tokenization, WordMatchMethod method, QueryExpressionEvaluator.QueryToken[]? postfix, Regex? regex)
    {
        _tokenization = tokenization;
        _method = method;
        _postfix = postfix;
        _regex = regex;
        if (postfix is not null)
        {
            _globs = new GlobMatcher.Shape?[postfix.Length];
            for (int i = 0; i < postfix.Length; i++)
            {
                string word = postfix[i].Word;
                if (postfix[i].Operation == WordOperations.None && GlobMatcher.ContainsMetacharacters(word))
                    _globs[i] = new GlobMatcher.Shape(word, tokenization.GraphemeBoundaries);
            }
        }
    }

    /// <summary>Compiles <paramref name="expression"/> once for many <see cref="IsMatch"/> calls.</summary>
    /// <param name="expression">Query as passed to <c>Find</c>.</param>
    /// <param name="method">Match method as passed to <c>Find</c>.</param>
    /// <param name="enableOperators">As passed to <c>Find</c>.</param>
    /// <param name="tokenization">Preset of the index this matcher stands in for; <see cref="SearchTokenization.Default"/> when null.</param>
    public static NameMatcher Create(string expression, WordMatchMethod method, bool enableOperators = false, SearchTokenization? tokenization = null)
    {
        ArgumentNullException.ThrowIfNull(expression);
        tokenization ??= SearchTokenization.Default;
        if (string.IsNullOrWhiteSpace(expression))
            return new NameMatcher(tokenization, method, null, null);

        if (method == WordMatchMethod.Regex)
        {
            RegexPatternCache.TryGet(expression, out var regex); // invalid pattern: null, never matches
            return new NameMatcher(tokenization, method, null, regex);
        }

        var postfix = QueryExpressionEvaluator.ToPostfix(expression, enableOperators, tokenization);
        return new NameMatcher(tokenization, method, postfix?.ToArray(), null);
    }

    /// <summary>True when an index holding only <paramref name="text"/> would return it for this query.</summary>
    public bool IsMatch(ReadOnlySpan<char> text)
    {
        if (_postfix is null && _regex is null)
            return false;

        string? folded = null;
        if (_tokenization.UnicodeMatching != UnicodeMatching.None && !System.Text.Ascii.IsValid(text))
        {
            folded = _tokenization.FoldIndexText(text.ToString());
            text = folded;
        }

        // As in the index, tokens are split on the separators of the (folded) text and then lowercased: a
        // separator that lowercasing would create or remove does not move a token boundary.
        char[]? rented = text.Length > 256 ? ArrayPool<char>.Shared.Rent(text.Length) : null;
        Span<char> lower = rented is null ? stackalloc char[text.Length] : rented.AsSpan(0, text.Length);
        TextNormalizer.ToLowerInvariantFast(text, lower);

        try
        {
            return _regex is not null ? MatchRegex(text, lower) : Evaluate(text, lower);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<char>.Shared.Return(rented);
        }
    }

    // Every token is tried: the index returns nothing when the pattern times out on any word, so a timeout after
    // an earlier hit is still no match.
    private bool MatchRegex(ReadOnlySpan<char> text, ReadOnlySpan<char> lower)
    {
        bool matched = false;
        try
        {
            foreach (var range in Tokens(text))
                matched |= _regex!.IsMatch(lower[range]);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }

        return matched;
    }

    private bool Evaluate(ReadOnlySpan<char> text, ReadOnlySpan<char> lower)
    {
        var postfix = _postfix!;
        Span<bool> stack = postfix.Length <= 128 ? stackalloc bool[postfix.Length] : new bool[postfix.Length];
        var algebra = new TextAlgebra(this, text, lower);
        return QueryExpressionEvaluator.Evaluate<bool, TextAlgebra>(postfix, ref algebra, stack);
    }

    // The terms of one text: true when a token of the text matches the term. A text without tokens is still a
    // document, so "*" and NOT terms match it.
    private readonly ref struct TextAlgebra(NameMatcher matcher, ReadOnlySpan<char> text, ReadOnlySpan<char> lower) : QueryExpressionEvaluator.IQueryAlgebra<bool>
    {
        private readonly ReadOnlySpan<char> _text = text;
        private readonly ReadOnlySpan<char> _lower = lower;

        public bool Match(int position, string word) => matcher.MatchTerm(position, word, _text, _lower);
        public bool Not(bool operand) => !operand;
        public bool And(bool left, bool right) => left & right;
        public bool Or(bool left, bool right) => left | right;
    }

    private bool MatchTerm(int position, string word, ReadOnlySpan<char> text, ReadOnlySpan<char> lower)
    {
        if (word.Length == 1 && word[0] == '*')
            return true;

        var compiled = _globs![position];
        bool glob = compiled.HasValue;
        var shape = compiled.GetValueOrDefault();
        foreach (var range in Tokens(text))
        {
            var token = lower[range];
            if (glob)
            {
                if (shape.IsMatch(token)) return true;
            }
            else if (_method == WordMatchMethod.Exact)
            {
                if (token.SequenceEqual(word)) return true;
            }
            else if (QueryMatcher.ContainsWithin(token, word, _tokenization.GraphemeBoundaries))
            {
                return true;
            }
        }

        return false;
    }

    private TokenEnumerator Tokens(ReadOnlySpan<char> text) => new(text, _tokenization.IndexSeparatorValues);

    private ref struct TokenEnumerator(ReadOnlySpan<char> text, SearchValues<char> separators)
    {
        private readonly ReadOnlySpan<char> _text = text;
        private int _position;

        public Range Current { get; private set; }

        public readonly TokenEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            while (_position < _text.Length && separators.Contains(_text[_position]))
                _position++;

            if (_position >= _text.Length)
                return false;

            int start = _position;
            int length = _text[start..].IndexOfAny(separators);
            _position = length < 0 ? _text.Length : start + length;
            Current = start.._position;
            return true;
        }
    }
}

using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SearchEngine;
using SearchEngine.Index;
using SearchEngine.Pooling;
using SearchEngine.Snapshots;
using SearchEngine.Tokenizer;

namespace SearchEngine.Query;

internal static class QueryExpressionEvaluator
{
    // Fast path: if the expression contains exactly one word (no operators, no multiple
    // tokens), skip the full tokenize → postfix → evaluate pipeline and go straight to
    // QueryMatcher.Match. This is the common case for simple type-ahead search.
    internal static bool TryGetSingleWord(
        ReadOnlySpan<char> expression,
        SearchTokenization tokenization,
        out string? word)
    {
        word = null;
        var separators = tokenization.QuerySeparatorValues;

        int start = expression.IndexOfAnyExcept(separators);
        if (start < 0)
            return false;

        int length = expression[start..].IndexOfAny(separators);
        if (length >= 0 && expression[(start + length)..].IndexOfAnyExcept(separators) >= 0)
            return false;

        var term = tokenization.FoldQueryTerm(length < 0 ? expression[start..] : expression.Slice(start, length));
        if (term.IsEmpty || term.ContainsAny(separators))
            return false; // folded to nothing, or to several terms

        word = TextNormalizer.CreateNormalizedWord(term);
        return true;
    }

    /// <summary>
    /// Returns true when the expression evaluates to a single word operand, with or without
    /// <paramref name="enableOperators"/>. Multi-word, NOT, and parenthesized expressions return false.
    /// </summary>
    internal static bool TryGetSingleSemanticWord(
        ReadOnlySpan<char> expression,
        bool enableOperators,
        SearchTokenization tokenization,
        out string? word)
    {
        if (!enableOperators)
            return TryGetSingleWord(expression, tokenization, out word);

        word = null;
        var tokens = Tokenize(expression, enableOperators: true, tokenization);
        if (tokens.Count != 1 || tokens[0].Operation != WordOperations.None)
            return false;

        word = tokens[0].Word;
        return !string.IsNullOrEmpty(word);
    }

    internal static FastBitSet? Evaluate(
        string expression,
        bool enableOperators,
        WordMatchMethod method,
        QueryContext qc,
        IndexSnapshot snapshot)
    {
        var postfix = ToPostfix(expression, enableOperators, snapshot.Tokenization);
        if (postfix is null)
            return null;

        var algebra = new IndexAlgebra(method, qc, snapshot);
        return Evaluate<FastBitSet, IndexAlgebra>(CollectionsMarshal.AsSpan(postfix), ref algebra, new FastBitSet[postfix.Count]);
    }

    /// <summary>
    /// What the operands of an expression are and how the operators combine them: a document set per term for an
    /// index (<see cref="IndexAlgebra"/>), a match flag per term for one text (<see cref="NameMatcher"/>).
    /// </summary>
    internal interface IQueryAlgebra<TValue>
    {
        // position: index of the term in the postfix expression, for algebras that compile terms in advance.
        TValue Match(int position, string word);
        TValue Not(TValue operand);
        TValue And(TValue left, TValue right);
        TValue Or(TValue left, TValue right);
    }

    /// <summary>
    /// Evaluates a postfix expression from <see cref="ToPostfix"/>. <paramref name="stack"/> holds at least
    /// <c>postfix.Length</c> values. The expression is well formed, so every operator finds its operands.
    /// </summary>
    internal static TValue Evaluate<TValue, TAlgebra>(ReadOnlySpan<QueryToken> postfix, ref TAlgebra algebra, scoped Span<TValue> stack)
        where TAlgebra : IQueryAlgebra<TValue>, allows ref struct
    {
        int count = 0;
        for (int position = 0; position < postfix.Length; position++)
        {
            var token = postfix[position];
            switch (token.Operation)
            {
                case WordOperations.None:
                    stack[count++] = algebra.Match(position, token.Word);
                    break;
                case WordOperations.Not:
                    stack[count - 1] = algebra.Not(stack[count - 1]);
                    break;
                case WordOperations.And:
                    count--;
                    stack[count - 1] = algebra.And(stack[count - 1], stack[count]);
                    break;
                case WordOperations.Or:
                    count--;
                    stack[count - 1] = algebra.Or(stack[count - 1], stack[count]);
                    break;
            }
        }

        Debug.Assert(count == 1);
        return stack[0];
    }

    // Document sets of an index snapshot. AND / OR reuse the left operand; NOT copies the all-documents set
    // rented once per evaluation.
    private struct IndexAlgebra(WordMatchMethod method, QueryContext qc, IndexSnapshot snapshot) : IQueryAlgebra<FastBitSet>
    {
        private FastBitSet? _allRecords;

        public readonly FastBitSet Match(int position, string word) => QueryMatcher.Match(word, method, qc, snapshot);

        public FastBitSet Not(FastBitSet operand)
        {
            _allRecords ??= qc.RentAllTrueBitSet();
            var result = qc.RentCopyOf(_allRecords);
            result.ExceptWith(operand);
            return result;
        }

        public readonly FastBitSet And(FastBitSet left, FastBitSet right)
        {
            left.IntersectWith(right);
            return left;
        }

        public readonly FastBitSet Or(FastBitSet left, FastBitSet right)
        {
            left.UnionWith(right);
            return left;
        }
    }

    // Splits the expression into a well-formed infix token list (see Append); empty when nothing is left.
    private static List<QueryToken> Tokenize(
        ReadOnlySpan<char> expression,
        bool enableOperators,
        SearchTokenization tokenization)
    {
        var separators = tokenization.QuerySeparatorValues;
        var tokens = new List<QueryToken>();
        int wordStart = -1;
        // Both parentheses are separators here, so counting the characters counts the tokens: the token list is
        // balanced whenever it gets parenthesis tokens at all.
        bool groups = enableOperators && tokenization.ParenthesesGroup && HasValidParentheses(expression);

        for (int i = 0; i < expression.Length; i++)
        {
            char ch = expression[i];
            if (!separators.Contains(ch))
            {
                if (wordStart < 0)
                    wordStart = i;

                continue;
            }

            EmitWord(expression, wordStart, i, enableOperators, tokenization, tokens);
            wordStart = -1;

            if (groups && ch is '(' or ')')
            {
                Append(tokens, QueryToken.CreateOperation(
                    ch == '(' ? WordOperations.OpenParenthesis : WordOperations.CloseParenthesis));
            }
        }

        EmitWord(expression, wordStart, expression.Length, enableOperators, tokenization, tokens);
        DropTrailingOperators(tokens);
        return tokens;
    }

    private static void EmitWord(
        ReadOnlySpan<char> expression,
        int wordStart,
        int wordEnd,
        bool enableOperators,
        SearchTokenization tokenization,
        List<QueryToken> tokens)
    {
        if (wordStart < 0)
            return;

        var wordSpan = expression[wordStart..wordEnd];
        if (enableOperators && TryGetOperator(wordSpan, out var operation))
        {
            Append(tokens, QueryToken.CreateOperation(operation));
            return;
        }

        AddTerms(tokenization.FoldQueryTerm(wordSpan), tokenization.QuerySeparatorValues, tokens);
    }

    // Operators are recognized on the word as typed; folding comes after, so a word that only folds to and/or/not
    // (nöt, ånd) stays a term. A folded term can contain a query separator (U+037E GREEK QUESTION MARK composes to
    // ';'): it is split as the folded index text is, and the parts are joined by implicit AND like adjacent terms.
    private static void AddTerms(ReadOnlySpan<char> folded, SearchValues<char> separators, List<QueryToken> tokens)
    {
        while (!folded.IsEmpty)
        {
            int end = folded.IndexOfAny(separators);
            var term = end < 0 ? folded : folded[..end];
            if (!term.IsEmpty)
                Append(tokens, QueryToken.CreateWord(TextNormalizer.CreateNormalizedWord(term)));

            folded = end < 0 ? default : folded[(end + 1)..];
        }
    }

    // Appends one token so that the list stays a well-formed infix expression (malformed input is corrected, not
    // rejected; docs/query-semantics.md, query parsing rule 5). The start of the expression and of every group
    // behave alike:
    //   - a term, "(" or NOT right after a term or ")" gets an implicit AND in front ("cat dog" = "cat AND dog");
    //   - a binary operator at the start is dropped;
    //   - NOT NOT counts as one NOT; a binary operator followed by NOT is kept (NOT binds to what follows);
    //   - any other pair of adjacent operators collapses to AND, which is then checked against the token before it
    //     ("a AND NOT AND b" = "a AND b", "(NOT AND a)" = "(a)");
    //   - ")" drops the operators in front of it, and an empty group is dropped with the implicit AND in front of
    //     it, so what follows the group meets what preceded it ("a () OR b" = "a OR b").
    // Trailing operators at the end of the expression are dropped by DropTrailingOperators once all tokens are in.
    private static void Append(List<QueryToken> tokens, QueryToken token)
    {
        var last = tokens.Count > 0 ? tokens[^1].Operation : WordOperations.OpenParenthesis;
        bool afterOperand = last is WordOperations.None or WordOperations.CloseParenthesis;

        switch (token.Operation)
        {
            case WordOperations.None:
            case WordOperations.OpenParenthesis:
            case WordOperations.Not:
                if (afterOperand)
                    tokens.Add(new(string.Empty, WordOperations.And, Implicit: token.Operation == WordOperations.OpenParenthesis));
                else if (token.Operation == WordOperations.Not && last == WordOperations.Not)
                    return;

                tokens.Add(token);
                return;

            case WordOperations.And:
            case WordOperations.Or:
                if (afterOperand)
                {
                    tokens.Add(token);
                }
                else if (last != WordOperations.OpenParenthesis)
                {
                    tokens.RemoveAt(tokens.Count - 1);
                    Append(tokens, QueryToken.CreateOperation(WordOperations.And));
                }

                return;

            case WordOperations.CloseParenthesis:
                DropTrailingOperators(tokens);
                if (tokens[^1].Operation != WordOperations.OpenParenthesis)
                {
                    tokens.Add(token);
                    return;
                }

                // An empty group is dropped together with the AND inserted for it, so "a () OR b" = "a OR b". An AND
                // that was typed stays, as it would without the group.
                tokens.RemoveAt(tokens.Count - 1);
                if (tokens.Count > 0 && tokens[^1].Implicit)
                    tokens.RemoveAt(tokens.Count - 1);
                return;
        }
    }

    private static void DropTrailingOperators(List<QueryToken> tokens)
    {
        while (tokens.Count > 0 && tokens[^1].Operation is WordOperations.And or WordOperations.Or or WordOperations.Not)
            tokens.RemoveAt(tokens.Count - 1);
    }

    /// <summary>
    /// Parses an expression into postfix order (Dijkstra's shunting-yard algorithm): operands as words, operators in
    /// the order they apply. NOT binds tighter than AND, AND tighter than OR; equal precedence applies left to
    /// right. Returns <see langword="null"/> when no operand is left after correction. Shared by the index
    /// (<see cref="Evaluate(string, bool, WordMatchMethod, QueryContext, IndexSnapshot)"/>) and
    /// <see cref="NameMatcher"/>.
    /// </summary>
    internal static List<QueryToken>? ToPostfix(string expression, bool enableOperators, SearchTokenization tokenization)
    {
        var tokens = Tokenize(expression.AsSpan(), enableOperators, tokenization);
        if (tokens.Count == 0)
            return null;

        var output = new List<QueryToken>(tokens.Count);
        var operators = new List<WordOperations>(tokens.Count);
        foreach (var token in tokens)
        {
            switch (token.Operation)
            {
                case WordOperations.None:
                    output.Add(token);
                    break;
                case WordOperations.OpenParenthesis:
                case WordOperations.Not: // prefix operator: applies once the operand that follows is out
                    operators.Add(token.Operation);
                    break;
                case WordOperations.CloseParenthesis:
                    PopOperators(operators, output, WordOperations.None);
                    operators.RemoveAt(operators.Count - 1); // the matching "("
                    break;
                default:
                    PopOperators(operators, output, token.Operation);
                    operators.Add(token.Operation);
                    break;
            }
        }

        PopOperators(operators, output, WordOperations.None);
        return output;
    }

    // Moves pending operators of the current group to the output, down to the first one that binds less tightly
    // than `incoming` (all of them when `incoming` is None).
    private static void PopOperators(List<WordOperations> operators, List<QueryToken> output, WordOperations incoming)
    {
        while (operators.Count > 0
               && operators[^1] != WordOperations.OpenParenthesis
               && GetPrecedence(operators[^1]) >= GetPrecedence(incoming))
        {
            output.Add(QueryToken.CreateOperation(operators[^1]));
            operators.RemoveAt(operators.Count - 1);
        }
    }

    private static int GetPrecedence(WordOperations operation)
    {
        return operation switch
        {
            WordOperations.Or => 1,
            WordOperations.And => 2,
            WordOperations.Not => 3,
            _ => 0
        };
    }

    private static bool HasValidParentheses(ReadOnlySpan<char> expression)
    {
        int openCount = 0;

        for (int i = 0; i < expression.Length; i++)
        {
            if (expression[i] == '(')
            {
                openCount++;
            }
            else if (expression[i] == ')')
            {
                openCount--;
                if (openCount < 0)
                    return false;
            }
        }

        return openCount == 0;
    }

    private static bool TryGetOperator(ReadOnlySpan<char> token, out WordOperations operation)
    {
        operation = token.Length switch
        {
            2 when EqualsIgnoreCaseAscii(token, "or") => WordOperations.Or,
            3 when EqualsIgnoreCaseAscii(token, "and") => WordOperations.And,
            3 when EqualsIgnoreCaseAscii(token, "not") => WordOperations.Not,
            _ => WordOperations.None
        };

        return operation != WordOperations.None;
    }

    private static bool EqualsIgnoreCaseAscii(ReadOnlySpan<char> token, ReadOnlySpan<char> expected)
    {
        if (token.Length != expected.Length)
            return false;

        for (int i = 0; i < token.Length; i++)
        {
            if (TextNormalizer.ToLowerInvariantFast(token[i]) != expected[i])
                return false;
        }

        return true;
    }

    // Implicit: an AND that Append inserted in front of "(" and takes back when the group turns out empty.
    internal readonly record struct QueryToken(string Word, WordOperations Operation, bool Implicit = false)
    {
        public static QueryToken CreateWord(string word) => new(word, WordOperations.None);
        public static QueryToken CreateOperation(WordOperations operation) => new(string.Empty, operation);
    }
}

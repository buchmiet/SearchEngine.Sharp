using System.Buffers;
using SearchEngine;
using SearchEngine.Index;
using SearchEngine.Pooling;
using SearchEngine.Snapshots;
using SearchEngine.Tokenizer;

namespace SearchEngine.Query;

internal static class QueryExpressionEvaluator
{
    // Fast path: if the expression contains exactly one word (no operators, no multiple
    // tokens), skip the full tokenize → correct → evaluate pipeline and go straight to
    // QueryMatcher.Match. This is the common case for simple type-ahead search.
    internal static bool TryGetSingleWord(
        ReadOnlySpan<char> expression,
        SearchValues<char> separators,
        out string? word)
    {
        word = null;

        int wordStart = -1;
        bool hasWord = false;

        for (int i = 0; i < expression.Length; i++)
        {
            if (separators.Contains(expression[i]))
            {
                if (wordStart < 0)
                    continue;

                if (hasWord)
                    return false;

                word = TextNormalizer.CreateNormalizedWord(expression[wordStart..i]);
                hasWord = true;
                wordStart = -1;
                continue;
            }

            if (wordStart < 0)
                wordStart = i;
        }

        if (wordStart >= 0)
        {
            if (hasWord)
                return false;

            word = TextNormalizer.CreateNormalizedWord(expression[wordStart..]);
            hasWord = true;
        }

        return hasWord;
    }

    /// <summary>
    /// Returns true when the expression evaluates to a single word operand — with or without
    /// <paramref name="enableOperators"/>. Multi-word, NOT, and parenthesized expressions return false.
    /// </summary>
    internal static bool TryGetSingleSemanticWord(
        ReadOnlySpan<char> expression,
        bool enableOperators,
        SearchValues<char> separators,
        out string? word)
    {
        if (!enableOperators)
            return TryGetSingleWord(expression, separators, out word);

        word = null;
        var tokens = Tokenize(expression, enableOperators: true, separators);
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
        var separators = snapshot.Tokenization.QuerySeparatorValues;
        var tokens = Tokenize(expression.AsSpan(), enableOperators, separators);
        if (tokens.Count == 0)
            return null;

        return EvaluateTokens(tokens, method, qc, snapshot);
    }

    // Splits the expression into a well-formed infix token list (see Append); empty when nothing is left.
    private static List<QueryToken> Tokenize(
        ReadOnlySpan<char> expression,
        bool enableOperators,
        SearchValues<char> separators)
    {
        var tokens = new List<QueryToken>();
        int wordStart = -1;
        bool allowParentheses = !enableOperators || HasValidParentheses(expression);

        for (int i = 0; i < expression.Length; i++)
        {
            char ch = expression[i];
            if (!separators.Contains(ch))
            {
                if (wordStart < 0)
                    wordStart = i;

                continue;
            }

            EmitWord(expression, wordStart, i, enableOperators, tokens);
            wordStart = -1;

            if (enableOperators && allowParentheses && ch is '(' or ')')
            {
                Append(tokens, QueryToken.CreateOperation(
                    ch == '(' ? WordOperations.OpenParenthesis : WordOperations.CloseParenthesis));
            }
        }

        EmitWord(expression, wordStart, expression.Length, enableOperators, tokens);
        DropTrailingOperators(tokens);
        return tokens;
    }

    private static void EmitWord(
        ReadOnlySpan<char> expression,
        int wordStart,
        int wordEnd,
        bool enableOperators,
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

        Append(tokens, QueryToken.CreateWord(TextNormalizer.CreateNormalizedWord(wordSpan)));
    }

    // Appends one token so that the list stays a well-formed infix expression (malformed input is corrected, not
    // rejected; docs/query-semantics.md, query parsing rule 5). The start of the expression and of every group
    // behave alike:
    //   - a term, "(" or NOT right after a term or ")" gets an implicit AND in front ("cat dog" = "cat AND dog");
    //   - a binary operator at the start is dropped;
    //   - NOT NOT counts as one NOT; a binary operator followed by NOT is kept (NOT binds to what follows);
    //   - any other pair of adjacent operators collapses to AND, which is then checked against the token before it
    //     ("a AND NOT AND b" = "a AND b", "(NOT AND a)" = "(a)");
    //   - ")" drops the operators in front of it, and an empty group is dropped.
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
                    tokens.Add(QueryToken.CreateOperation(WordOperations.And));
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
                if (tokens[^1].Operation == WordOperations.OpenParenthesis)
                    tokens.RemoveAt(tokens.Count - 1);
                else
                    tokens.Add(token);
                return;
        }
    }

    private static void DropTrailingOperators(List<QueryToken> tokens)
    {
        while (tokens.Count > 0 && tokens[^1].Operation is WordOperations.And or WordOperations.Or or WordOperations.Not)
            tokens.RemoveAt(tokens.Count - 1);
    }

    // Evaluates the token list using the shunting-yard algorithm (Dijkstra, 1961).
    // Operators are held in a stack and applied in precedence order as values arrive.
    /// <summary>
    /// Parses an expression exactly as <see cref="Evaluate"/> does and returns it in postfix order
    /// (operands as words, operators applied in the order EvaluateTokens applies them).
    /// Returns <see langword="null"/> when the expression has no operand or is malformed in a way
    /// that makes <see cref="Evaluate"/> return no result. Used by <see cref="NameMatcher"/>.
    /// </summary>
    internal static List<QueryToken>? ToPostfix(string expression, bool enableOperators, SearchValues<char> separators)
    {
        var tokens = Tokenize(expression.AsSpan(), enableOperators, separators);
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
                    operators.Add(token.Operation);
                    break;
                case WordOperations.CloseParenthesis:
                    while (operators.Count > 0 && operators[^1] != WordOperations.OpenParenthesis)
                    {
                        output.Add(QueryToken.CreateOperation(operators[^1]));
                        operators.RemoveAt(operators.Count - 1);
                    }

                    if (operators.Count > 0)
                        operators.RemoveAt(operators.Count - 1);
                    break;
                default:
                    while (operators.Count > 0 && HasHigherOrEqualPrecedence(operators[^1], token.Operation))
                    {
                        output.Add(QueryToken.CreateOperation(operators[^1]));
                        operators.RemoveAt(operators.Count - 1);
                    }

                    operators.Add(token.Operation);
                    break;
            }
        }

        for (int i = operators.Count - 1; i >= 0; i--)
        {
            if (operators[i] != WordOperations.OpenParenthesis)
                output.Add(QueryToken.CreateOperation(operators[i]));
        }

        return output;
    }

    private static FastBitSet? EvaluateTokens(
        List<QueryToken> tokens,
        WordMatchMethod method,
        QueryContext qc,
        IndexSnapshot snapshot)
    {
        var values = new List<FastBitSet>(tokens.Count);
        var operators = new List<WordOperations>(tokens.Count);
        FastBitSet? allRecords = null;

        foreach (var token in tokens)
        {
            switch (token.Operation)
            {
                case WordOperations.None:
                    values.Add(QueryMatcher.Match(token.Word, method, qc, snapshot));
                    break;

                case WordOperations.OpenParenthesis:
                    operators.Add(token.Operation);
                    break;

                case WordOperations.CloseParenthesis:
                    while (operators.Count > 0 && operators[^1] != WordOperations.OpenParenthesis)
                    {
                        if (!TryApplyTopOperator(values, operators, qc, ref allRecords))
                            return null;
                    }

                    if (operators.Count > 0 && operators[^1] == WordOperations.OpenParenthesis)
                        operators.RemoveAt(operators.Count - 1);
                    break;

                default:
                    while (operators.Count > 0 && HasHigherOrEqualPrecedence(operators[^1], token.Operation))
                    {
                        if (!TryApplyTopOperator(values, operators, qc, ref allRecords))
                            return null;
                    }

                    operators.Add(token.Operation);
                    break;
            }
        }

        while (operators.Count > 0)
        {
            if (operators[^1] == WordOperations.OpenParenthesis)
            {
                operators.RemoveAt(operators.Count - 1);
                continue;
            }

            if (!TryApplyTopOperator(values, operators, qc, ref allRecords))
                return null;
        }

        return values.Count > 0 ? values[^1] : null;
    }

    private static bool TryApplyTopOperator(
        List<FastBitSet> values,
        List<WordOperations> operators,
        QueryContext qc,
        ref FastBitSet? allRecords)
    {
        var operation = operators[^1];
        operators.RemoveAt(operators.Count - 1);

        switch (operation)
        {
            case WordOperations.Not:
                if (values.Count < 1)
                    return false;

                var operand = values[^1];
                values.RemoveAt(values.Count - 1);

                allRecords ??= qc.RentAllTrueBitSet();
                var notResult = qc.RentCopyOf(allRecords);
                notResult.ExceptWith(operand);
                values.Add(notResult);
                return true;

            case WordOperations.And:
            case WordOperations.Or:
                if (values.Count < 2)
                    return false;

                var right = values[^1];
                values.RemoveAt(values.Count - 1);
                var left = values[^1];

                if (operation == WordOperations.And)
                    left.IntersectWith(right);
                else
                    left.UnionWith(right);

                return true;

            default:
                return false;
        }
    }

    private static bool HasHigherOrEqualPrecedence(WordOperations left, WordOperations right)
    {
        if (left == WordOperations.OpenParenthesis)
            return false;

        return GetPrecedence(left) >= GetPrecedence(right);
    }

    private static int GetPrecedence(WordOperations operation)
    {
        return operation switch
        {
            WordOperations.Or => 1,
            WordOperations.And => 2,
            WordOperations.Not => 3,
            _ => -1
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

    internal readonly record struct QueryToken(string Word, WordOperations Operation)
    {
        public static QueryToken CreateWord(string word) => new(word, WordOperations.None);
        public static QueryToken CreateOperation(WordOperations operation) => new(string.Empty, operation);
    }
}

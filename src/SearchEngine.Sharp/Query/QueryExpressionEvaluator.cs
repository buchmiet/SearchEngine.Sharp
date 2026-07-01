using System.Buffers;
using SearchEngine;
using SearchEngine.Index;
using SearchEngine.Pooling;
using SearchEngine.Snapshots;
using SearchEngine.Tokenizer;

namespace SearchEngine.Query;

internal static class QueryExpressionEvaluator
{
    private static readonly SearchValues<char> Separators = SearchSeparators.QueryExpression;

    // Fast path: if the expression contains exactly one word (no operators, no multiple
    // tokens), skip the full tokenize → correct → evaluate pipeline and go straight to
    // QueryMatcher.Match. This is the common case for simple type-ahead search.
    internal static bool TryGetSingleWord(ReadOnlySpan<char> expression, out string? word)
    {
        word = null;

        int wordStart = -1;
        bool hasWord = false;

        for (int i = 0; i < expression.Length; i++)
        {
            if (Separators.Contains(expression[i]))
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

    internal static FastBitSet? Evaluate(
        string expression,
        bool enableOperators,
        WordMatchMethod method,
        QueryContext qc,
        IndexSnapshot snapshot)
    {
        var tokens = Tokenize(expression.AsSpan(), enableOperators);
        if (tokens.Count == 0)
            return null;

        CorrectTokens(tokens);
        if (tokens.Count == 0)
            return null;

        return EvaluateTokens(tokens, method, qc, snapshot);
    }

    private static List<QueryToken> Tokenize(ReadOnlySpan<char> expression, bool enableOperators)
    {
        var tokens = new List<QueryToken>();
        int wordStart = -1;
        bool allowParentheses = !enableOperators || HasValidParentheses(expression);

        for (int i = 0; i < expression.Length; i++)
        {
            char ch = expression[i];
            if (!Separators.Contains(ch))
            {
                if (wordStart < 0)
                    wordStart = i;

                continue;
            }

            EmitWord(expression, wordStart, i, enableOperators, tokens);
            wordStart = -1;

            if (enableOperators && allowParentheses && ch is '(' or ')')
            {
                tokens.Add(QueryToken.CreateOperation(
                    ch == '(' ? WordOperations.OpenParenthesis : WordOperations.CloseParenthesis));
            }
        }

        EmitWord(expression, wordStart, expression.Length, enableOperators, tokens);
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
            tokens.Add(QueryToken.CreateOperation(operation));
            return;
        }

        tokens.Add(QueryToken.CreateWord(TextNormalizer.CreateNormalizedWord(wordSpan)));
    }

    // Sanitises a raw token list so that EvaluateTokens receives a well-formed expression:
    //  1. Strip leading OR/AND (an expression cannot start with a binary operator).
    //  2. Strip trailing operators (an expression cannot end with an operator).
    //  3. Collapse consecutive binary operators (e.g. "AND AND" → "AND") via
    //     CorrectOperationSequence.
    //  4. Insert implicit AND between adjacent words / close-open parentheses
    //     (e.g. "cat dog" → "cat AND dog") via InsertDefaultAnd.
    private static void CorrectTokens(List<QueryToken> tokens)
    {
        if (tokens.Count == 0)
            return;

        while (tokens.Count > 0 && tokens[0].Operation is WordOperations.Or or WordOperations.And)
            tokens.RemoveAt(0);

        while (tokens.Count > 0 && tokens[^1].Operation is not WordOperations.None and not WordOperations.CloseParenthesis)
            tokens.RemoveAt(tokens.Count - 1);

        for (int i = 0; i < tokens.Count - 1; i++)
        {
            CorrectOperationSequence(tokens, ref i);
            InsertDefaultAnd(tokens, i);
        }
    }

    // Collapses two adjacent operators into a single well-defined one.
    // Rules (only binary–binary adjacency is handled; word and parenthesis tokens are skipped):
    //   binary + NOT   → keep as-is (NOT binds to the following word)
    //   NOT    + NOT   → remove the second (double negation is a no-op at parse time)
    //   anything else  → replace both with AND and back up the index
    private static void CorrectOperationSequence(List<QueryToken> tokens, ref int i)
    {
        if (tokens[i].Operation is WordOperations.None or WordOperations.OpenParenthesis or WordOperations.CloseParenthesis
            || tokens[i + 1].Operation is WordOperations.None or WordOperations.OpenParenthesis or WordOperations.CloseParenthesis)
            return;

        if (tokens[i].Operation != WordOperations.Not && tokens[i + 1].Operation == WordOperations.Not)
            return;

        if (tokens[i].Operation == WordOperations.Not && tokens[i + 1].Operation == WordOperations.Not)
        {
            tokens.RemoveAt(i + 1);
            return;
        }

        tokens[i] = QueryToken.CreateOperation(WordOperations.And);
        tokens.RemoveAt(i + 1);
        i--;
    }

    // If the current token ends a value (word or ")") and the next token starts a
    // new value (word, "(", or NOT), insert an implicit AND between them.
    // This makes "cat dog" behave identically to "cat AND dog".
    private static void InsertDefaultAnd(List<QueryToken> tokens, int i)
    {
        if (i >= tokens.Count - 1)
            return;

        var current = tokens[i].Operation;
        var next = tokens[i + 1].Operation;

        bool leftComplete = current is WordOperations.None or WordOperations.CloseParenthesis;
        bool rightStarting = next is WordOperations.None or WordOperations.OpenParenthesis or WordOperations.Not;

        if (leftComplete && rightStarting)
            tokens.Insert(i + 1, QueryToken.CreateOperation(WordOperations.And));
    }

    // Evaluates the token list using the shunting-yard algorithm (Dijkstra, 1961).
    // Operators are held in a stack and applied in precedence order as values arrive.
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

    private readonly record struct QueryToken(string Word, WordOperations Operation)
    {
        public static QueryToken CreateWord(string word) => new(word, WordOperations.None);
        public static QueryToken CreateOperation(WordOperations operation) => new(string.Empty, operation);
    }
}

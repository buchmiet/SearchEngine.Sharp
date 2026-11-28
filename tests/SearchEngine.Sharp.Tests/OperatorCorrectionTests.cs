namespace SearchEngine.Sharp.Tests;

// Malformed operator sequences are corrected, not rejected (docs/query-semantics.md, query parsing rule 5), so a user
// typing a query live never gets an exception or an empty result for a half-typed expression. Expected ids follow from
// the documented rules, applied at the start and end of the expression and of every parenthesized group: leading AND/OR
// and trailing operators are dropped, NOT NOT counts as one NOT, any other pair of adjacent operators (other than a
// binary operator followed by NOT) collapses to AND, and an empty group is dropped. The correction is in
// QueryExpressionEvaluator. A leading NOT AND or NOT OR is corrected like the rest and must not throw.
public class OperatorCorrectionTests
{
    // 1: alpha beta, 2: alpha, 3: beta, 4: gamma
    private static readonly Dictionary<int, string> Documents = new()
    {
        [1] = "alpha beta",
        [2] = "alpha",
        [3] = "beta",
        [4] = "gamma",
    };

    public static IEnumerable<Func<(string Expression, int[] Expected)>> Cases() =>
    [
        // Pairs of operators between two terms.
        () => ("alpha NOT AND beta", [1]),
        () => ("alpha NOT OR beta", [1]),
        () => ("alpha AND NOT AND beta", [1]),
        () => ("alpha OR NOT OR beta", [1]),
        () => ("alpha AND AND beta", [1]),
        () => ("alpha OR OR beta", [1]),
        () => ("alpha AND OR beta", [1]),
        () => ("alpha OR AND beta", [1]),
        () => ("beta OR NOT AND alpha", [1]),
        () => ("alpha AND NOT beta", [2]),
        () => ("alpha NOT beta", [2]),
        () => ("alpha NOT NOT beta", [2]),
        () => ("alpha OR NOT beta", [1, 2, 4]),

        // Start and end of the expression.
        () => ("AND alpha", [1, 2]),
        () => ("OR alpha", [1, 2]),
        () => ("NOT AND alpha", [1, 2]),
        () => ("NOT OR alpha", [1, 2]),
        () => ("NOT AND NOT OR alpha", [1, 2]),
        () => ("AND NOT alpha", [3, 4]),
        () => ("NOT NOT alpha", [3, 4]),
        () => ("NOT NOT NOT alpha", [3, 4]),
        () => ("alpha AND", [1, 2]),
        () => ("alpha OR", [1, 2]),
        () => ("alpha NOT", [1, 2]),
        () => ("alpha AND NOT", [1, 2]),
        () => ("alpha NOT AND", [1, 2]),

        // Start and end of a group.
        () => ("(AND alpha)", [1, 2]),
        () => ("(OR alpha)", [1, 2]),
        () => ("(NOT AND alpha)", [1, 2]),
        () => ("(alpha AND)", [1, 2]),
        () => ("(alpha NOT)", [1, 2]),
        () => ("(alpha OR) beta", [1]),
        () => ("gamma OR (AND alpha)", [1, 2, 4]),
        () => ("gamma OR (alpha NOT AND beta)", [1, 4]),
        () => ("((NOT AND alpha) OR (gamma AND))", [1, 2, 4]),
        () => ("NOT (AND alpha OR beta)", [4]),
        () => ("NOT (alpha OR beta)", [4]),
        () => ("((alpha OR gamma)) beta", [1]),
        () => ("(alpha OR gamma)(beta)", [1]),

        // Empty groups.
        () => ("alpha () beta", [1]),
        () => ("() alpha", [1, 2]),
        () => ("alpha ()", [1, 2]),
        () => ("(()) alpha", [1, 2]),
        () => ("alpha AND (OR) beta", [1]),
        () => ("gamma OR (NOT)", [4]),
        () => ("alpha () OR gamma", [1, 2, 4]),
        () => ("alpha (OR) OR gamma", [1, 2, 4]),
        () => ("alpha (NOT) OR gamma", [1, 2, 4]),
        () => ("(alpha) () OR gamma", [1, 2, 4]),
        () => ("alpha (()) OR gamma", [1, 2, 4]),
        () => ("alpha OR () gamma", [1, 2, 4]),
        () => ("alpha AND () OR gamma", []),
        () => ("alpha NOT () OR gamma", []),

        // Unbalanced parentheses are plain separators.
        () => ("(alpha OR gamma", [1, 2, 4]),
        () => ("alpha OR gamma)", [1, 2, 4]),

        // Nothing left after correction.
        () => ("AND", []),
        () => ("NOT", []),
        () => ("AND OR NOT", []),
        () => ("()", []),
        () => ("(AND)", []),
        () => ("(NOT) OR", []),
    ];

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task A_malformed_operator_sequence_is_corrected_by_the_documented_rules(string expression, int[] expected)
    {
        var (engine, updater, _) = TestIndex.Create();
        updater.RebuildFrom(Documents);

        foreach (var method in new[] { WordMatchMethod.Exact, WordMatchMethod.Within })
        {
            await Assert.That(engine.Find(expression, method, enableOperators: true).Order()).IsEquivalentTo(expected, CollectionOrdering.Matching);
            await Assert.That(engine.CountMatches(expression, method, enableOperators: true)).IsEqualTo(expected.Length);
        }
    }

    // Parentheses group only when both are query separators. With one of them a separator and the other a token
    // character, the separator splits terms like a space and the other stays part of its term.
    // 1: "(a b", 2: "a b)", 3: "zzz", 4: "b)"
    public static IEnumerable<Func<(string QuerySeparators, string Expression, int[] Expected)>> OneParenthesisCases() =>
    [
        () => (" )", "(a) b", [1]),
        () => (" )", "(a b)", [1]),
        () => (" )", "a (b)", []),
        () => (" )", "NOT (a zzz)", [3]),
        () => (" (", "(a b)", [2]),
        () => (" (", "NOT (a b)", [4]),
        () => (" (", "(a) b", []),
        () => (" (", "zzz OR (a b)", [2, 3]),
    ];

    [Test]
    [MethodDataSource(nameof(OneParenthesisCases))]
    public async Task A_lone_parenthesis_that_is_a_query_separator_splits_like_a_space(string querySeparators, string expression, int[] expected)
    {
        var tokenization = SearchTokenization.Create(" ", querySeparators);
        var documents = new Dictionary<int, string> { [1] = "(a b", [2] = "a b)", [3] = "zzz", [4] = "b)" };
        var (engine, updater, _) = TestIndex.Create(tokenization);
        updater.RebuildFrom(documents);

        foreach (var method in new[] { WordMatchMethod.Exact, WordMatchMethod.Within })
        {
            await Assert.That(engine.Find(expression, method, enableOperators: true).Order()).IsEquivalentTo(expected, CollectionOrdering.Matching);
            await Assert.That(engine.CountMatches(expression, method, enableOperators: true)).IsEqualTo(expected.Length);

            var matcher = NameMatcher.Create(expression, method, enableOperators: true, tokenization);
            int[] matched = [.. documents.Where(d => matcher.IsMatch(d.Value)).Select(d => d.Key).Order()];
            await Assert.That(matched).IsEquivalentTo(expected, CollectionOrdering.Matching);
        }
    }

    // The FileMask preset parses a glob term after the corrected operators.
    [Test]
    public async Task A_leading_NOT_AND_is_corrected_under_the_FileMask_preset()
    {
        var (engine, updater, _) = TestIndex.Create(SearchTokenization.FileMask);
        updater.RebuildFrom(new Dictionary<int, string> { [1] = "report.pdf", [2] = "notes.txt" });

        var results = engine.Find("NOT AND *.pdf", WordMatchMethod.Exact, enableOperators: true);

        await Assert.That(results).IsEquivalentTo([1], CollectionOrdering.Matching);
    }
}

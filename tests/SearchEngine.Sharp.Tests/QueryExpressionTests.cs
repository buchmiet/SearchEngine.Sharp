namespace SearchEngine.Sharp.Tests;

// How a well-formed query expression selects entries (docs/query-semantics.md#query-parsing-default-preset): terms
// ignore case, terms separated by spaces or AND must all match, OR takes either side, NOT excludes, AND binds tighter
// than OR, parentheses override that, and an empty expression matches nothing. With operators off, and, or and not are
// plain terms. Find and CountMatches must agree, for Exact and Within alike (QueryExpressionEvaluator). Malformed
// operator sequences are corrected by the rules tested in OperatorCorrectionTests.
public class QueryExpressionTests
{
    // 1: GA-100 digital watch, 2: GA-200 analog watch, 3: DW-5600 digital watch, 4: DW-6900 analog clock,
    // 5: PRW-3000 solar watch. Every term is a whole token, so Exact and Within select the same entries.
    private static readonly Dictionary<int, string> Watches = new()
    {
        [1] = "GA-100 digital watch",
        [2] = "GA-200 analog watch",
        [3] = "DW-5600 digital watch",
        [4] = "DW-6900 analog clock",
        [5] = "PRW-3000 solar watch",
    };

    public static IEnumerable<Func<(string Expression, int[] Expected)>> Cases() =>
    [
        // Terms ignore case.
        () => ("digital", [1, 3]),
        () => ("DIGITAL", [1, 3]),
        () => ("DiGiTaL", [1, 3]),
        () => ("nonexistent", []),

        // A space and AND both require every term.
        () => ("digital watch", [1, 3]),
        () => ("digital AND watch", [1, 3]),

        // OR takes the union.
        () => ("digital OR analog", [1, 2, 3, 4]),
        () => ("solar OR clock", [4, 5]),

        // NOT excludes, also as the whole expression.
        () => ("watch NOT digital", [2, 5]),
        () => ("NOT clock", [1, 2, 3, 5]),
        () => ("NOT nonexistent", [1, 2, 3, 4, 5]),

        // AND binds tighter than OR: clock OR (digital AND watch), not (clock OR digital) AND watch. The first row
        // gives the same set read either way, the second tells them apart, parentheses give the other reading.
        () => ("solar OR digital AND watch", [1, 3, 5]),
        () => ("clock OR digital AND watch", [1, 3, 4]),
        () => ("(clock OR digital) AND watch", [1, 3]),

        // Combinations.
        () => ("(digital OR analog) AND watch NOT GA", [3]),
        () => ("watch AND NOT digital AND NOT analog", [5]),
        () => ("NOT digital AND NOT analog", [5]),

        // Nothing to search for.
        () => ("", []),
        () => ("   ", []),
    ];

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task An_expression_selects_the_entries_its_operators_describe(string expression, int[] expected)
    {
        var (engine, updater, _) = TestIndex.Create();
        updater.RebuildFrom(Watches);
        // An expression without operators must give the same entries with operators off.
        bool hasOperators = expression.Contains('(') || expression.Split(' ').Any(word => word is "AND" or "OR" or "NOT");
        bool[] operatorModes = hasOperators ? [true] : [true, false];

        foreach (var method in new[] { WordMatchMethod.Exact, WordMatchMethod.Within })
        foreach (bool operators in operatorModes)
        {
            await Assert.That(engine.Find(expression, method, operators).Order()).IsEquivalentTo(expected, CollectionOrdering.Matching);
            await Assert.That(engine.CountMatches(expression, method, operators)).IsEqualTo(expected.Length);
        }
    }

    [Test]
    [Arguments("or", 1)]
    [Arguments("not", 3)]
    public async Task With_operators_off_an_operator_word_is_a_plain_term(string query, int expectedId)
    {
        var (engine, updater, _) = TestIndex.Create();
        updater.RebuildFrom(new Dictionary<int, string>
        {
            [1] = "this or that",
            [2] = "something else",
            [3] = "not applicable",
            [4] = "applicable",
        });

        var results = engine.Find(query, WordMatchMethod.Within, enableOperators: false);

        await Assert.That(results).IsEquivalentTo([expectedId], CollectionOrdering.Matching);
    }

    [Test]
    public async Task An_empty_index_matches_nothing()
    {
        var (engine, _, _) = TestIndex.Create();

        var results = engine.Find("test", WordMatchMethod.Within, true);

        await Assert.That(results).IsEmpty();
    }
}

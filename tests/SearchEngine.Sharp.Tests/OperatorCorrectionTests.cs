namespace SearchEngine.Sharp.Tests;

/// <summary>
/// Malformed operator sequences are corrected, not rejected (docs/query-semantics.md, query parsing rule 5).
/// Expected ids follow from the documented rules, applied at the start and end of the expression and of every
/// parenthesised group: leading AND/OR and trailing operators are dropped, NOT NOT counts as one NOT, any other
/// pair of adjacent operators (other than a binary operator followed by NOT) collapses to AND, and an empty group
/// is dropped.
/// </summary>
public class OperatorCorrectionTests
{
    // 1: alpha beta, 2: alpha, 3: beta, 4: gamma
    internal static readonly Dictionary<int, string> Documents = new()
    {
        [1] = "alpha beta",
        [2] = "alpha",
        [3] = "beta",
        [4] = "gamma",
    };

    public static TheoryData<string, int[]> Cases() => new()
    {
        // Pairs of operators between two terms.
        { "alpha NOT AND beta", [1] },
        { "alpha NOT OR beta", [1] },
        { "alpha AND NOT AND beta", [1] },
        { "alpha OR NOT OR beta", [1] },
        { "alpha AND AND beta", [1] },
        { "alpha OR OR beta", [1] },
        { "alpha AND OR beta", [1] },
        { "alpha OR AND beta", [1] },
        { "beta OR NOT AND alpha", [1] },
        { "alpha AND NOT beta", [2] },
        { "alpha NOT beta", [2] },
        { "alpha NOT NOT beta", [2] },
        { "alpha OR NOT beta", [1, 2, 4] },

        // Start and end of the expression.
        { "AND alpha", [1, 2] },
        { "OR alpha", [1, 2] },
        { "NOT AND alpha", [1, 2] },
        { "NOT OR alpha", [1, 2] },
        { "AND NOT alpha", [3, 4] },
        { "NOT NOT alpha", [3, 4] },
        { "NOT NOT NOT alpha", [3, 4] },
        { "alpha AND", [1, 2] },
        { "alpha OR", [1, 2] },
        { "alpha NOT", [1, 2] },
        { "alpha AND NOT", [1, 2] },
        { "alpha NOT AND", [1, 2] },

        // Start and end of a group.
        { "(AND alpha)", [1, 2] },
        { "(OR alpha)", [1, 2] },
        { "(NOT AND alpha)", [1, 2] },
        { "(alpha AND)", [1, 2] },
        { "(alpha NOT)", [1, 2] },
        { "(alpha OR) beta", [1] },
        { "gamma OR (AND alpha)", [1, 2, 4] },
        { "gamma OR (alpha NOT AND beta)", [1, 4] },
        { "((NOT AND alpha) OR (gamma AND))", [1, 2, 4] },
        { "NOT (AND alpha OR beta)", [4] },
        { "NOT (alpha OR beta)", [4] },
        { "((alpha OR gamma)) beta", [1] },
        { "(alpha OR gamma)(beta)", [1] },

        // Empty groups.
        { "alpha () beta", [1] },
        { "() alpha", [1, 2] },
        { "alpha ()", [1, 2] },
        { "(()) alpha", [1, 2] },
        { "alpha AND (OR) beta", [1] },
        { "gamma OR (NOT)", [4] },

        // Unbalanced parentheses are plain separators.
        { "(alpha OR gamma", [1, 2, 4] },
        { "alpha OR gamma)", [1, 2, 4] },

        // Nothing left after correction.
        { "AND", [] },
        { "NOT", [] },
        { "AND OR NOT", [] },
        { "()", [] },
        { "(AND)", [] },
        { "(NOT) OR", [] },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void MalformedOperatorSequence_IsCorrected(string expression, int[] expected)
    {
        var provider = new IndexSnapshotProvider();
        new IndexUpdater(provider).RebuildFrom(Documents);
        var engine = new SearchEngineSharp(provider);

        foreach (var method in new[] { WordMatchMethod.Exact, WordMatchMethod.Within })
        {
            Assert.Equal(expected, engine.Find(expression, method, enableOperators: true).Order());
            Assert.Equal(expected.Length, engine.CountMatches(expression, method, enableOperators: true));
        }
    }
}

using SearchEngine;

namespace SearchEngine.Sharp.Tests;

/// <summary>
/// End-to-end tests for AND/OR/NOT operators through ISearchEngine.Find.
/// Each test builds a real index and verifies search results.
/// </summary>
public class SearchEngineOperatorTests
{
    private static (SearchEngineSharp engine, IIndexUpdater updater) CreateEngine()
    {
        var provider = new IndexSnapshotProvider();
        var updater = new IndexUpdater(provider);
        var engine = new SearchEngineSharp(provider);
        return (engine, updater);
    }

    private static (SearchEngineSharp engine, IIndexUpdater updater)
        CreateWatchIndex()
    {
        var (engine, updater) = CreateEngine();
        updater.RebuildFrom(new Dictionary<int, string>
        {
            [1] = "GA-100 digital watch",
            [2] = "GA-200 analog watch",
            [3] = "DW-5600 digital watch",
            [4] = "DW-6900 analog clock",
            [5] = "PRW-3000 solar watch",
        });
        return (engine, updater);
    }

    private List<int> Find(SearchEngineSharp engine, string expression,
        bool enableOperators = true,
        WordMatchMethod method = WordMatchMethod.Within)
    {
        return engine.Find(expression, method, enableOperators);
    }

    // ══════════════════════════════════════════════
    //  Implicit AND (spaces between words)
    // ══════════════════════════════════════════════

    [Fact]
    public void ImplicitAnd_TwoWords_BothMustMatch()
    {
        var (engine, _) = CreateWatchIndex();

        // "digital watch" → implicit AND → docs with BOTH "digital" and "watch"
        var results = Find(engine, "digital watch");

        Assert.Equal(2, results.Count);
        Assert.Contains(1, results); // GA-100 digital watch
        Assert.Contains(3, results); // DW-5600 digital watch
        Assert.DoesNotContain(4, results); // "clock", not "watch"
    }

    [Fact]
    public void ExplicitAnd_SameAsImplicit()
    {
        var (engine, _) = CreateWatchIndex();

        var implicit_ = Find(engine, "digital watch");
        var explicit_ = Find(engine, "digital AND watch");

        Assert.Equal(implicit_.OrderBy(x => x), explicit_.OrderBy(x => x));
    }

    // ══════════════════════════════════════════════
    //  OR
    // ══════════════════════════════════════════════

    [Fact]
    public void Or_ReturnsUnionOfBothSides()
    {
        var (engine, _) = CreateWatchIndex();

        // "digital OR analog" → any doc with either word
        var results = Find(engine, "digital OR analog");

        Assert.Equal(4, results.Count);
        Assert.Contains(1, results); // digital
        Assert.Contains(2, results); // analog
        Assert.Contains(3, results); // digital
        Assert.Contains(4, results); // analog
        Assert.DoesNotContain(5, results); // solar — neither digital nor analog
    }

    [Fact]
    public void Or_SingleMatch_OnOneSide()
    {
        var (engine, _) = CreateWatchIndex();

        // "solar OR clock" → PRW-3000 (solar) + DW-6900 (clock)
        var results = Find(engine, "solar OR clock");

        Assert.Equal(2, results.Count);
        Assert.Contains(4, results); // clock
        Assert.Contains(5, results); // solar
    }

    // ══════════════════════════════════════════════
    //  NOT
    // ══════════════════════════════════════════════

    [Fact]
    public void Not_ExcludesMatchingDocs()
    {
        var (engine, _) = CreateWatchIndex();

        // "watch NOT digital" → has "watch" but NOT "digital"
        var results = Find(engine, "watch NOT digital");

        Assert.Equal(2, results.Count);
        Assert.Contains(2, results); // GA-200 analog watch
        Assert.Contains(5, results); // PRW-3000 solar watch
        Assert.DoesNotContain(1, results); // GA-100 digital watch — excluded
        Assert.DoesNotContain(3, results); // DW-5600 digital watch — excluded
    }

    [Fact]
    public void Not_Standalone_ReturnsAllExceptMatch()
    {
        var (engine, _) = CreateWatchIndex();

        // "NOT clock" → everything except docs with "clock"
        var results = Find(engine, "NOT clock");

        Assert.Equal(4, results.Count);
        Assert.DoesNotContain(4, results); // DW-6900 analog clock — excluded
    }

    [Fact]
    public void Not_NoMatches_ReturnsAll()
    {
        var (engine, _) = CreateWatchIndex();

        // "NOT nonexistent" → NOT matches nothing → all docs returned
        var results = Find(engine, "NOT nonexistent");

        Assert.Equal(5, results.Count);
    }

    // ══════════════════════════════════════════════
    //  Operator precedence: AND binds tighter than OR
    // ══════════════════════════════════════════════

    [Fact]
    public void Precedence_AndBindsTighterThanOr()
    {
        var (engine, _) = CreateWatchIndex();

        // "solar OR digital AND watch" → solar OR (digital AND watch)
        // = PRW-3000(solar) + GA-100(digital watch) + DW-5600(digital watch)
        var results = Find(engine, "solar OR digital AND watch");

        Assert.Equal(3, results.Count);
        Assert.Contains(1, results); // GA-100 digital watch
        Assert.Contains(3, results); // DW-5600 digital watch
        Assert.Contains(5, results); // PRW-3000 solar watch
    }

    // ══════════════════════════════════════════════
    //  Parentheses override precedence
    // ══════════════════════════════════════════════

    [Fact]
    public void Parentheses_OverridePrecedence()
    {
        var (engine, updater) = CreateEngine();
        updater.RebuildFrom(new Dictionary<int, string>
        {
            [1] = "red apple fruit",
            [2] = "green apple fruit",
            [3] = "red cherry fruit",
            [4] = "green cherry fruit",
        });

        // Without parens: "red OR green AND apple" → red OR (green AND apple) = {1,2,3}
        var withoutParens = Find(engine, "red OR green AND apple");
        Assert.Equal(3, withoutParens.Count);

        // With parens: "(red OR green) AND apple" → (red ∪ green) ∩ apple = {1,2}
        var withParens = Find(engine, "(red OR green) AND apple");
        Assert.Equal(2, withParens.Count);
        Assert.Contains(1, withParens); // red apple
        Assert.Contains(2, withParens); // green apple
        Assert.DoesNotContain(3, withParens); // cherry — not apple
    }

    // ══════════════════════════════════════════════
    //  Complex expressions
    // ══════════════════════════════════════════════

    [Fact]
    public void Complex_AndOrNotCombined()
    {
        var (engine, _) = CreateWatchIndex();

        // "(digital OR analog) AND watch NOT GA" →
        // (digital ∪ analog) ∩ watch ∩ ¬GA
        // digital|analog = {1,2,3,4}, watch = {1,2,3,5}, AND = {1,2,3}
        // NOT GA = {3,4,5}, AND = {3}
        var results = Find(engine, "(digital OR analog) AND watch NOT GA");

        Assert.Single(results);
        Assert.Contains(3, results); // DW-5600 digital watch
    }

    [Fact]
    public void Complex_ExplicitAndOrNotWithoutParens()
    {
        var (engine, _) = CreateWatchIndex();

        // "watch AND NOT digital AND NOT analog" → watch ∩ ¬digital ∩ ¬analog
        // watch={1,2,3,5}, ¬digital={2,4,5}, ¬analog={1,3,5}
        // = {5} PRW-3000 solar watch
        var results = Find(engine, "watch AND NOT digital AND NOT analog");

        Assert.Single(results);
        Assert.Contains(5, results);
    }

    [Fact]
    public void Complex_NotWithOr()
    {
        var (engine, _) = CreateWatchIndex();

        // "NOT digital AND NOT analog" → docs without digital AND without analog
        // = [5] PRW-3000 solar watch
        var results = Find(engine, "NOT digital AND NOT analog");

        Assert.Single(results);
        Assert.Contains(5, results);
    }

    // ══════════════════════════════════════════════
    //  enableOperators = false
    // ══════════════════════════════════════════════

    [Fact]
    public void OperatorsDisabled_OrTreatedAsWord()
    {
        var (engine, updater) = CreateEngine();
        updater.RebuildFrom(new Dictionary<int, string>
        {
            [1] = "this or that",
            [2] = "something else",
        });

        // With operators disabled, "or" is just a word to search for
        var results = Find(engine, "or", enableOperators: false);

        Assert.Single(results);
        Assert.Contains(1, results);
    }

    [Fact]
    public void OperatorsDisabled_NotTreatedAsWord()
    {
        var (engine, updater) = CreateEngine();
        updater.RebuildFrom(new Dictionary<int, string>
        {
            [1] = "not applicable",
            [2] = "applicable",
        });

        var results = Find(engine, "not", enableOperators: false);

        Assert.Single(results);
        Assert.Contains(1, results);
    }

    // ══════════════════════════════════════════════
    //  Exact vs Within with operators
    // ══════════════════════════════════════════════

    [Fact]
    public void Operators_WorkWithExactMatch()
    {
        var (engine, updater) = CreateEngine();
        updater.RebuildFrom(new Dictionary<int, string>
        {
            [1] = "alpha beta",
            [2] = "alpha gamma",
            [3] = "beta gamma",
        });

        var results = Find(engine, "alpha OR gamma",
            method: WordMatchMethod.Exact);

        Assert.Equal(3, results.Count);
        Assert.Contains(1, results); // alpha
        Assert.Contains(2, results); // alpha, gamma
        Assert.Contains(3, results); // gamma
    }

    [Fact]
    public void Operators_ExactNotWithin_NoSubstringMatch()
    {
        var (engine, updater) = CreateEngine();
        updater.RebuildFrom(new Dictionary<int, string>
        {
            [1] = "alphabet",
            [2] = "alpha",
        });

        // Exact: "alph" shouldn't match "alpha" or "alphabet"
        var exact = Find(engine, "alph", enableOperators: false,
            method: WordMatchMethod.Exact);
        Assert.Empty(exact);

        // Within: "alph" should match both
        var within = Find(engine, "alph", enableOperators: false,
            method: WordMatchMethod.Within);
        Assert.Equal(2, within.Count);
    }

    [Fact]
    public void Within_DoesNotReturnFalsePositive_WhenBigramsMatchButSubstringDoesNot()
    {
        var (engine, updater) = CreateEngine();
        updater.RebuildFrom(new Dictionary<int, string>
        {
            [1] = "abxba",
            [2] = "xxababyy",
            [3] = "abab",
        });

        var results = Find(engine, "abab", enableOperators: false,
            method: WordMatchMethod.Within);

        Assert.Equal([2, 3], results.OrderBy(x => x));
    }

    [Fact]
    public void Within_RepeatedBigram_QueryStillRequiresFullSubstring()
    {
        var (engine, updater) = CreateEngine();
        updater.RebuildFrom(new Dictionary<int, string>
        {
            [1] = "aaxaax",
            [2] = "zaaaab",
            [3] = "aaaa",
        });

        var results = Find(engine, "aaaa", enableOperators: false,
            method: WordMatchMethod.Within);

        Assert.Equal([2, 3], results.OrderBy(x => x));
    }

    // ══════════════════════════════════════════════
    //  Edge cases
    // ══════════════════════════════════════════════

    [Fact]
    public void EmptyExpression_ReturnsEmpty()
    {
        var (engine, _) = CreateWatchIndex();

        var results = engine.Find("", WordMatchMethod.Within, true);

        Assert.Empty(results);
    }

    [Fact]
    public void WhitespaceOnlyExpression_ReturnsEmpty()
    {
        var (engine, _) = CreateWatchIndex();

        var results = engine.Find("   ", WordMatchMethod.Within, true);

        Assert.Empty(results);
    }

    [Fact]
    public void EmptyIndex_ReturnsEmpty()
    {
        var (engine, _) = CreateEngine();

        var results = engine.Find("test", WordMatchMethod.Within, true);

        Assert.Empty(results);
    }

    [Fact]
    public void NonexistentWord_ReturnsEmpty()
    {
        var (engine, _) = CreateWatchIndex();

        var results = Find(engine, "nonexistent");

        Assert.Empty(results);
    }

    [Fact]
    public void CaseInsensitive_Search()
    {
        var (engine, _) = CreateWatchIndex();

        var lower = Find(engine, "digital");
        var upper = Find(engine, "DIGITAL");
        var mixed = Find(engine, "DiGiTaL");

        Assert.Equal(lower.OrderBy(x => x), upper.OrderBy(x => x));
        Assert.Equal(lower.OrderBy(x => x), mixed.OrderBy(x => x));
    }

    [Fact]
    public void LeadingBinaryOperator_IsIgnored()
    {
        var (engine, _) = CreateWatchIndex();

        var results = Find(engine, "OR digital");

        Assert.Equal([1, 3], results.OrderBy(x => x));
    }

    [Fact]
    public void TrailingBinaryOperator_IsIgnored()
    {
        var (engine, _) = CreateWatchIndex();

        var results = Find(engine, "digital AND");

        Assert.Equal([1, 3], results.OrderBy(x => x));
    }

    [Fact]
    public void DoubleNot_BehavesLikeSingleNot()
    {
        var (engine, _) = CreateWatchIndex();

        var results = Find(engine, "NOT NOT clock");

        Assert.Equal(4, results.Count);
        Assert.DoesNotContain(4, results);
    }

    [Fact]
    public void CountMatches_Exact_EqualsFindCount()
    {
        var (engine, _) = CreateWatchIndex();

        var count = engine.CountMatches("digital", WordMatchMethod.Exact, false);
        var results = engine.Find("digital", WordMatchMethod.Exact, false);

        Assert.Equal(results.Count, count);
    }

    [Fact]
    public void CountMatches_ExactAnd_EqualsFindCount()
    {
        var (engine, _) = CreateWatchIndex();

        var count = engine.CountMatches("digital AND watch", WordMatchMethod.Exact, true);
        var results = engine.Find("digital AND watch", WordMatchMethod.Exact, true);

        Assert.Equal(results.Count, count);
        Assert.Equal([1, 3], results.OrderBy(x => x));
    }

    [Fact]
    public void Find_ExactImplicitAnd_ReturnsIntersection()
    {
        var (engine, _) = CreateWatchIndex();

        var results = engine.Find("digital watch", WordMatchMethod.Exact, true);

        Assert.Equal([1, 3], results.OrderBy(x => x));
    }

    [Fact]
    public void CountMatches_WithOperators_EqualsFindCount()
    {
        var (engine, _) = CreateWatchIndex();

        var count = engine.CountMatches("(digital OR analog) AND watch NOT GA",
            WordMatchMethod.Within, true);
        var results = engine.Find("(digital OR analog) AND watch NOT GA",
            WordMatchMethod.Within, true);

        Assert.Equal(results.Count, count);
    }
}

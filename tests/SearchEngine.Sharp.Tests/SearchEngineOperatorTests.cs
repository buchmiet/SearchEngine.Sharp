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

    [Test]
    public async Task ImplicitAnd_TwoWords_BothMustMatch()
    {
        var (engine, _) = CreateWatchIndex();

        // "digital watch" → implicit AND → docs with BOTH "digital" and "watch"
        var results = Find(engine, "digital watch");

        await Assert.That(results.Count).IsEqualTo(2);
        await Assert.That(results).Contains(1); // GA-100 digital watch
        await Assert.That(results).Contains(3); // DW-5600 digital watch
        await Assert.That(results).DoesNotContain(4); // "clock", not "watch"
    }

    [Test]
    public async Task ExplicitAnd_SameAsImplicit()
    {
        var (engine, _) = CreateWatchIndex();

        var implicit_ = Find(engine, "digital watch");
        var explicit_ = Find(engine, "digital AND watch");

        await Assert.That(explicit_.OrderBy(x => x)).IsEquivalentTo(implicit_.OrderBy(x => x), CollectionOrdering.Matching);
    }

    // ══════════════════════════════════════════════
    //  OR
    // ══════════════════════════════════════════════

    [Test]
    public async Task Or_ReturnsUnionOfBothSides()
    {
        var (engine, _) = CreateWatchIndex();

        // "digital OR analog" → any doc with either word
        var results = Find(engine, "digital OR analog");

        await Assert.That(results.Count).IsEqualTo(4);
        await Assert.That(results).Contains(1); // digital
        await Assert.That(results).Contains(2); // analog
        await Assert.That(results).Contains(3); // digital
        await Assert.That(results).Contains(4); // analog
        await Assert.That(results).DoesNotContain(5); // solar — neither digital nor analog
    }

    [Test]
    public async Task Or_SingleMatch_OnOneSide()
    {
        var (engine, _) = CreateWatchIndex();

        // "solar OR clock" → PRW-3000 (solar) + DW-6900 (clock)
        var results = Find(engine, "solar OR clock");

        await Assert.That(results.Count).IsEqualTo(2);
        await Assert.That(results).Contains(4); // clock
        await Assert.That(results).Contains(5); // solar
    }

    // ══════════════════════════════════════════════
    //  NOT
    // ══════════════════════════════════════════════

    [Test]
    public async Task Not_ExcludesMatchingDocs()
    {
        var (engine, _) = CreateWatchIndex();

        // "watch NOT digital" → has "watch" but NOT "digital"
        var results = Find(engine, "watch NOT digital");

        await Assert.That(results.Count).IsEqualTo(2);
        await Assert.That(results).Contains(2); // GA-200 analog watch
        await Assert.That(results).Contains(5); // PRW-3000 solar watch
        await Assert.That(results).DoesNotContain(1); // GA-100 digital watch — excluded
        await Assert.That(results).DoesNotContain(3); // DW-5600 digital watch — excluded
    }

    [Test]
    public async Task Not_Standalone_ReturnsAllExceptMatch()
    {
        var (engine, _) = CreateWatchIndex();

        // "NOT clock" → everything except docs with "clock"
        var results = Find(engine, "NOT clock");

        await Assert.That(results.Count).IsEqualTo(4);
        await Assert.That(results).DoesNotContain(4); // DW-6900 analog clock — excluded
    }

    [Test]
    public async Task Not_NoMatches_ReturnsAll()
    {
        var (engine, _) = CreateWatchIndex();

        // "NOT nonexistent" → NOT matches nothing → all docs returned
        var results = Find(engine, "NOT nonexistent");

        await Assert.That(results.Count).IsEqualTo(5);
    }

    // ══════════════════════════════════════════════
    //  Operator precedence: AND binds tighter than OR
    // ══════════════════════════════════════════════

    [Test]
    public async Task Precedence_AndBindsTighterThanOr()
    {
        var (engine, _) = CreateWatchIndex();

        // "solar OR digital AND watch" → solar OR (digital AND watch)
        // = PRW-3000(solar) + GA-100(digital watch) + DW-5600(digital watch)
        var results = Find(engine, "solar OR digital AND watch");

        await Assert.That(results.Count).IsEqualTo(3);
        await Assert.That(results).Contains(1); // GA-100 digital watch
        await Assert.That(results).Contains(3); // DW-5600 digital watch
        await Assert.That(results).Contains(5); // PRW-3000 solar watch
    }

    // ══════════════════════════════════════════════
    //  Parentheses override precedence
    // ══════════════════════════════════════════════

    [Test]
    public async Task Parentheses_OverridePrecedence()
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
        await Assert.That(withoutParens.Count).IsEqualTo(3);

        // With parens: "(red OR green) AND apple" → (red ∪ green) ∩ apple = {1,2}
        var withParens = Find(engine, "(red OR green) AND apple");
        await Assert.That(withParens.Count).IsEqualTo(2);
        await Assert.That(withParens).Contains(1); // red apple
        await Assert.That(withParens).Contains(2); // green apple
        await Assert.That(withParens).DoesNotContain(3); // cherry — not apple
    }

    // ══════════════════════════════════════════════
    //  Complex expressions
    // ══════════════════════════════════════════════

    [Test]
    public async Task Complex_AndOrNotCombined()
    {
        var (engine, _) = CreateWatchIndex();

        // "(digital OR analog) AND watch NOT GA" →
        // (digital ∪ analog) ∩ watch ∩ ¬GA
        // digital|analog = {1,2,3,4}, watch = {1,2,3,5}, AND = {1,2,3}
        // NOT GA = {3,4,5}, AND = {3}
        var results = Find(engine, "(digital OR analog) AND watch NOT GA");

        await Assert.That(results).HasSingleItem();
        await Assert.That(results).Contains(3); // DW-5600 digital watch
    }

    [Test]
    public async Task Complex_ExplicitAndOrNotWithoutParens()
    {
        var (engine, _) = CreateWatchIndex();

        // "watch AND NOT digital AND NOT analog" → watch ∩ ¬digital ∩ ¬analog
        // watch={1,2,3,5}, ¬digital={2,4,5}, ¬analog={1,3,5}
        // = {5} PRW-3000 solar watch
        var results = Find(engine, "watch AND NOT digital AND NOT analog");

        await Assert.That(results).HasSingleItem();
        await Assert.That(results).Contains(5);
    }

    [Test]
    public async Task Complex_NotWithOr()
    {
        var (engine, _) = CreateWatchIndex();

        // "NOT digital AND NOT analog" → docs without digital AND without analog
        // = [5] PRW-3000 solar watch
        var results = Find(engine, "NOT digital AND NOT analog");

        await Assert.That(results).HasSingleItem();
        await Assert.That(results).Contains(5);
    }

    // ══════════════════════════════════════════════
    //  enableOperators = false
    // ══════════════════════════════════════════════

    [Test]
    public async Task OperatorsDisabled_OrTreatedAsWord()
    {
        var (engine, updater) = CreateEngine();
        updater.RebuildFrom(new Dictionary<int, string>
        {
            [1] = "this or that",
            [2] = "something else",
        });

        // With operators disabled, "or" is just a word to search for
        var results = Find(engine, "or", enableOperators: false);

        await Assert.That(results).HasSingleItem();
        await Assert.That(results).Contains(1);
    }

    [Test]
    public async Task OperatorsDisabled_NotTreatedAsWord()
    {
        var (engine, updater) = CreateEngine();
        updater.RebuildFrom(new Dictionary<int, string>
        {
            [1] = "not applicable",
            [2] = "applicable",
        });

        var results = Find(engine, "not", enableOperators: false);

        await Assert.That(results).HasSingleItem();
        await Assert.That(results).Contains(1);
    }

    // ══════════════════════════════════════════════
    //  Exact vs Within with operators
    // ══════════════════════════════════════════════

    [Test]
    public async Task Operators_WorkWithExactMatch()
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

        await Assert.That(results.Count).IsEqualTo(3);
        await Assert.That(results).Contains(1); // alpha
        await Assert.That(results).Contains(2); // alpha, gamma
        await Assert.That(results).Contains(3); // gamma
    }

    [Test]
    public async Task Operators_ExactNotWithin_NoSubstringMatch()
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
        await Assert.That(exact).IsEmpty();

        // Within: "alph" should match both
        var within = Find(engine, "alph", enableOperators: false,
            method: WordMatchMethod.Within);
        await Assert.That(within.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Within_DoesNotReturnFalsePositive_WhenBigramsMatchButSubstringDoesNot()
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

        await Assert.That(results.OrderBy(x => x)).IsEquivalentTo([2, 3], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Within_RepeatedBigram_QueryStillRequiresFullSubstring()
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

        await Assert.That(results.OrderBy(x => x)).IsEquivalentTo([2, 3], CollectionOrdering.Matching);
    }

    // ══════════════════════════════════════════════
    //  Edge cases
    // ══════════════════════════════════════════════

    [Test]
    public async Task EmptyExpression_ReturnsEmpty()
    {
        var (engine, _) = CreateWatchIndex();

        var results = engine.Find("", WordMatchMethod.Within, true);

        await Assert.That(results).IsEmpty();
    }

    [Test]
    public async Task WhitespaceOnlyExpression_ReturnsEmpty()
    {
        var (engine, _) = CreateWatchIndex();

        var results = engine.Find("   ", WordMatchMethod.Within, true);

        await Assert.That(results).IsEmpty();
    }

    [Test]
    public async Task EmptyIndex_ReturnsEmpty()
    {
        var (engine, _) = CreateEngine();

        var results = engine.Find("test", WordMatchMethod.Within, true);

        await Assert.That(results).IsEmpty();
    }

    [Test]
    public async Task NonexistentWord_ReturnsEmpty()
    {
        var (engine, _) = CreateWatchIndex();

        var results = Find(engine, "nonexistent");

        await Assert.That(results).IsEmpty();
    }

    [Test]
    public async Task CaseInsensitive_Search()
    {
        var (engine, _) = CreateWatchIndex();

        var lower = Find(engine, "digital");
        var upper = Find(engine, "DIGITAL");
        var mixed = Find(engine, "DiGiTaL");

        await Assert.That(upper.OrderBy(x => x)).IsEquivalentTo(lower.OrderBy(x => x), CollectionOrdering.Matching);
        await Assert.That(mixed.OrderBy(x => x)).IsEquivalentTo(lower.OrderBy(x => x), CollectionOrdering.Matching);
    }

    [Test]
    public async Task LeadingBinaryOperator_IsIgnored()
    {
        var (engine, _) = CreateWatchIndex();

        var results = Find(engine, "OR digital");

        await Assert.That(results.OrderBy(x => x)).IsEquivalentTo([1, 3], CollectionOrdering.Matching);
    }

    [Test]
    public async Task TrailingBinaryOperator_IsIgnored()
    {
        var (engine, _) = CreateWatchIndex();

        var results = Find(engine, "digital AND");

        await Assert.That(results.OrderBy(x => x)).IsEquivalentTo([1, 3], CollectionOrdering.Matching);
    }

    [Test]
    public async Task DoubleNot_BehavesLikeSingleNot()
    {
        var (engine, _) = CreateWatchIndex();

        var results = Find(engine, "NOT NOT clock");

        await Assert.That(results.Count).IsEqualTo(4);
        await Assert.That(results).DoesNotContain(4);
    }

    [Test]
    public async Task CountMatches_Exact_EqualsFindCount()
    {
        var (engine, _) = CreateWatchIndex();

        var count = engine.CountMatches("digital", WordMatchMethod.Exact, false);
        var results = engine.Find("digital", WordMatchMethod.Exact, false);

        await Assert.That(count).IsEqualTo(results.Count);
    }

    [Test]
    public async Task CountMatches_ExactAnd_EqualsFindCount()
    {
        var (engine, _) = CreateWatchIndex();

        var count = engine.CountMatches("digital AND watch", WordMatchMethod.Exact, true);
        var results = engine.Find("digital AND watch", WordMatchMethod.Exact, true);

        await Assert.That(count).IsEqualTo(results.Count);
        await Assert.That(results.OrderBy(x => x)).IsEquivalentTo([1, 3], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Find_ExactImplicitAnd_ReturnsIntersection()
    {
        var (engine, _) = CreateWatchIndex();

        var results = engine.Find("digital watch", WordMatchMethod.Exact, true);

        await Assert.That(results.OrderBy(x => x)).IsEquivalentTo([1, 3], CollectionOrdering.Matching);
    }

    [Test]
    public async Task CountMatches_WithOperators_EqualsFindCount()
    {
        var (engine, _) = CreateWatchIndex();

        var count = engine.CountMatches("(digital OR analog) AND watch NOT GA",
            WordMatchMethod.Within, true);
        var results = engine.Find("(digital OR analog) AND watch NOT GA",
            WordMatchMethod.Within, true);

        await Assert.That(count).IsEqualTo(results.Count);
    }

    [Test]
    [Arguments("NOT AND digital")]
    [Arguments("NOT OR digital")]
    [Arguments("NOT AND NOT OR digital")]
    public async Task LeadingNotFollowedByBinaryOperator_DoesNotThrow_AndDropsTheDanglingOperators(string expression)
    {
        var (engine, _) = CreateWatchIndex();

        var result = engine.Find(expression, WordMatchMethod.Exact, enableOperators: true);

        await Assert.That(result).IsEquivalentTo(engine.Find("digital", WordMatchMethod.Exact, enableOperators: true), CollectionOrdering.Matching);
    }

    [Test]
    public async Task LeadingNotAnd_FileMaskPreset_DoesNotThrow()
    {
        var provider = new IndexSnapshotProvider();
        var updater = new IndexUpdater(provider, SearchTokenization.FileMask);
        updater.RebuildFrom(new Dictionary<int, string> { [1] = "report.pdf", [2] = "notes.txt" });
        var engine = new SearchEngineSharp(provider);

        await Assert.That(engine.Find("NOT AND *.pdf", WordMatchMethod.Exact, enableOperators: true)).IsEquivalentTo([1], CollectionOrdering.Matching);
    }
}

using SearchEngine.Filters;
using SearchEngine.Ingestion;
using SearchEngine.Snapshots;

namespace SearchEngine.Sharp.Tests;

public class FileMaskTokenizationTests
{
    private static async Task<(SearchEngineSharp engine, IIndexUpdater updater)> CreateAcceptanceIndex()
    {
        var (engine, updater, provider) = TestIndex.Create(SearchTokenization.FileMask);
        updater.RebuildFrom(new Dictionary<int, string>
        {
            [1] = "report-final.pdf",
            [2] = "System",
            [3] = "my-system-backup.zip",
            [4] = "notes.txt",
        });

        await Assert.That(provider.Current.Tokenization).IsEqualTo(SearchTokenization.FileMask);
        return (engine, updater);
    }

    [Test]
    [Arguments("system", new[] { 2 })]
    [Arguments("system*", new[] { 2 })]
    [Arguments("*.pdf", new[] { 1 })]
    [Arguments("*system*", new[] { 2, 3 })]
    [Arguments("*.pdf OR *.txt", new[] { 1, 4 })]
    [Arguments("* AND NOT *.zip", new[] { 1, 2, 4 })]
    [Arguments("*", new[] { 1, 2, 3, 4 })]
    public async Task FileMask_AcceptanceQueries(string query, int[] expectedIds)
    {
        var (engine, _) = await CreateAcceptanceIndex();
        var results = engine.Find(query, WordMatchMethod.Exact, enableOperators: true);
        await Assert.That(results.OrderBy(x => x)).IsEquivalentTo(expectedIds.OrderBy(x => x), CollectionOrdering.Matching);
    }

    [Test]
    public async Task FileMask_Within_MatchesSubstringOfWholeName()
    {
        var (engine, _) = await CreateAcceptanceIndex();
        var results = engine.Find("port", WordMatchMethod.Within, enableOperators: false);
        await Assert.That(results).IsEquivalentTo(new[] { 1 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task FileMask_BareSystem_DoesNotMatchSystemBackupZip()
    {
        var (engine, _) = await CreateAcceptanceIndex();
        var results = engine.Find("system", WordMatchMethod.Exact, enableOperators: false);
        await Assert.That(results).IsEquivalentTo(new[] { 2 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task FileMask_ExactSingleWord_UsesPostingFastPath()
    {
        var (engine, provider, _) = CreateEngineWithProvider();
        engine.Find("system", WordMatchMethod.Exact);
        await Assert.That(provider.Current.Base.ExactPostings.ContainsKey("system")).IsTrue();
        await Assert.That(engine.Find("system", WordMatchMethod.Exact)).IsEquivalentTo(new[] { 2 }, CollectionOrdering.Matching);
        await Assert.That(engine.CountMatches("system", WordMatchMethod.Exact)).IsEqualTo(1);
    }

    [Test]
    public async Task FileMask_GlobTerm_BypassesExactFastPath()
    {
        var (engine, _, _) = CreateEngineWithProvider();
        var results = engine.Find("system*", WordMatchMethod.Exact, enableOperators: false);
        await Assert.That(results).IsEquivalentTo(new[] { 2 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task FileMask_FacetFilter_WorksUnchanged()
    {
        var (engine, updater, provider) = TestIndex.Create(SearchTokenization.FileMask);
        updater.RebuildFrom(new Dictionary<int, IndexedEntry>
        {
            [1] = new("report-final.pdf", "report-final.pdf", FacetValues.Create("size", 100)),
            [2] = new("System", "System", FacetValues.Create("size", 500)),
        });

        var filter = FacetFilter.Range("size", 200, long.MaxValue);
        await Assert.That(engine.Find("", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, filter)).IsEquivalentTo(new[] { 2 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task FileMask_NaturalSort_WorksUnchanged()
    {
        var (engine, updater, _) = TestIndex.Create(SearchTokenization.FileMask);
        updater.RebuildFrom(new Dictionary<int, string>
        {
            [10] = "GA-100",
            [11] = "GA-10",
            [12] = "GA-2",
        });

        var results = engine.Find(
            "ga*",
            WordMatchMethod.Exact,
            enableOperators: false,
            SearchSortMode.NaturalSortAscending);

        await Assert.That(results).IsEquivalentTo(new[] { 12, 11, 10 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task DefaultTokenization_MatchesLegacySeparatorBehavior()
    {
        var (engine, updater, provider) = TestIndex.Create(SearchTokenization.Default);
        updater.RebuildFrom(new Dictionary<int, string>
        {
            [1] = "report-final.pdf",
            [2] = "System",
        });

        await Assert.That(provider.Current.Tokenization).IsEqualTo(SearchTokenization.Default);
        await Assert.That(engine.Find("report", WordMatchMethod.Within)).IsEquivalentTo(new[] { 1 }, CollectionOrdering.Matching);
        await Assert.That(engine.Find("report OR system", WordMatchMethod.Exact, enableOperators: true)).IsEquivalentTo(new[] { 1, 2 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ProgressiveIngestion_ProducesFileMaskSnapshots()
    {
        var (engine, updater, provider) = TestIndex.Create(SearchTokenization.FileMask);
        var ingestion = new ProgressiveIndexIngestion(updater);

        static async IAsyncEnumerable<KeyValuePair<int, IndexedEntry>> Feed()
        {
            yield return new(1, new IndexedEntry("report-final.pdf", "report-final.pdf"));
            yield return new(2, new IndexedEntry("System", "System"));
            yield return new(3, new IndexedEntry("my-system-backup.zip", "my-system-backup.zip"));
            await Task.Yield();
            yield return new(4, new IndexedEntry("notes.txt", "notes.txt"));
        }

        var result = await ingestion.IngestAsync(
            Feed(),
            new IngestPublishOptions { Policy = new IngestPublishPolicy.FixedBatch { BatchSize = 2 } });

        await Assert.That(result.CompletedSuccessfully).IsTrue();
        await Assert.That(provider.Current.Tokenization).IsEqualTo(SearchTokenization.FileMask);
        await Assert.That(engine.Find("*.pdf", WordMatchMethod.Exact, enableOperators: true)).IsEquivalentTo(new[] { 1 }, CollectionOrdering.Matching);
        await Assert.That(engine.Find("system", WordMatchMethod.Exact)).IsEquivalentTo(new[] { 2 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task CustomTokenization_CreateFactory()
    {
        var custom = SearchTokenization.Create("-", " ");
        await Assert.That(custom.IndexSeparators).IsEqualTo("-");
        await Assert.That(custom.QuerySeparators).IsEqualTo(" ");

        var snapshot = IndexSnapshotBuilder.Build(
            new Dictionary<int, string> { [1] = "a-b-c" },
            custom);

        await Assert.That(snapshot.Tokenization).IsEqualTo(custom);
        await Assert.That(snapshot.UniqueWordCount).IsEqualTo(3);
    }

    private static (SearchEngineSharp engine, IndexSnapshotProvider provider, IIndexUpdater updater) CreateEngineWithProvider()
    {
        var (engine, updater, provider) = TestIndex.Create(SearchTokenization.FileMask);
        updater.RebuildFrom(new Dictionary<int, string>
        {
            [1] = "report-final.pdf",
            [2] = "System",
            [3] = "my-system-backup.zip",
            [4] = "notes.txt",
        });
        return (engine, provider, updater);
    }

    [Test]
    public async Task LowercaseWholeNameToken_ReusesTheEntryString_UppercaseNameIsLowercasedCopy()
    {
        string lower = string.Concat("x", "report-final.pdf"); // runtime instance, not an interned literal
        string mixed = "Report-Final.PDF";
        var snapshot = SearchEngine.Snapshots.IndexSnapshotBuilder.Build(
            new Dictionary<int, string> { [1] = lower, [2] = mixed },
            SearchTokenization.FileMask);

        await Assert.That(snapshot.Base.ExactPostings.Keys).Contains(key => ReferenceEquals(key, lower));
        await Assert.That(snapshot.Base.ExactPostings.Keys).Contains("report-final.pdf"); // lowercased copy of the mixed-case name
        await Assert.That(snapshot.Base.ExactPostings.Keys).DoesNotContain(key => ReferenceEquals(key, mixed));
    }
}

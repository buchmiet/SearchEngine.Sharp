using SearchEngine.Filters;
using SearchEngine.Ingestion;
using SearchEngine.Snapshots;

namespace SearchEngine.Sharp.Tests;

public class FileMaskTokenizationTests
{
    private static (SearchEngineSharp engine, IIndexUpdater updater, IndexSnapshotProvider provider) CreateEngine()
    {
        var provider = new IndexSnapshotProvider();
        var updater = new IndexUpdater(provider, SearchTokenization.FileMask);
        var engine = new SearchEngineSharp(provider);
        return (engine, updater, provider);
    }

    private static (SearchEngineSharp engine, IIndexUpdater updater) CreateAcceptanceIndex()
    {
        var (engine, updater, provider) = CreateEngine();
        updater.RebuildFrom(new Dictionary<int, string>
        {
            [1] = "report-final.pdf",
            [2] = "System",
            [3] = "my-system-backup.zip",
            [4] = "notes.txt",
        });

        Assert.Equal(SearchTokenization.FileMask, provider.Current.Tokenization);
        return (engine, updater);
    }

    [Theory]
    [InlineData("system", new[] { 2 })]
    [InlineData("system*", new[] { 2 })]
    [InlineData("*.pdf", new[] { 1 })]
    [InlineData("*system*", new[] { 2, 3 })]
    [InlineData("*.pdf OR *.txt", new[] { 1, 4 })]
    [InlineData("* AND NOT *.zip", new[] { 1, 2, 4 })]
    [InlineData("*", new[] { 1, 2, 3, 4 })]
    public void FileMask_AcceptanceQueries(string query, int[] expectedIds)
    {
        var (engine, _) = CreateAcceptanceIndex();
        var results = engine.Find(query, WordMatchMethod.Exact, enableOperators: true);
        Assert.Equal(expectedIds.OrderBy(x => x), results.OrderBy(x => x));
    }

    [Fact]
    public void FileMask_Within_MatchesSubstringOfWholeName()
    {
        var (engine, _) = CreateAcceptanceIndex();
        var results = engine.Find("port", WordMatchMethod.Within, enableOperators: false);
        Assert.Equal(new[] { 1 }, results);
    }

    [Fact]
    public void FileMask_BareSystem_DoesNotMatchSystemBackupZip()
    {
        var (engine, _) = CreateAcceptanceIndex();
        var results = engine.Find("system", WordMatchMethod.Exact, enableOperators: false);
        Assert.Equal(new[] { 2 }, results);
    }

    [Fact]
    public void FileMask_ExactSingleWord_UsesPostingFastPath()
    {
        var (engine, provider, _) = CreateEngineWithProvider();
        engine.Find("system", WordMatchMethod.Exact);
        Assert.True(provider.Current.ExactPostings.ContainsKey("system"));
        Assert.Equal(new[] { 2 }, engine.Find("system", WordMatchMethod.Exact));
        Assert.Equal(1, engine.CountMatches("system", WordMatchMethod.Exact));
    }

    [Fact]
    public void FileMask_GlobTerm_BypassesExactFastPath()
    {
        var (engine, _, _) = CreateEngineWithProvider();
        var results = engine.Find("system*", WordMatchMethod.Exact, enableOperators: false);
        Assert.Equal(new[] { 2 }, results);
    }

    [Fact]
    public void FileMask_FacetFilter_WorksUnchanged()
    {
        var provider = new IndexSnapshotProvider();
        var updater = new IndexUpdater(provider, SearchTokenization.FileMask);
        var engine = new SearchEngineSharp(provider);
        updater.RebuildFrom(new Dictionary<int, IndexedEntry>
        {
            [1] = new("report-final.pdf", "report-final.pdf", FacetValues.Create("size", 100)),
            [2] = new("System", "System", FacetValues.Create("size", 500)),
        });

        var filter = FacetFilter.Range("size", 200, long.MaxValue);
        Assert.Equal(new[] { 2 }, engine.Find("", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, filter));
    }

    [Fact]
    public void FileMask_NaturalSort_WorksUnchanged()
    {
        var (engine, updater, _) = CreateEngine();
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

        Assert.Equal(new[] { 12, 11, 10 }, results);
    }

    [Fact]
    public void DefaultTokenization_MatchesLegacySeparatorBehavior()
    {
        var provider = new IndexSnapshotProvider();
        var updater = new IndexUpdater(provider, SearchTokenization.Default);
        var engine = new SearchEngineSharp(provider);
        updater.RebuildFrom(new Dictionary<int, string>
        {
            [1] = "report-final.pdf",
            [2] = "System",
        });

        Assert.Equal(SearchTokenization.Default, provider.Current.Tokenization);
        Assert.Equal(new[] { 1 }, engine.Find("report", WordMatchMethod.Within));
        Assert.Equal(new[] { 1, 2 }, engine.Find("report OR system", WordMatchMethod.Exact, enableOperators: true));
    }

    [Fact]
    public async Task ProgressiveIngestion_ProducesFileMaskSnapshots()
    {
        var provider = new IndexSnapshotProvider();
        var updater = new IndexUpdater(provider, SearchTokenization.FileMask);
        var engine = new SearchEngineSharp(provider);
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
            new IngestPublishOptions { Policy = IngestPublishPolicy.FixedBatch, FixedBatchSize = 2 });

        Assert.True(result.CompletedSuccessfully);
        Assert.Equal(SearchTokenization.FileMask, provider.Current.Tokenization);
        Assert.Equal(new[] { 1 }, engine.Find("*.pdf", WordMatchMethod.Exact, enableOperators: true));
        Assert.Equal(new[] { 2 }, engine.Find("system", WordMatchMethod.Exact));
    }

    [Fact]
    public void CustomTokenization_CreateFactory()
    {
        var custom = SearchTokenization.Create("-", " ");
        Assert.Equal("-", custom.IndexSeparators);
        Assert.Equal(" ", custom.QuerySeparators);

        var snapshot = IndexSnapshotBuilder.Build(
            new Dictionary<int, string> { [1] = "a-b-c" },
            custom);

        Assert.Equal(custom, snapshot.Tokenization);
        Assert.Equal(3, snapshot.UniqueWordCount);
    }

    private static (SearchEngineSharp engine, IndexSnapshotProvider provider, IIndexUpdater updater) CreateEngineWithProvider()
    {
        var (engine, updater, provider) = CreateEngine();
        updater.RebuildFrom(new Dictionary<int, string>
        {
            [1] = "report-final.pdf",
            [2] = "System",
            [3] = "my-system-backup.zip",
            [4] = "notes.txt",
        });
        return (engine, provider, updater);
    }
}

using Microsoft.Extensions.DependencyInjection;
using SearchEngine.DependencyInjection;

namespace SearchEngine.Sharp.Tests;

public class DependencyInjectionTests
{
    private static Dictionary<int, string> InitialEntries() => new()
    {
        [1] = "alpha-report.pdf",
        [2] = "beta-notes.txt",
    };

    private static int[] Ids(ISearchEngine engine, string query)
        => [.. engine.Find(query, WordMatchMethod.Within).Order()];

    // Resolves the index the way an application does: the updater makes the first change and the
    // engine answers queries. The entries registered up front must still be found afterwards.
    private static async Task AssertFirstUpdateKeepsInitialEntries(IServiceProvider provider, object? key = null)
    {
        var updater = key is null ? provider.GetRequiredService<IIndexUpdater>() : provider.GetRequiredKeyedService<IIndexUpdater>(key);
        var engine = key is null ? provider.GetRequiredService<ISearchEngine>() : provider.GetRequiredKeyedService<ISearchEngine>(key);

        await Assert.That(Ids(engine, "a")).IsEquivalentTo([1, 2], CollectionOrdering.Matching);
        await Assert.That(updater.EntryCount).IsEqualTo(2);

        updater.AddEntry(3, "gamma-data.csv");

        await Assert.That(updater.EntryCount).IsEqualTo(3);
        await Assert.That(Ids(engine, "a")).IsEquivalentTo([1, 2, 3], CollectionOrdering.Matching);
        await Assert.That(Ids(engine, "alpha")).IsEquivalentTo([1], CollectionOrdering.Matching);
        await Assert.That(Ids(engine, "gamma")).IsEquivalentTo([3], CollectionOrdering.Matching);
    }

    [Test]
    public async Task AddSearchEngine_WithInitialEntries_UpdaterKeepsThem()
    {
        var services = new ServiceCollection().AddSearchEngine(InitialEntries());
        using var provider = services.BuildServiceProvider();

        await AssertFirstUpdateKeepsInitialEntries(provider);
    }

    [Test]
    public async Task AddSearchEngine_WithInitialEntriesAndTokenization_UpdaterKeepsThemAndTheTokenization()
    {
        var services = new ServiceCollection().AddSearchEngine(InitialEntries(), SearchTokenization.FileMask);
        using var provider = services.BuildServiceProvider();

        var snapshots = provider.GetRequiredService<IIndexSnapshotProvider>();
        var updater = provider.GetRequiredService<IIndexUpdater>();
        var engine = provider.GetRequiredService<ISearchEngine>();

        await Assert.That(snapshots.Current.Tokenization).IsEqualTo(SearchTokenization.FileMask);
        updater.AddEntry(3, "gamma-data.csv");

        await Assert.That(snapshots.Current.Tokenization).IsEqualTo(SearchTokenization.FileMask);
        await Assert.That(engine.Find("*.pdf", WordMatchMethod.Exact, enableOperators: true)).IsEquivalentTo([1], CollectionOrdering.Matching);
        await Assert.That(engine.Find("*.csv", WordMatchMethod.Exact, enableOperators: true)).IsEquivalentTo([3], CollectionOrdering.Matching);
        await Assert.That(engine.Find("*.txt", WordMatchMethod.Exact, enableOperators: true)).IsEquivalentTo([2], CollectionOrdering.Matching);
    }

    [Test]
    public async Task AddSearchEngine_WithEntriesFactory_UpdaterKeepsTheEntries()
    {
        var services = new ServiceCollection().AddSearchEngine(_ => InitialEntries());
        using var provider = services.BuildServiceProvider();

        await AssertFirstUpdateKeepsInitialEntries(provider);
    }

    [Test]
    public async Task AddSearchEngine_WithEntriesFactoryAndTokenization_UpdaterKeepsTheEntriesAndTheTokenization()
    {
        var services = new ServiceCollection().AddSearchEngine(_ => InitialEntries(), SearchTokenization.FileMask);
        using var provider = services.BuildServiceProvider();

        var snapshots = provider.GetRequiredService<IIndexSnapshotProvider>();
        var updater = provider.GetRequiredService<IIndexUpdater>();
        var engine = provider.GetRequiredService<ISearchEngine>();

        await Assert.That(snapshots.Current.Tokenization).IsEqualTo(SearchTokenization.FileMask);
        updater.AddEntry(3, "gamma-data.csv");

        await Assert.That(snapshots.Current.Tokenization).IsEqualTo(SearchTokenization.FileMask);
        await Assert.That(engine.Find("*.pdf", WordMatchMethod.Exact, enableOperators: true)).IsEquivalentTo([1], CollectionOrdering.Matching);
        await Assert.That(engine.Find("*.csv", WordMatchMethod.Exact, enableOperators: true)).IsEquivalentTo([3], CollectionOrdering.Matching);
    }

    [Test]
    public async Task AddSearchEngine_WithEntriesFactory_RunsOnceForTheIndexAndTheUpdater()
    {
        int calls = 0;
        var services = new ServiceCollection().AddSearchEngine(_ =>
        {
            calls++;
            return InitialEntries();
        });
        using var provider = services.BuildServiceProvider();

        await Assert.That(calls).IsEqualTo(0);
        provider.GetRequiredService<IIndexUpdater>().AddEntry(3, "gamma-data.csv");
        _ = provider.GetRequiredService<IIndexSnapshotProvider>().Current;

        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task AddSearchEngine_WithInitialEntries_IgnoresChangesToTheDictionaryAfterRegistration()
    {
        var entries = InitialEntries();
        var services = new ServiceCollection().AddSearchEngine(entries);
        entries[9] = "late-addition.txt";
        using var provider = services.BuildServiceProvider();

        var engine = provider.GetRequiredService<ISearchEngine>();
        provider.GetRequiredService<IIndexUpdater>().AddEntry(3, "gamma-data.csv");

        await Assert.That(Ids(engine, "a")).IsEquivalentTo([1, 2, 3], CollectionOrdering.Matching);
        await Assert.That(Ids(engine, "late")).IsEmpty();
    }

    [Test]
    public async Task AddKeyedSearchEngine_WithInitialEntries_UpdaterKeepsThem()
    {
        var services = new ServiceCollection().AddKeyedSearchEngine("files", InitialEntries());
        using var provider = services.BuildServiceProvider();

        await AssertFirstUpdateKeepsInitialEntries(provider, "files");
    }

    [Test]
    public async Task AddKeyedSearchEngine_WithInitialEntriesAndTokenization_UpdaterKeepsThemAndTheTokenization()
    {
        var services = new ServiceCollection().AddKeyedSearchEngine("files", InitialEntries(), SearchTokenization.FileMask);
        using var provider = services.BuildServiceProvider();

        var snapshots = provider.GetRequiredKeyedService<IIndexSnapshotProvider>("files");
        var updater = provider.GetRequiredKeyedService<IIndexUpdater>("files");
        var engine = provider.GetRequiredKeyedService<ISearchEngine>("files");

        updater.AddEntry(3, "gamma-data.csv");

        await Assert.That(snapshots.Current.Tokenization).IsEqualTo(SearchTokenization.FileMask);
        await Assert.That(engine.Find("*.pdf", WordMatchMethod.Exact, enableOperators: true)).IsEquivalentTo([1], CollectionOrdering.Matching);
        await Assert.That(engine.Find("*.csv", WordMatchMethod.Exact, enableOperators: true)).IsEquivalentTo([3], CollectionOrdering.Matching);
    }

    [Test]
    public async Task AddKeyedSearchEngine_WithSeveralKeys_KeepsEachIndexSeparateAfterUpdates()
    {
        var services = new ServiceCollection()
            .AddKeyedSearchEngine("one", new Dictionary<int, string> { [1] = "alpha one" })
            .AddKeyedSearchEngine("two", new Dictionary<int, string> { [2] = "beta two" })
            .AddKeyedSearchEngine("empty");
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredKeyedService<IIndexUpdater>("one").AddEntry(11, "alpha eleven");
        provider.GetRequiredKeyedService<IIndexUpdater>("two").AddEntry(22, "beta twenty-two");

        await Assert.That(Ids(provider.GetRequiredKeyedService<ISearchEngine>("one"), "alpha")).IsEquivalentTo([1, 11], CollectionOrdering.Matching);
        await Assert.That(Ids(provider.GetRequiredKeyedService<ISearchEngine>("one"), "beta")).IsEmpty();
        await Assert.That(Ids(provider.GetRequiredKeyedService<ISearchEngine>("two"), "beta")).IsEquivalentTo([2, 22], CollectionOrdering.Matching);
        await Assert.That(Ids(provider.GetRequiredKeyedService<ISearchEngine>("two"), "alpha")).IsEmpty();
        await Assert.That(Ids(provider.GetRequiredKeyedService<ISearchEngine>("empty"), "alpha")).IsEmpty();
        await Assert.That(provider.GetRequiredKeyedService<IIndexUpdater>("empty").EntryCount).IsEqualTo(0);
    }

    [Test]
    public async Task TwoContainersBuiltFromOneRegistration_DoNotShareUpdaterEntries()
    {
        var services = new ServiceCollection().AddSearchEngine(InitialEntries());
        using var first = services.BuildServiceProvider();
        using var second = services.BuildServiceProvider();

        first.GetRequiredService<IIndexUpdater>().AddEntry(3, "gamma-data.csv");
        second.GetRequiredService<IIndexUpdater>().AddEntry(4, "delta-log.txt");

        await Assert.That(Ids(first.GetRequiredService<ISearchEngine>(), "a")).IsEquivalentTo([1, 2, 3], CollectionOrdering.Matching);
        await Assert.That(Ids(second.GetRequiredService<ISearchEngine>(), "a")).IsEquivalentTo([1, 2, 4], CollectionOrdering.Matching);
    }
}

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
    private static void AssertFirstUpdateKeepsInitialEntries(IServiceProvider provider, object? key = null)
    {
        var updater = key is null ? provider.GetRequiredService<IIndexUpdater>() : provider.GetRequiredKeyedService<IIndexUpdater>(key);
        var engine = key is null ? provider.GetRequiredService<ISearchEngine>() : provider.GetRequiredKeyedService<ISearchEngine>(key);

        Assert.Equal([1, 2], Ids(engine, "a"));
        Assert.Equal(2, updater.EntryCount);

        updater.AddEntry(3, "gamma-data.csv");

        Assert.Equal(3, updater.EntryCount);
        Assert.Equal([1, 2, 3], Ids(engine, "a"));
        Assert.Equal([1], Ids(engine, "alpha"));
        Assert.Equal([3], Ids(engine, "gamma"));
    }

    [Fact]
    public void AddSearchEngine_WithInitialEntries_UpdaterKeepsThem()
    {
        var services = new ServiceCollection().AddSearchEngine(InitialEntries());
        using var provider = services.BuildServiceProvider();

        AssertFirstUpdateKeepsInitialEntries(provider);
    }

    [Fact]
    public void AddSearchEngine_WithInitialEntriesAndTokenization_UpdaterKeepsThemAndTheTokenization()
    {
        var services = new ServiceCollection().AddSearchEngine(InitialEntries(), SearchTokenization.FileMask);
        using var provider = services.BuildServiceProvider();

        var snapshots = provider.GetRequiredService<IIndexSnapshotProvider>();
        var updater = provider.GetRequiredService<IIndexUpdater>();
        var engine = provider.GetRequiredService<ISearchEngine>();

        Assert.Equal(SearchTokenization.FileMask, snapshots.Current.Tokenization);
        updater.AddEntry(3, "gamma-data.csv");

        Assert.Equal(SearchTokenization.FileMask, snapshots.Current.Tokenization);
        Assert.Equal([1], engine.Find("*.pdf", WordMatchMethod.Exact, enableOperators: true));
        Assert.Equal([3], engine.Find("*.csv", WordMatchMethod.Exact, enableOperators: true));
        Assert.Equal([2], engine.Find("*.txt", WordMatchMethod.Exact, enableOperators: true));
    }

    [Fact]
    public void AddSearchEngine_WithEntriesFactory_UpdaterKeepsTheEntries()
    {
        var services = new ServiceCollection().AddSearchEngine(_ => InitialEntries());
        using var provider = services.BuildServiceProvider();

        AssertFirstUpdateKeepsInitialEntries(provider);
    }

    [Fact]
    public void AddSearchEngine_WithEntriesFactoryAndTokenization_UpdaterKeepsTheEntriesAndTheTokenization()
    {
        var services = new ServiceCollection().AddSearchEngine(_ => InitialEntries(), SearchTokenization.FileMask);
        using var provider = services.BuildServiceProvider();

        var snapshots = provider.GetRequiredService<IIndexSnapshotProvider>();
        var updater = provider.GetRequiredService<IIndexUpdater>();
        var engine = provider.GetRequiredService<ISearchEngine>();

        Assert.Equal(SearchTokenization.FileMask, snapshots.Current.Tokenization);
        updater.AddEntry(3, "gamma-data.csv");

        Assert.Equal(SearchTokenization.FileMask, snapshots.Current.Tokenization);
        Assert.Equal([1], engine.Find("*.pdf", WordMatchMethod.Exact, enableOperators: true));
        Assert.Equal([3], engine.Find("*.csv", WordMatchMethod.Exact, enableOperators: true));
    }

    [Fact]
    public void AddSearchEngine_WithEntriesFactory_RunsOnceForTheIndexAndTheUpdater()
    {
        int calls = 0;
        var services = new ServiceCollection().AddSearchEngine(_ =>
        {
            calls++;
            return InitialEntries();
        });
        using var provider = services.BuildServiceProvider();

        Assert.Equal(0, calls);
        provider.GetRequiredService<IIndexUpdater>().AddEntry(3, "gamma-data.csv");
        _ = provider.GetRequiredService<IIndexSnapshotProvider>().Current;

        Assert.Equal(1, calls);
    }

    [Fact]
    public void AddSearchEngine_WithInitialEntries_IgnoresChangesToTheDictionaryAfterRegistration()
    {
        var entries = InitialEntries();
        var services = new ServiceCollection().AddSearchEngine(entries);
        entries[9] = "late-addition.txt";
        using var provider = services.BuildServiceProvider();

        var engine = provider.GetRequiredService<ISearchEngine>();
        provider.GetRequiredService<IIndexUpdater>().AddEntry(3, "gamma-data.csv");

        Assert.Equal([1, 2, 3], Ids(engine, "a"));
        Assert.Empty(Ids(engine, "late"));
    }

    [Fact]
    public void AddKeyedSearchEngine_WithInitialEntries_UpdaterKeepsThem()
    {
        var services = new ServiceCollection().AddKeyedSearchEngine("files", InitialEntries());
        using var provider = services.BuildServiceProvider();

        AssertFirstUpdateKeepsInitialEntries(provider, "files");
    }

    [Fact]
    public void AddKeyedSearchEngine_WithInitialEntriesAndTokenization_UpdaterKeepsThemAndTheTokenization()
    {
        var services = new ServiceCollection().AddKeyedSearchEngine("files", InitialEntries(), SearchTokenization.FileMask);
        using var provider = services.BuildServiceProvider();

        var snapshots = provider.GetRequiredKeyedService<IIndexSnapshotProvider>("files");
        var updater = provider.GetRequiredKeyedService<IIndexUpdater>("files");
        var engine = provider.GetRequiredKeyedService<ISearchEngine>("files");

        updater.AddEntry(3, "gamma-data.csv");

        Assert.Equal(SearchTokenization.FileMask, snapshots.Current.Tokenization);
        Assert.Equal([1], engine.Find("*.pdf", WordMatchMethod.Exact, enableOperators: true));
        Assert.Equal([3], engine.Find("*.csv", WordMatchMethod.Exact, enableOperators: true));
    }

    [Fact]
    public void AddKeyedSearchEngine_WithSeveralKeys_KeepsEachIndexSeparateAfterUpdates()
    {
        var services = new ServiceCollection()
            .AddKeyedSearchEngine("one", new Dictionary<int, string> { [1] = "alpha one" })
            .AddKeyedSearchEngine("two", new Dictionary<int, string> { [2] = "beta two" })
            .AddKeyedSearchEngine("empty");
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredKeyedService<IIndexUpdater>("one").AddEntry(11, "alpha eleven");
        provider.GetRequiredKeyedService<IIndexUpdater>("two").AddEntry(22, "beta twenty-two");

        Assert.Equal([1, 11], Ids(provider.GetRequiredKeyedService<ISearchEngine>("one"), "alpha"));
        Assert.Empty(Ids(provider.GetRequiredKeyedService<ISearchEngine>("one"), "beta"));
        Assert.Equal([2, 22], Ids(provider.GetRequiredKeyedService<ISearchEngine>("two"), "beta"));
        Assert.Empty(Ids(provider.GetRequiredKeyedService<ISearchEngine>("two"), "alpha"));
        Assert.Empty(Ids(provider.GetRequiredKeyedService<ISearchEngine>("empty"), "alpha"));
        Assert.Equal(0, provider.GetRequiredKeyedService<IIndexUpdater>("empty").EntryCount);
    }

    [Fact]
    public void TwoContainersBuiltFromOneRegistration_DoNotShareUpdaterEntries()
    {
        var services = new ServiceCollection().AddSearchEngine(InitialEntries());
        using var first = services.BuildServiceProvider();
        using var second = services.BuildServiceProvider();

        first.GetRequiredService<IIndexUpdater>().AddEntry(3, "gamma-data.csv");
        second.GetRequiredService<IIndexUpdater>().AddEntry(4, "delta-log.txt");

        Assert.Equal([1, 2, 3], Ids(first.GetRequiredService<ISearchEngine>(), "a"));
        Assert.Equal([1, 2, 4], Ids(second.GetRequiredService<ISearchEngine>(), "a"));
    }
}

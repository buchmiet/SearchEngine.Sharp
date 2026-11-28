using Microsoft.Extensions.DependencyInjection;
using SearchEngine.DependencyInjection;

namespace SearchEngine.Sharp.Tests;

// AddSearchEngine and AddKeyedSearchEngine register a snapshot provider, an updater and an engine for one index
// (src/DependencyInjection/ServiceCollectionExtensions.cs). The registered updater must start from the same entries and
// the same preset as the first snapshot, so the first change an application makes does not drop the initial entries.
// Every registration overload (entries, entries factory, keyed, transient engine) is its own path. That the updater also keeps the base
// segment of the initial snapshot is in IndexUpdaterSegmentTests; null arguments are in NullArgumentTests.
public class DependencyInjectionTests
{
    private static Dictionary<int, string> InitialEntries() => new()
    {
        [1] = "alpha-report.pdf",
        [2] = "beta-notes.txt",
    };

    private const string Key = "files";

    // The registration overloads that take initial entries, with or without a preset.
    private static ServiceProvider Register(string registration, SearchTokenization? preset = null) => registration switch
    {
        "entries" => (preset is null
            ? new ServiceCollection().AddSearchEngine(InitialEntries())
            : new ServiceCollection().AddSearchEngine(InitialEntries(), preset)).BuildServiceProvider(),
        "factory" => (preset is null
            ? new ServiceCollection().AddSearchEngine(_ => InitialEntries())
            : new ServiceCollection().AddSearchEngine(_ => InitialEntries(), preset)).BuildServiceProvider(),
        _ => (preset is null
            ? new ServiceCollection().AddKeyedSearchEngine(Key, InitialEntries())
            : new ServiceCollection().AddKeyedSearchEngine(Key, InitialEntries(), preset)).BuildServiceProvider(),
    };

    private static T Resolve<T>(IServiceProvider provider, string registration) where T : notnull
        => registration == "keyed" ? provider.GetRequiredKeyedService<T>(Key) : provider.GetRequiredService<T>();

    // Resolves the index the way an application does: the updater makes the first change and the engine answers
    // queries. The entries registered up front must still be found afterwards.
    [Test]
    [Arguments("entries")]
    [Arguments("factory")]
    [Arguments("keyed")]
    public async Task The_first_update_keeps_the_initial_entries(string registration)
    {
        using var provider = Register(registration);
        var updater = Resolve<IIndexUpdater>(provider, registration);
        var engine = Resolve<ISearchEngine>(provider, registration);
        var before = TestIndex.Ids(engine, "a");
        int countBefore = updater.EntryCount;

        updater.AddEntry(3, "gamma-data.csv");

        await Assert.That(before).IsEquivalentTo([1, 2], CollectionOrdering.Matching);
        await Assert.That(countBefore).IsEqualTo(2);
        await Assert.That(updater.EntryCount).IsEqualTo(3);
        await Assert.That(TestIndex.Ids(engine, "a")).IsEquivalentTo([1, 2, 3], CollectionOrdering.Matching);
        await Assert.That(TestIndex.Ids(engine, "alpha")).IsEquivalentTo([1], CollectionOrdering.Matching);
        await Assert.That(TestIndex.Ids(engine, "gamma")).IsEquivalentTo([3], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("entries")]
    [Arguments("factory")]
    [Arguments("keyed")]
    public async Task The_first_update_keeps_the_initial_entries_and_the_preset(string registration)
    {
        using var provider = Register(registration, SearchTokenization.FileMask);
        var snapshots = Resolve<IIndexSnapshotProvider>(provider, registration);
        var updater = Resolve<IIndexUpdater>(provider, registration);
        var engine = Resolve<ISearchEngine>(provider, registration);
        var presetBefore = snapshots.Current.Tokenization;

        updater.AddEntry(3, "gamma-data.csv");

        await Assert.That(presetBefore).IsEqualTo(SearchTokenization.FileMask);
        await Assert.That(snapshots.Current.Tokenization).IsEqualTo(SearchTokenization.FileMask);
        await Assert.That(engine.Find("*.pdf", WordMatchMethod.Exact, enableOperators: true)).IsEquivalentTo([1], CollectionOrdering.Matching);
        await Assert.That(engine.Find("*.csv", WordMatchMethod.Exact, enableOperators: true)).IsEquivalentTo([3], CollectionOrdering.Matching);
        await Assert.That(engine.Find("*.txt", WordMatchMethod.Exact, enableOperators: true)).IsEquivalentTo([2], CollectionOrdering.Matching);
    }

    // Before anything is published, the index already carries the registered preset: an application that reads
    // Current.Tokenization (to build a NameMatcher for the same index, say) gets the right one.
    [Test]
    public async Task An_index_registered_without_entries_starts_empty_with_its_preset()
    {
        var preset = SearchTokenization.FileMask.WithUnicodeMatching(UnicodeMatching.CaseFold);
        using var plain = new ServiceCollection().AddSearchEngine(preset).BuildServiceProvider();
        using var keyed = new ServiceCollection().AddKeyedSearchEngine(Key, preset).BuildServiceProvider();

        var plainPreset = plain.GetRequiredService<IIndexSnapshotProvider>().Current.Tokenization;
        var keyedPreset = keyed.GetRequiredKeyedService<IIndexSnapshotProvider>(Key).Current.Tokenization;

        await Assert.That(plainPreset).IsSameReferenceAs(preset);
        await Assert.That(keyedPreset).IsSameReferenceAs(preset);
    }

    // The engine is registered with the lifetime the overload names, over one provider and one updater per container,
    // so every engine of the container, in every scope, answers from the same index.
    [Test]
    [Arguments("scoped", false)]
    [Arguments("scoped", true)]
    [Arguments("transient", false)]
    [Arguments("transient", true)]
    public async Task The_engine_has_the_registered_lifetime_over_one_shared_index(string lifetime, bool withPreset)
    {
        var preset = SearchTokenization.FileMask;
        var services = (lifetime, withPreset) switch
        {
            ("scoped", false) => new ServiceCollection().AddSearchEngine(),
            ("scoped", true) => new ServiceCollection().AddSearchEngine(preset),
            ("transient", false) => new ServiceCollection().AddSearchEngineTransient(),
            ("transient", true) => new ServiceCollection().AddSearchEngineTransient(preset),
            _ => throw new ArgumentOutOfRangeException(nameof(lifetime)),
        };
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();
        using var otherScope = provider.CreateScope();

        var first = scope.ServiceProvider.GetRequiredService<ISearchEngine>();
        var second = scope.ServiceProvider.GetRequiredService<ISearchEngine>();
        var inOtherScope = otherScope.ServiceProvider.GetRequiredService<ISearchEngine>();
        scope.ServiceProvider.GetRequiredService<IIndexUpdater>().AddEntry(1, "alpha-report.pdf");

        await Assert.That(ReferenceEquals(first, second)).IsEqualTo(lifetime == "scoped");
        await Assert.That(inOtherScope).IsNotSameReferenceAs(first);
        await Assert.That(otherScope.ServiceProvider.GetRequiredService<IIndexUpdater>())
            .IsSameReferenceAs(scope.ServiceProvider.GetRequiredService<IIndexUpdater>());
        foreach (var engine in new[] { first, second, inOtherScope })
            await Assert.That(TestIndex.Ids(engine, "alpha")).IsEquivalentTo([1], CollectionOrdering.Matching);
        await Assert.That(provider.GetRequiredService<IIndexSnapshotProvider>().Current.Tokenization)
            .IsSameReferenceAs(withPreset ? preset : SearchTokenization.Default);
    }

    [Test]
    public async Task The_entries_factory_runs_once_for_the_index_and_the_updater()
    {
        int calls = 0;
        var services = new ServiceCollection().AddSearchEngine(_ =>
        {
            calls++;
            return InitialEntries();
        });
        using var provider = services.BuildServiceProvider();
        int callsBeforeResolving = calls;

        provider.GetRequiredService<IIndexUpdater>().AddEntry(3, "gamma-data.csv");
        _ = provider.GetRequiredService<IIndexSnapshotProvider>().Current;

        await Assert.That(callsBeforeResolving).IsEqualTo(0);
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task Changes_to_the_entries_after_registration_are_not_indexed()
    {
        var entries = InitialEntries();
        var services = new ServiceCollection().AddSearchEngine(entries);
        entries[9] = "late-addition.txt";
        using var provider = services.BuildServiceProvider();
        var engine = provider.GetRequiredService<ISearchEngine>();

        provider.GetRequiredService<IIndexUpdater>().AddEntry(3, "gamma-data.csv");

        await Assert.That(TestIndex.Ids(engine, "a")).IsEquivalentTo([1, 2, 3], CollectionOrdering.Matching);
        await Assert.That(TestIndex.Ids(engine, "late")).IsEmpty();
    }

    [Test]
    public async Task Each_key_keeps_its_own_index()
    {
        var services = new ServiceCollection()
            .AddKeyedSearchEngine("one", new Dictionary<int, string> { [1] = "alpha one" })
            .AddKeyedSearchEngine("two", new Dictionary<int, string> { [2] = "beta two" })
            .AddKeyedSearchEngine("empty");
        using var provider = services.BuildServiceProvider();
        var one = provider.GetRequiredKeyedService<ISearchEngine>("one");
        var two = provider.GetRequiredKeyedService<ISearchEngine>("two");
        var empty = provider.GetRequiredKeyedService<ISearchEngine>("empty");

        provider.GetRequiredKeyedService<IIndexUpdater>("one").AddEntry(11, "alpha eleven");
        provider.GetRequiredKeyedService<IIndexUpdater>("two").AddEntry(22, "beta twenty-two");

        await Assert.That(TestIndex.Ids(one, "alpha")).IsEquivalentTo([1, 11], CollectionOrdering.Matching);
        await Assert.That(TestIndex.Ids(one, "beta")).IsEmpty();
        await Assert.That(TestIndex.Ids(two, "beta")).IsEquivalentTo([2, 22], CollectionOrdering.Matching);
        await Assert.That(TestIndex.Ids(two, "alpha")).IsEmpty();
        await Assert.That(TestIndex.Ids(empty, "alpha")).IsEmpty();
        await Assert.That(provider.GetRequiredKeyedService<IIndexUpdater>("empty").EntryCount).IsEqualTo(0);
    }

    [Test]
    public async Task Two_containers_built_from_one_registration_do_not_share_entries()
    {
        var services = new ServiceCollection().AddSearchEngine(InitialEntries());
        using var first = services.BuildServiceProvider();
        using var second = services.BuildServiceProvider();
        var firstEngine = first.GetRequiredService<ISearchEngine>();
        var secondEngine = second.GetRequiredService<ISearchEngine>();

        first.GetRequiredService<IIndexUpdater>().AddEntry(3, "gamma-data.csv");
        second.GetRequiredService<IIndexUpdater>().AddEntry(4, "delta-log.txt");

        await Assert.That(TestIndex.Ids(firstEngine, "a")).IsEquivalentTo([1, 2, 3], CollectionOrdering.Matching);
        await Assert.That(TestIndex.Ids(secondEngine, "a")).IsEquivalentTo([1, 2, 4], CollectionOrdering.Matching);
    }
}

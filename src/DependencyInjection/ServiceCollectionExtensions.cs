using Microsoft.Extensions.DependencyInjection;
using SearchEngine.Snapshots;

namespace SearchEngine.DependencyInjection;

/// <summary>
/// Registers an index (provider, updater and engine) with a service collection.
/// </summary>
/// <remarks>
/// Every method registers <see cref="IIndexSnapshotProvider"/> and <see cref="IIndexUpdater"/> as singletons and
/// <see cref="ISearchEngine"/> as scoped, except <see cref="AddSearchEngineTransient(IServiceCollection)"/> and its
/// overload, which register it as transient. The engine keeps no state, so its lifetime decides only how many
/// instances are created.
/// </remarks>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers an index that uses <see cref="SearchTokenization.Default"/>.
    /// </summary>
    /// <param name="services">The service collection to add the index to.</param>
    /// <returns><paramref name="services"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddSearchEngine(this IServiceCollection services)
        => AddSearchEngine(services, SearchTokenization.Default);

    /// <summary>
    /// Registers an index with a tokenization preset for every snapshot of the index. The provider starts with an
    /// empty snapshot that uses <paramref name="tokenization"/>.
    /// </summary>
    /// <param name="services">The service collection to add the index to.</param>
    /// <param name="tokenization">The preset of every snapshot of the index.</param>
    /// <returns><paramref name="services"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="tokenization"/> is
    /// <see langword="null"/>.</exception>
    public static IServiceCollection AddSearchEngine(this IServiceCollection services, SearchTokenization tokenization)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(tokenization);
        return AddIndexServices(services, key: null, initialIndex: null, tokenization, ServiceLifetime.Scoped);
    }

    /// <summary>
    /// Same as <see cref="AddSearchEngine(IServiceCollection)"/>, but registers <see cref="ISearchEngine"/> as
    /// transient.
    /// </summary>
    /// <param name="services">The service collection to add the index to.</param>
    /// <returns><paramref name="services"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddSearchEngineTransient(this IServiceCollection services)
        => AddSearchEngineTransient(services, SearchTokenization.Default);

    /// <summary>
    /// Same as <see cref="AddSearchEngine(IServiceCollection, SearchTokenization)"/>, but registers
    /// <see cref="ISearchEngine"/> as transient.
    /// </summary>
    /// <param name="services">The service collection to add the index to.</param>
    /// <param name="tokenization">The preset of every snapshot of the index.</param>
    /// <returns><paramref name="services"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="tokenization"/> is
    /// <see langword="null"/>.</exception>
    public static IServiceCollection AddSearchEngineTransient(this IServiceCollection services, SearchTokenization tokenization)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(tokenization);
        return AddIndexServices(services, key: null, initialIndex: null, tokenization, ServiceLifetime.Transient);
    }

    /// <summary>
    /// Registers an index that uses <see cref="SearchTokenization.Default"/> and whose first snapshot is built from
    /// <paramref name="initialEntries"/>.
    /// </summary>
    /// <param name="services">The service collection to add the index to.</param>
    /// <param name="initialEntries">The content of the first snapshot, keyed by the id that queries return.</param>
    /// <returns><paramref name="services"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="initialEntries"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A text in <paramref name="initialEntries"/> is
    /// <see langword="null"/>.</exception>
    /// <remarks>
    /// The entries are copied and checked when this method is called; the snapshot is built when the provider, the
    /// updater or the engine is first resolved. The registered <see cref="IIndexUpdater"/> holds the same entries, so
    /// its changes extend or change them.
    /// </remarks>
    public static IServiceCollection AddSearchEngine(
        this IServiceCollection services,
        IDictionary<int, string> initialEntries)
        => AddSearchEngine(services, initialEntries, SearchTokenization.Default);

    /// <summary>
    /// Registers an index with a tokenization preset and a first snapshot built from
    /// <paramref name="initialEntries"/>.
    /// </summary>
    /// <param name="services">The service collection to add the index to.</param>
    /// <param name="initialEntries">The content of the first snapshot, keyed by the id that queries return.</param>
    /// <param name="tokenization">The preset of every snapshot of the index.</param>
    /// <returns><paramref name="services"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/>, <paramref name="initialEntries"/> or
    /// <paramref name="tokenization"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A text in <paramref name="initialEntries"/> is
    /// <see langword="null"/>.</exception>
    /// <remarks>
    /// The entries are copied and checked when this method is called; the snapshot is built when the provider, the
    /// updater or the engine is first resolved. The registered <see cref="IIndexUpdater"/> holds the same entries and
    /// uses the same tokenization, so its changes extend or change them.
    /// </remarks>
    public static IServiceCollection AddSearchEngine(
        this IServiceCollection services,
        IDictionary<int, string> initialEntries,
        SearchTokenization tokenization)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(initialEntries);
        ArgumentNullException.ThrowIfNull(tokenization);
        return AddIndexServices(services, key: null, CopiedNow(initialEntries, tokenization), tokenization, ServiceLifetime.Scoped);
    }

    /// <summary>
    /// Registers an index that uses <see cref="SearchTokenization.Default"/> and whose first snapshot is built from
    /// the entries that <paramref name="entriesFactory"/> returns.
    /// </summary>
    /// <param name="services">The service collection to add the index to.</param>
    /// <param name="entriesFactory">Returns the content of the first snapshot, keyed by the id that queries
    /// return.</param>
    /// <returns><paramref name="services"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="entriesFactory"/> is
    /// <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>The factory runs once per container, when the provider, the updater or the engine is first resolved,
    /// and the snapshot is built then. The registered <see cref="IIndexUpdater"/> holds the same entries, so its
    /// changes extend or change them.</para>
    /// <para>When the factory returns <see langword="null"/>, resolving the index throws
    /// <see cref="InvalidOperationException"/>; a <see langword="null"/> text in what it returns throws
    /// <see cref="ArgumentException"/>.</para>
    /// </remarks>
    public static IServiceCollection AddSearchEngine(
        this IServiceCollection services,
        Func<IServiceProvider, IDictionary<int, string>> entriesFactory)
        => AddSearchEngine(services, entriesFactory, SearchTokenization.Default);

    /// <summary>
    /// Registers an index with a tokenization preset and a first snapshot built from the entries that
    /// <paramref name="entriesFactory"/> returns.
    /// </summary>
    /// <param name="services">The service collection to add the index to.</param>
    /// <param name="entriesFactory">Returns the content of the first snapshot, keyed by the id that queries
    /// return.</param>
    /// <param name="tokenization">The preset of every snapshot of the index.</param>
    /// <returns><paramref name="services"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/>, <paramref name="entriesFactory"/> or
    /// <paramref name="tokenization"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>The factory runs once per container, when the provider, the updater or the engine is first resolved,
    /// and the snapshot is built then. The registered <see cref="IIndexUpdater"/> holds the same entries and uses the
    /// same tokenization, so its changes extend or change them.</para>
    /// <para>When the factory returns <see langword="null"/>, resolving the index throws
    /// <see cref="InvalidOperationException"/>; a <see langword="null"/> text in what it returns throws
    /// <see cref="ArgumentException"/>.</para>
    /// </remarks>
    public static IServiceCollection AddSearchEngine(
        this IServiceCollection services,
        Func<IServiceProvider, IDictionary<int, string>> entriesFactory,
        SearchTokenization tokenization)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(entriesFactory);
        ArgumentNullException.ThrowIfNull(tokenization);

        return AddIndexServices(
            services,
            key: null,
            sp => new(CopyEntries(
                entriesFactory(sp) ?? throw new InvalidOperationException("The entries factory returned null."),
                tokenization,
                nameof(entriesFactory))),
            tokenization,
            ServiceLifetime.Scoped);
    }

    /// <summary>
    /// Registers an index under a service key that uses <see cref="SearchTokenization.Default"/>, so several
    /// independent indexes can live in one container. Inject its services with
    /// <c>[FromKeyedServices("key")]</c>.
    /// </summary>
    /// <param name="services">The service collection to add the index to.</param>
    /// <param name="key">The service key of the index's provider, updater and engine.</param>
    /// <returns><paramref name="services"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="key"/> is
    /// <see langword="null"/>.</exception>
    public static IServiceCollection AddKeyedSearchEngine(this IServiceCollection services, string key)
        => AddKeyedSearchEngine(services, key, SearchTokenization.Default);

    /// <summary>
    /// Registers an index under a service key with a tokenization preset, so several independent indexes can live
    /// in one container. Inject its services with <c>[FromKeyedServices("key")]</c>. The provider starts with an
    /// empty snapshot that uses <paramref name="tokenization"/>.
    /// </summary>
    /// <param name="services">The service collection to add the index to.</param>
    /// <param name="key">The service key of the index's provider, updater and engine.</param>
    /// <param name="tokenization">The preset of every snapshot of the index.</param>
    /// <returns><paramref name="services"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/>, <paramref name="key"/> or
    /// <paramref name="tokenization"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddKeyedSearchEngine(
        this IServiceCollection services,
        string key,
        SearchTokenization tokenization)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(tokenization);
        return AddIndexServices(services, key, initialIndex: null, tokenization, ServiceLifetime.Scoped);
    }

    /// <summary>
    /// Registers an index under a service key that uses <see cref="SearchTokenization.Default"/> and whose first
    /// snapshot is built from <paramref name="initialEntries"/>. Otherwise it behaves like
    /// <see cref="AddSearchEngine(IServiceCollection, IDictionary{int, string})"/>.
    /// </summary>
    /// <param name="services">The service collection to add the index to.</param>
    /// <param name="key">The service key of the index's provider, updater and engine.</param>
    /// <param name="initialEntries">The content of the first snapshot, keyed by the id that queries return.</param>
    /// <returns><paramref name="services"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/>, <paramref name="key"/> or
    /// <paramref name="initialEntries"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A text in <paramref name="initialEntries"/> is
    /// <see langword="null"/>.</exception>
    public static IServiceCollection AddKeyedSearchEngine(
        this IServiceCollection services,
        string key,
        IDictionary<int, string> initialEntries)
        => AddKeyedSearchEngine(services, key, initialEntries, SearchTokenization.Default);

    /// <summary>
    /// Registers an index under a service key with a tokenization preset and a first snapshot built from
    /// <paramref name="initialEntries"/>. Otherwise it behaves like
    /// <see cref="AddSearchEngine(IServiceCollection, IDictionary{int, string}, SearchTokenization)"/>.
    /// </summary>
    /// <param name="services">The service collection to add the index to.</param>
    /// <param name="key">The service key of the index's provider, updater and engine.</param>
    /// <param name="initialEntries">The content of the first snapshot, keyed by the id that queries return.</param>
    /// <param name="tokenization">The preset of every snapshot of the index.</param>
    /// <returns><paramref name="services"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/>, <paramref name="key"/>,
    /// <paramref name="initialEntries"/> or <paramref name="tokenization"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A text in <paramref name="initialEntries"/> is
    /// <see langword="null"/>.</exception>
    public static IServiceCollection AddKeyedSearchEngine(
        this IServiceCollection services,
        string key,
        IDictionary<int, string> initialEntries,
        SearchTokenization tokenization)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(initialEntries);
        ArgumentNullException.ThrowIfNull(tokenization);
        return AddIndexServices(services, key, CopiedNow(initialEntries, tokenization), tokenization, ServiceLifetime.Scoped);
    }

    // The one registration of an index, keyed or not: a null key registers ordinary (non-keyed) services, and a
    // keyed lookup with a null key resolves them. The provider starts with a snapshot of the registered preset: empty
    // when initialIndex is null, otherwise built from the entries of initialIndex once per container (StartingIndex),
    // and the updater starts from the same entries and the same snapshot. The updater then publishes nothing until its
    // first change.
    private static IServiceCollection AddIndexServices(
        IServiceCollection services,
        string? key,
        Func<IServiceProvider, InitialIndex>? initialIndex,
        SearchTokenization tokenization,
        ServiceLifetime engineLifetime)
    {
        services.AddKeyedSingleton(key, (sp, _) => initialIndex is null
            ? new StartingIndex(new Dictionary<int, IndexedEntry>(), IndexSnapshot.EmptyFor(tokenization))
            : StartingIndex.Build(initialIndex(sp).Entries, tokenization));
        services.AddKeyedSingleton(key, (sp, k) =>
            new IndexSnapshotProvider(sp.GetRequiredKeyedService<StartingIndex>(k).Snapshot));
        services.AddKeyedSingleton<IIndexUpdater>(key, (sp, k) =>
        {
            var start = sp.GetRequiredKeyedService<StartingIndex>(k);
            return new IndexUpdater(sp.GetRequiredKeyedService<IndexSnapshotProvider>(k), tokenization, start.Snapshot);
        });

        services.AddKeyedSingleton<IIndexSnapshotProvider>(key, (sp, k) =>
            sp.GetRequiredKeyedService<IndexSnapshotProvider>(k));
        services.Add(ServiceDescriptor.DescribeKeyed(
            typeof(ISearchEngine),
            key,
            (sp, k) => new SearchEngineSharp(sp.GetRequiredKeyedService<IIndexSnapshotProvider>(k)),
            engineLifetime));
        return services;
    }

    // The entries an index starts with, folded once: the first snapshot is built from them and the updater starts
    // from them.
    private sealed record InitialIndex(IReadOnlyDictionary<int, IndexedEntry> Entries);

    // The entries and the first snapshot of an index in one container: the provider publishes Snapshot and the
    // updater keeps its base.
    private sealed record StartingIndex(IReadOnlyDictionary<int, IndexedEntry> Entries, IndexSnapshot Snapshot)
    {
        internal static StartingIndex Build(IReadOnlyDictionary<int, IndexedEntry> entries, SearchTokenization tokenization)
            => new(entries, IndexSnapshotBuilder.BuildFolded(entries, tokenization));
    }

    // Copies the entries at registration, so later changes to the caller's dictionary do not reach the index.
    private static Func<IServiceProvider, InitialIndex> CopiedNow(IDictionary<int, string> initialEntries, SearchTokenization tokenization)
    {
        var initial = new InitialIndex(CopyEntries(initialEntries, tokenization, nameof(initialEntries)));
        return _ => initial;
    }

    private static Dictionary<int, IndexedEntry> CopyEntries(IDictionary<int, string> entries, SearchTokenization tokenization, string paramName)
    {
        var copy = new Dictionary<int, IndexedEntry>(entries.Count);
        foreach (var (id, text) in entries)
            copy[id] = tokenization.FoldEntry(IndexedEntry.FromText(id, text, paramName));
        return copy;
    }
}

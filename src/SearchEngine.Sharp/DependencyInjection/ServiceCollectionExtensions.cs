using Microsoft.Extensions.DependencyInjection;
using SearchEngine.Snapshots;

namespace SearchEngine.DependencyInjection;

/// <summary>
/// Extension methods for registering SearchEngine.Sharp services with DI.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers SearchEngine.Sharp with recommended lifetimes:
    /// IIndexSnapshotProvider and IIndexUpdater as singletons; ISearchEngine as scoped.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddSearchEngine(this IServiceCollection services)
        => AddSearchEngine(services, SearchTokenization.Default);

    /// <summary>
    /// Registers SearchEngine.Sharp with a tokenization preset for all index rebuilds.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="tokenization"/> is
    /// <see langword="null"/>.</exception>
    public static IServiceCollection AddSearchEngine(this IServiceCollection services, SearchTokenization tokenization)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(tokenization);
        services.AddSingleton<IndexSnapshotProvider>();
        services.AddSingleton<IIndexSnapshotProvider>(sp => sp.GetRequiredService<IndexSnapshotProvider>());
        services.AddSingleton<IIndexUpdater>(sp =>
            new IndexUpdater(sp.GetRequiredService<IndexSnapshotProvider>(), tokenization));
        services.AddScoped<ISearchEngine, SearchEngineSharp>();
        return services;
    }

    /// <summary>
    /// Same as AddSearchEngine, but registers ISearchEngine as transient.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddSearchEngineTransient(this IServiceCollection services)
        => AddSearchEngineTransient(services, SearchTokenization.Default);

    /// <summary>
    /// Same as AddSearchEngine with a tokenization preset, but registers ISearchEngine as transient.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="tokenization"/> is
    /// <see langword="null"/>.</exception>
    public static IServiceCollection AddSearchEngineTransient(this IServiceCollection services, SearchTokenization tokenization)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(tokenization);
        services.AddSingleton<IndexSnapshotProvider>();
        services.AddSingleton<IIndexSnapshotProvider>(sp => sp.GetRequiredService<IndexSnapshotProvider>());
        services.AddSingleton<IIndexUpdater>(sp =>
            new IndexUpdater(sp.GetRequiredService<IndexSnapshotProvider>(), tokenization));
        services.AddTransient<ISearchEngine, SearchEngineSharp>();
        return services;
    }

    /// <summary>
    /// Registers services and publishes a first snapshot built from <paramref name="initialEntries"/>.
    /// </summary>
    /// <remarks>
    /// The entries are copied when this method is called. The registered <see cref="IIndexUpdater"/> holds the same
    /// entries, so its mutations extend or change them.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="initialEntries"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A text in <paramref name="initialEntries"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddSearchEngine(
        this IServiceCollection services,
        IDictionary<int, string> initialEntries)
        => AddSearchEngine(services, initialEntries, SearchTokenization.Default);

    /// <summary>
    /// Registers services with a tokenization preset and publishes a first snapshot built from
    /// <paramref name="initialEntries"/>.
    /// </summary>
    /// <remarks>
    /// The entries are copied when this method is called. The registered <see cref="IIndexUpdater"/> holds the same
    /// entries and uses the same tokenization, so its mutations extend or change them.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="services"/>, <paramref name="initialEntries"/> or
    /// <paramref name="tokenization"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A text in <paramref name="initialEntries"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddSearchEngine(
        this IServiceCollection services,
        IDictionary<int, string> initialEntries,
        SearchTokenization tokenization)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(initialEntries);
        ArgumentNullException.ThrowIfNull(tokenization);

        var initial = new InitialIndex(CopyEntries(initialEntries, nameof(initialEntries)));
        return AddIndexServices(services, _ => initial, tokenization);
    }

    /// <summary>
    /// Registers services and publishes a first snapshot built from the entries returned by
    /// <paramref name="entriesFactory"/>.
    /// </summary>
    /// <remarks>
    /// <para>The factory runs once, when the index or its <see cref="IIndexUpdater"/> is first resolved. The updater
    /// holds the same entries, so its mutations extend or change them.</para>
    /// <para>When the factory returns <see langword="null"/>, resolving the index throws
    /// <see cref="InvalidOperationException"/>; a <see langword="null"/> text in what it returns throws
    /// <see cref="ArgumentException"/>.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="entriesFactory"/> is
    /// <see langword="null"/>.</exception>
    public static IServiceCollection AddSearchEngine(
        this IServiceCollection services,
        Func<IServiceProvider, IDictionary<int, string>> entriesFactory)
        => AddSearchEngine(services, entriesFactory, SearchTokenization.Default);

    /// <summary>
    /// Registers services with a tokenization preset and publishes a first snapshot built from the entries
    /// returned by <paramref name="entriesFactory"/>.
    /// </summary>
    /// <remarks>
    /// <para>The factory runs once, when the index or its <see cref="IIndexUpdater"/> is first resolved. The updater
    /// holds the same entries and uses the same tokenization, so its mutations extend or change them.</para>
    /// <para>When the factory returns <see langword="null"/>, resolving the index throws
    /// <see cref="InvalidOperationException"/>; a <see langword="null"/> text in what it returns throws
    /// <see cref="ArgumentException"/>.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="services"/>, <paramref name="entriesFactory"/> or
    /// <paramref name="tokenization"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddSearchEngine(
        this IServiceCollection services,
        Func<IServiceProvider, IDictionary<int, string>> entriesFactory,
        SearchTokenization tokenization)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(entriesFactory);
        ArgumentNullException.ThrowIfNull(tokenization);

        return AddIndexServices(services, sp => new InitialIndex(CopyEntries(
            entriesFactory(sp) ?? throw new InvalidOperationException("The entries factory returned null."),
            nameof(entriesFactory))), tokenization);
    }

    private static IServiceCollection AddIndexServices(
        IServiceCollection services,
        Func<IServiceProvider, InitialIndex> initialIndex,
        SearchTokenization tokenization)
    {
        services.AddSingleton(initialIndex);
        services.AddSingleton<IndexSnapshotProvider>(sp => CreateProvider(sp.GetRequiredService<InitialIndex>(), tokenization));
        services.AddSingleton<IIndexSnapshotProvider>(sp => sp.GetRequiredService<IndexSnapshotProvider>());
        services.AddSingleton<IIndexUpdater>(sp => new IndexUpdater(
            sp.GetRequiredService<IndexSnapshotProvider>(), tokenization, sp.GetRequiredService<InitialIndex>().Entries));
        services.AddScoped<ISearchEngine, SearchEngineSharp>();
        return services;
    }

    /// <summary>
    /// Registers keyed services for multiple independent indexes.
    /// Use [FromKeyedServices("key")] to inject.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="key"/> is
    /// <see langword="null"/>.</exception>
    public static IServiceCollection AddKeyedSearchEngine(this IServiceCollection services, string key)
        => AddKeyedSearchEngine(services, key, SearchTokenization.Default);

    /// <summary>
    /// Registers keyed services for multiple independent indexes with a tokenization preset.
    /// Use [FromKeyedServices("key")] to inject.
    /// </summary>
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
        services.AddKeyedSingleton<IndexSnapshotProvider>(key);
        services.AddKeyedSingleton<IIndexSnapshotProvider>(key, (sp, k) =>
            sp.GetRequiredKeyedService<IndexSnapshotProvider>(k));
        services.AddKeyedSingleton<IIndexUpdater>(key, (sp, k) =>
            new IndexUpdater(sp.GetRequiredKeyedService<IndexSnapshotProvider>(k), tokenization));
        services.AddKeyedScoped<ISearchEngine>(key, (sp, k) =>
            new SearchEngineSharp(sp.GetRequiredKeyedService<IIndexSnapshotProvider>(k)));
        return services;
    }

    /// <summary>
    /// Registers keyed services and publishes a first snapshot built from <paramref name="initialEntries"/>.
    /// The keyed <see cref="IIndexUpdater"/> holds the same entries (see
    /// <see cref="AddSearchEngine(IServiceCollection, IDictionary{int, string})"/>).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="services"/>, <paramref name="key"/> or
    /// <paramref name="initialEntries"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A text in <paramref name="initialEntries"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddKeyedSearchEngine(
        this IServiceCollection services,
        string key,
        IDictionary<int, string> initialEntries)
        => AddKeyedSearchEngine(services, key, initialEntries, SearchTokenization.Default);

    /// <summary>
    /// Registers keyed services with a tokenization preset and publishes a first snapshot built from
    /// <paramref name="initialEntries"/>. The keyed <see cref="IIndexUpdater"/> holds the same entries and uses the
    /// same tokenization (see <see cref="AddSearchEngine(IServiceCollection, IDictionary{int, string}, SearchTokenization)"/>).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="services"/>, <paramref name="key"/>,
    /// <paramref name="initialEntries"/> or <paramref name="tokenization"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A text in <paramref name="initialEntries"/> is <see langword="null"/>.</exception>
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

        var initial = new InitialIndex(CopyEntries(initialEntries, nameof(initialEntries)));

        services.AddKeyedSingleton<IndexSnapshotProvider>(key, (_, _) => CreateProvider(initial, tokenization));
        services.AddKeyedSingleton<IIndexSnapshotProvider>(key, (sp, k) =>
            sp.GetRequiredKeyedService<IndexSnapshotProvider>(k));
        services.AddKeyedSingleton<IIndexUpdater>(key, (sp, k) =>
            new IndexUpdater(sp.GetRequiredKeyedService<IndexSnapshotProvider>(k), tokenization, initial.Entries));
        services.AddKeyedScoped<ISearchEngine>(key, (sp, k) =>
            new SearchEngineSharp(sp.GetRequiredKeyedService<IIndexSnapshotProvider>(k)));
        return services;
    }

    // The entries an index starts with: the first snapshot is built from them and the updater starts from them.
    private sealed record InitialIndex(IReadOnlyDictionary<int, IndexedEntry> Entries);

    private static Dictionary<int, IndexedEntry> CopyEntries(IDictionary<int, string> entries, string paramName)
    {
        var copy = new Dictionary<int, IndexedEntry>(entries.Count);
        foreach (var (id, text) in entries)
            copy[id] = IndexedEntry.FromText(id, text, paramName);
        return copy;
    }

    private static IndexSnapshotProvider CreateProvider(InitialIndex initial, SearchTokenization tokenization)
    {
        var provider = new IndexSnapshotProvider();
        provider.Publish(IndexSnapshotBuilder.Build(initial.Entries, tokenization));
        return provider;
    }
}

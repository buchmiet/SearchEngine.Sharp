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
    public static IServiceCollection AddSearchEngine(this IServiceCollection services)
        => AddSearchEngine(services, SearchTokenization.Default);

    /// <summary>
    /// Registers SearchEngine.Sharp with a tokenization preset for all index rebuilds.
    /// </summary>
    public static IServiceCollection AddSearchEngine(this IServiceCollection services, SearchTokenization tokenization)
        => AddServices(services, providerFactory: null, tokenization, ServiceLifetime.Scoped);

    /// <summary>
    /// Same as AddSearchEngine, but registers ISearchEngine as transient.
    /// </summary>
    public static IServiceCollection AddSearchEngineTransient(this IServiceCollection services)
        => AddSearchEngineTransient(services, SearchTokenization.Default);

    /// <summary>
    /// Same as AddSearchEngine with a tokenization preset, but registers ISearchEngine as transient.
    /// </summary>
    public static IServiceCollection AddSearchEngineTransient(this IServiceCollection services, SearchTokenization tokenization)
        => AddServices(services, providerFactory: null, tokenization, ServiceLifetime.Transient);

    /// <summary>
    /// Registers services and publishes a pre-built initial snapshot.
    /// </summary>
    public static IServiceCollection AddSearchEngine(
        this IServiceCollection services,
        IDictionary<int, string> initialEntries)
        => AddSearchEngine(services, initialEntries, SearchTokenization.Default);

    /// <summary>
    /// Registers services and publishes a pre-built initial snapshot.
    /// </summary>
    public static IServiceCollection AddSearchEngine(
        this IServiceCollection services,
        IDictionary<int, string> initialEntries,
        SearchTokenization tokenization)
    {
        var snapshot = IndexSnapshotBuilder.Build(initialEntries, tokenization);
        return AddServices(services, _ => ProviderPublishing(snapshot), tokenization, ServiceLifetime.Scoped);
    }

    /// <summary>
    /// Registers keyed services for multiple independent indexes.
    /// Use [FromKeyedServices("key")] to inject.
    /// </summary>
    public static IServiceCollection AddKeyedSearchEngine(this IServiceCollection services, string key)
        => AddKeyedSearchEngine(services, key, SearchTokenization.Default);

    /// <summary>
    /// Registers keyed services for multiple independent indexes with a tokenization preset.
    /// Use [FromKeyedServices("key")] to inject.
    /// </summary>
    public static IServiceCollection AddKeyedSearchEngine(
        this IServiceCollection services,
        string key,
        SearchTokenization tokenization)
        => AddKeyedServices(services, key, initialSnapshot: null, tokenization);

    /// <summary>
    /// Registers keyed services with a pre-built initial snapshot.
    /// </summary>
    public static IServiceCollection AddKeyedSearchEngine(
        this IServiceCollection services,
        string key,
        IDictionary<int, string> initialEntries)
        => AddKeyedSearchEngine(services, key, initialEntries, SearchTokenization.Default);

    /// <summary>
    /// Registers keyed services with a pre-built initial snapshot and tokenization preset.
    /// </summary>
    public static IServiceCollection AddKeyedSearchEngine(
        this IServiceCollection services,
        string key,
        IDictionary<int, string> initialEntries,
        SearchTokenization tokenization)
        => AddKeyedServices(services, key, IndexSnapshotBuilder.Build(initialEntries, tokenization), tokenization);

    /// <summary>
    /// Registers services and builds the initial snapshot from a factory delegate.
    /// </summary>
    public static IServiceCollection AddSearchEngine(
        this IServiceCollection services,
        Func<IServiceProvider, IndexSnapshot> snapshotFactory)
        => AddServices(services, sp => ProviderPublishing(snapshotFactory(sp)), tokenization: null, ServiceLifetime.Scoped);

    // providerFactory null: an empty provider. tokenization null: the container constructs IndexUpdater.
    private static IServiceCollection AddServices(
        IServiceCollection services,
        Func<IServiceProvider, IndexSnapshotProvider>? providerFactory,
        SearchTokenization? tokenization,
        ServiceLifetime engineLifetime)
    {
        if (providerFactory is null)
            services.AddSingleton<IndexSnapshotProvider>();
        else
            services.AddSingleton(providerFactory);

        services.AddSingleton<IIndexSnapshotProvider>(sp => sp.GetRequiredService<IndexSnapshotProvider>());
        if (tokenization is null)
            services.AddSingleton<IIndexUpdater, IndexUpdater>();
        else
            services.AddSingleton<IIndexUpdater>(sp => new IndexUpdater(sp.GetRequiredService<IndexSnapshotProvider>(), tokenization));

        services.Add(ServiceDescriptor.Describe(typeof(ISearchEngine), typeof(SearchEngineSharp), engineLifetime));
        return services;
    }

    private static IServiceCollection AddKeyedServices(
        IServiceCollection services,
        string key,
        IndexSnapshot? initialSnapshot,
        SearchTokenization tokenization)
    {
        if (initialSnapshot is null)
            services.AddKeyedSingleton<IndexSnapshotProvider>(key);
        else
            services.AddKeyedSingleton(key, (_, _) => ProviderPublishing(initialSnapshot));

        services.AddKeyedSingleton<IIndexSnapshotProvider>(key, (sp, k) =>
            sp.GetRequiredKeyedService<IndexSnapshotProvider>(k));
        services.AddKeyedSingleton<IIndexUpdater>(key, (sp, k) =>
            new IndexUpdater(sp.GetRequiredKeyedService<IndexSnapshotProvider>(k), tokenization));
        services.AddKeyedScoped<ISearchEngine>(key, (sp, k) =>
            new SearchEngineSharp(sp.GetRequiredKeyedService<IIndexSnapshotProvider>(k)));
        return services;
    }

    private static IndexSnapshotProvider ProviderPublishing(IndexSnapshot snapshot)
    {
        var provider = new IndexSnapshotProvider();
        provider.Publish(snapshot);
        return provider;
    }
}

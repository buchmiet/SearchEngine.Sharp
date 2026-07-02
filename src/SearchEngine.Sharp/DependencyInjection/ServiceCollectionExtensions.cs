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
    {
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
    public static IServiceCollection AddSearchEngineTransient(this IServiceCollection services)
        => AddSearchEngineTransient(services, SearchTokenization.Default);

    /// <summary>
    /// Same as AddSearchEngine with a tokenization preset, but registers ISearchEngine as transient.
    /// </summary>
    public static IServiceCollection AddSearchEngineTransient(this IServiceCollection services, SearchTokenization tokenization)
    {
        services.AddSingleton<IndexSnapshotProvider>();
        services.AddSingleton<IIndexSnapshotProvider>(sp => sp.GetRequiredService<IndexSnapshotProvider>());
        services.AddSingleton<IIndexUpdater>(sp =>
            new IndexUpdater(sp.GetRequiredService<IndexSnapshotProvider>(), tokenization));
        services.AddTransient<ISearchEngine, SearchEngineSharp>();
        return services;
    }

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

        services.AddSingleton<IndexSnapshotProvider>(_ =>
        {
            var provider = new IndexSnapshotProvider();
            provider.Publish(snapshot);
            return provider;
        });
        services.AddSingleton<IIndexSnapshotProvider>(sp => sp.GetRequiredService<IndexSnapshotProvider>());
        services.AddSingleton<IIndexUpdater>(sp =>
            new IndexUpdater(sp.GetRequiredService<IndexSnapshotProvider>(), tokenization));
        services.AddScoped<ISearchEngine, SearchEngineSharp>();
        return services;
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
    {
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
    {
        var snapshot = IndexSnapshotBuilder.Build(initialEntries, tokenization);

        services.AddKeyedSingleton<IndexSnapshotProvider>(key, (_, _) =>
        {
            var provider = new IndexSnapshotProvider();
            provider.Publish(snapshot);
            return provider;
        });
        services.AddKeyedSingleton<IIndexSnapshotProvider>(key, (sp, k) =>
            sp.GetRequiredKeyedService<IndexSnapshotProvider>(k));
        services.AddKeyedSingleton<IIndexUpdater>(key, (sp, k) =>
            new IndexUpdater(sp.GetRequiredKeyedService<IndexSnapshotProvider>(k), tokenization));
        services.AddKeyedScoped<ISearchEngine>(key, (sp, k) =>
            new SearchEngineSharp(sp.GetRequiredKeyedService<IIndexSnapshotProvider>(k)));
        return services;
    }

    /// <summary>
    /// Registers services and builds the initial snapshot from a factory delegate.
    /// </summary>
    public static IServiceCollection AddSearchEngine(
        this IServiceCollection services,
        Func<IServiceProvider, IndexSnapshot> snapshotFactory)
    {
        services.AddSingleton<IndexSnapshotProvider>(sp =>
        {
            var provider = new IndexSnapshotProvider();
            var snapshot = snapshotFactory(sp);
            provider.Publish(snapshot);
            return provider;
        });
        services.AddSingleton<IIndexSnapshotProvider>(sp => sp.GetRequiredService<IndexSnapshotProvider>());
        services.AddSingleton<IIndexUpdater, IndexUpdater>();
        services.AddScoped<ISearchEngine, SearchEngineSharp>();
        return services;
    }
}

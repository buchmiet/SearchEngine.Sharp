using SearchEngine.Snapshots;

namespace SearchEngine;

/// <summary>
/// Holds the current snapshot of one index. Reading <see cref="Current"/> takes no lock and is safe from any
/// number of threads.
/// </summary>
/// <remarks>
/// Snapshots are published by the <see cref="IndexUpdater"/> created with this provider and by the registration
/// methods of <see cref="DependencyInjection.ServiceCollectionExtensions"/> that take initial content. A publish
/// replaces the reference atomically. A query that read <see cref="Current"/> before a publish keeps using that
/// snapshot to the end; the snapshot never changes, so the query sees a consistent index.
/// </remarks>
public sealed class IndexSnapshotProvider : IIndexSnapshotProvider
{
    private IndexSnapshot _current;

    /// <summary>Creates a provider that holds <see cref="IndexSnapshot.Empty"/> until the first publish. Creating an
    /// <see cref="IndexUpdater"/> for it publishes an empty snapshot with the updater's preset.</summary>
    public IndexSnapshotProvider() => _current = IndexSnapshot.Empty;

    // Starts with the given snapshot (dependency injection: the empty snapshot of the registered preset, or the one
    // built from the initial entries), so queries use the index's preset before an updater is created.
    internal IndexSnapshotProvider(IndexSnapshot initial) => _current = initial;

    /// <summary>
    /// Gets the most recently published snapshot, or <see cref="IndexSnapshot.Empty"/> before the first publish.
    /// </summary>
    public IndexSnapshot Current => Volatile.Read(ref _current);

    /// <summary>
    /// Publishes a new snapshot atomically.
    /// </summary>
    internal void Publish(IndexSnapshot snapshot)
    {
        Interlocked.Exchange(ref _current, snapshot);
    }
}

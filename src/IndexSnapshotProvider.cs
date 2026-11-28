using SearchEngine.Snapshots;

namespace SearchEngine;

/// <summary>
/// Holds the current snapshot of one index. Reading <see cref="Current"/> takes no lock.
/// </summary>
/// <remarks>
/// Snapshots are published by the <see cref="IndexUpdater"/> created with this provider. A provider registered by
/// <see cref="DependencyInjection.ServiceCollectionExtensions"/> starts with a snapshot of the registered preset. A
/// publish replaces the reference atomically. A query that read <see cref="Current"/> before a publish keeps using
/// that snapshot to the end; the content of a snapshot never changes, so the query sees a consistent index.
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
    /// Gets the most recently published snapshot. Before the first publish it is <see cref="IndexSnapshot.Empty"/>,
    /// or for a provider registered by <see cref="DependencyInjection.ServiceCollectionExtensions"/>, the snapshot of
    /// the registered preset and entries.
    /// </summary>
    public IndexSnapshot Current => Volatile.Read(ref _current);

    // A reader sees either the previous snapshot or this one, never a mix.
    internal void Publish(IndexSnapshot snapshot)
    {
        Interlocked.Exchange(ref _current, snapshot);
    }
}

using SearchEngine.Snapshots;

namespace SearchEngine;

/// <summary>
/// Thread-safe provider for the current index snapshot.
/// Uses atomic reference swap for lock-free reads.
/// </summary>
/// <remarks>
/// The read/write protocol is:
///   Write (IndexUpdater): Interlocked.Exchange atomically replaces _current with the
///     new snapshot. Any reader that starts after this point sees the new snapshot.
///   Read (SearchEngineSharp): Volatile.Read ensures the reference is not cached in a CPU
///     register, so each call sees the latest value written by any thread.
///
/// Safety relies on the content of an IndexSnapshot never changing after construction (its
/// lazily built derived structures are thread-safe, see IndexSnapshot). A reader that obtained a
/// reference before a write keeps using the old snapshot for the duration of
/// that query. There is no data race because the old snapshot is never mutated.
/// </remarks>
public sealed class IndexSnapshotProvider : IIndexSnapshotProvider
{
    private IndexSnapshot _current = IndexSnapshot.Empty;

    /// <summary>
    /// Gets the current snapshot. Lock-free volatile read.
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

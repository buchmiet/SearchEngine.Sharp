using SearchEngine.Snapshots;

namespace SearchEngine;

/// <summary>
/// Gives access to the current snapshot of an index. <see cref="Current"/> is safe to read from any number of
/// threads.
/// </summary>
public interface IIndexSnapshotProvider
{
    /// <summary>
    /// Gets the current snapshot. Never <see langword="null"/>. Read it once per query: a later read can return a
    /// newer snapshot.
    /// </summary>
    IndexSnapshot Current { get; }
}

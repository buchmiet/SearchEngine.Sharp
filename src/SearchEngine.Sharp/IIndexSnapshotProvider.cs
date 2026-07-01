using SearchEngine.Snapshots;

namespace SearchEngine;

/// <summary>
/// Provides access to the current index snapshot.
/// Thread-safe for concurrent reads.
/// </summary>
public interface IIndexSnapshotProvider
{
    /// <summary>
    /// Gets the current index snapshot. Never null.
    /// </summary>
    IndexSnapshot Current { get; }
}

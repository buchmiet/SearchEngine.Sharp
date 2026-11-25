using SearchEngine.Snapshots;

namespace SearchEngine.Sharp.Tests;

// Also compiled into benchmarks/SearchEngine.Sharp.Profiling (linked in its project file).
/// <summary>A provider that always returns one snapshot, for code that builds a snapshot directly.</summary>
internal sealed class FixedSnapshotProvider(IndexSnapshot snapshot) : IIndexSnapshotProvider
{
    public IndexSnapshot Current => snapshot;
}

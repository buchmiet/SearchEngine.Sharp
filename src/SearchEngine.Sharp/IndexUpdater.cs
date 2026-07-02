using SearchEngine.Snapshots;

namespace SearchEngine;

// IndexUpdater owns the mutable source of truth (_entries) and is the only place
// that calls IndexSnapshotProvider.Publish. All mutating operations are serialised
// through _rebuildGate so concurrent callers never see a partially-updated index.
//
// Why full rebuild on every mutation?
//   The index data structures (flat arrays, sorted word list, bigram index) do not
//   support incremental updates — inserting or removing a word would require
//   re-sorting and re-packing all arrays. Full rebuild is simple, correct, and fast
//   enough for the expected mutation rates (human-speed edits, not bulk streaming).
//   For bulk loads, the Async variants allow streaming data in before acquiring the
//   lock, so the lock is held only for the final in-memory copy + rebuild.
//
// Thread safety:
//   _rebuildGate serialises all writes to _entries and all calls to Publish.
//   Reads (queries) are lock-free: SearchEngineSharp reads the snapshot reference via
//   Volatile.Read and never touches _entries.

/// <summary>
/// Handles index rebuilding with serialized concurrent access.
/// Maintains source data for convenient add/remove/refresh operations.
/// </summary>
public sealed class IndexUpdater(IndexSnapshotProvider provider, SearchTokenization? tokenization = null) : IIndexUpdater
{
    private readonly IndexSnapshotProvider _provider = provider;
    private readonly SearchTokenization _tokenization = tokenization ?? SearchTokenization.Default;
    private readonly Lock _rebuildGate = new();
    private readonly Dictionary<int, IndexedEntry> _entries = [];

    /// <inheritdoc />
    public int EntryCount
    {
        get { lock (_rebuildGate) return _entries.Count; }
    }

    /// <inheritdoc />
    public bool ContainsEntry(int id)
    {
        lock (_rebuildGate) return _entries.ContainsKey(id);
    }

    // ── Full rebuild (string) ──

    /// <inheritdoc />
    public void RebuildFrom(IDictionary<int, string> entries)
    {
        lock (_rebuildGate)
        {
            _entries.Clear();
            foreach (var (key, value) in entries)
                _entries[key] = new IndexedEntry(value, value);

            RebuildAndPublish();
        }
    }

    /// <inheritdoc />
    public async Task RebuildFromAsync(IAsyncEnumerable<KeyValuePair<int, string>> entries, IProgress<float>? progress = null, CancellationToken ct = default)
    {
        // Consume the async stream outside the lock — awaiting while holding a lock
        // would block the thread pool thread for the duration of the I/O.
        var buffer = new Dictionary<int, IndexedEntry>();
        await foreach (var (key, value) in entries.WithCancellation(ct))
            buffer[key] = new IndexedEntry(value, value);

        lock (_rebuildGate)
        {
            _entries.Clear();
            foreach (var (key, value) in buffer)
                _entries[key] = value;

            RebuildAndPublish(progress);
        }
    }

    // ── Full rebuild (IndexedEntry) ──

    /// <inheritdoc />
    public void RebuildFrom(IDictionary<int, IndexedEntry> entries)
    {
        lock (_rebuildGate)
        {
            _entries.Clear();
            foreach (var (key, value) in entries)
                _entries[key] = value;

            RebuildAndPublish();
        }
    }

    /// <inheritdoc />
    public async Task RebuildFromAsync(IAsyncEnumerable<KeyValuePair<int, IndexedEntry>> entries, IProgress<float>? progress = null, CancellationToken ct = default)
    {
        var buffer = new Dictionary<int, IndexedEntry>();
        await foreach (var (key, value) in entries.WithCancellation(ct))
            buffer[key] = value;

        lock (_rebuildGate)
        {
            _entries.Clear();
            foreach (var (key, value) in buffer)
                _entries[key] = value;

            RebuildAndPublish(progress);
        }
    }

    // ── Single entry operations ──

    /// <inheritdoc />
    public void AddEntry(int id, string text)
    {
        AddEntry(id, new IndexedEntry(text, text));
    }

    /// <inheritdoc />
    public void AddEntry(int id, IndexedEntry entry)
    {
        lock (_rebuildGate)
        {
            _entries[id] = entry;
            RebuildAndPublish();
        }
    }

    /// <inheritdoc />
    public bool RemoveEntry(int id)
    {
        lock (_rebuildGate)
        {
            if (!_entries.Remove(id))
                return false;

            RebuildAndPublish();
            return true;
        }
    }

    /// <inheritdoc />
    public bool RefreshEntry(int id, string text)
    {
        return RefreshEntry(id, new IndexedEntry(text, text));
    }

    /// <inheritdoc />
    public bool RefreshEntry(int id, IndexedEntry entry)
    {
        lock (_rebuildGate)
        {
            bool existed = _entries.ContainsKey(id);
            _entries[id] = entry;
            RebuildAndPublish();
            return existed;
        }
    }

    // ── Batch operations (single rebuild) ──

    /// <inheritdoc />
    public void AddOrUpdateEntries(IEnumerable<KeyValuePair<int, string>> entries)
    {
        lock (_rebuildGate)
        {
            foreach (var (key, value) in entries)
                _entries[key] = new IndexedEntry(value, value);

            RebuildAndPublish();
        }
    }

    /// <inheritdoc />
    public void AddOrUpdateEntries(IEnumerable<KeyValuePair<int, IndexedEntry>> entries)
    {
        lock (_rebuildGate)
        {
            foreach (var (key, value) in entries)
                _entries[key] = value;

            RebuildAndPublish();
        }
    }

    /// <inheritdoc />
    public int RemoveEntries(IEnumerable<int> ids)
    {
        lock (_rebuildGate)
        {
            int removed = 0;
            foreach (var id in ids)
            {
                if (_entries.Remove(id))
                    removed++;
            }

            if (removed > 0)
                RebuildAndPublish();

            return removed;
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_rebuildGate)
        {
            _entries.Clear();
            _provider.Publish(IndexSnapshot.Empty);
        }
    }

    private void RebuildAndPublish(IProgress<float>? progress = null)
    {
        var snapshot = IndexSnapshotBuilder.Build(_entries, _tokenization, progress);
        _provider.Publish(snapshot);
    }
}

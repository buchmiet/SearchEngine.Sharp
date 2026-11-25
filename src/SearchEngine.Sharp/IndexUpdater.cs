using SearchEngine.Snapshots;

namespace SearchEngine;

// IndexUpdater owns the mutable source of truth (_entries) and is the only place
// that calls IndexSnapshotProvider.Publish. All mutating operations are serialised
// through _rebuildGate so concurrent callers never see a partially-updated index.
//
// Why full rebuild on every mutation?
//   The index data structures (flat arrays, sorted word list, bigram index) do not
//   support incremental updates: inserting or removing a word would require
//   re-sorting and re-packing all arrays. A full rebuild is simple and correct, and it is
//   fast enough for interactive edit rates. Bulk loads use the batch operations or
//   ProgressiveIndexIngestion, which rebuild once per batch. The Async rebuild variants
//   read the stream before acquiring the lock, so the lock is held only for the final
//   in-memory copy and the rebuild.
//
// Thread safety:
//   _rebuildGate serialises all writes to _entries and all calls to Publish.
//   Reads (queries) are lock-free: SearchEngineSharp reads the snapshot reference via
//   Volatile.Read and never touches _entries.
//
// Atomicity:
//   A mutation either completes (new snapshot published, _entries updated) or leaves both
//   untouched. Single-entry and batch mutations record the previous value of every id they
//   change and restore it if building the snapshot (or enumerating the caller's entries)
//   throws; full rebuilds fill a new dictionary and only swap it in after the build succeeded.

/// <summary>
/// Keeps the entries of an index and publishes a new snapshot to the <see cref="IndexSnapshotProvider"/> after
/// each change. Changes are serialized; queries read the published snapshot without taking the lock.
/// </summary>
/// <param name="provider">The provider that receives every snapshot this updater builds. Use one updater per
/// provider: two updaters on one provider overwrite each other's snapshots.</param>
/// <param name="tokenization">The preset of every snapshot this updater builds, including the empty one published
/// by <see cref="Clear"/>; <see cref="SearchTokenization.Default"/> when <see langword="null"/>.</param>
/// <remarks>
/// The updater starts empty and does not read the snapshot that <paramref name="provider"/> already holds: its first
/// change publishes a snapshot built only from the entries given to the updater. All members are safe to call from
/// several threads.
/// </remarks>
public sealed class IndexUpdater(IndexSnapshotProvider provider, SearchTokenization? tokenization = null) : IIndexUpdater
{
    private readonly IndexSnapshotProvider _provider = provider;
    private readonly SearchTokenization _tokenization = tokenization ?? SearchTokenization.Default;
    private readonly Lock _rebuildGate = new();
    private Dictionary<int, IndexedEntry> _entries = [];

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
            var next = new Dictionary<int, IndexedEntry>(entries.Count);
            foreach (var (key, value) in entries)
                next[key] = new IndexedEntry(value, value);

            ReplaceAll(next);
        }
    }

    /// <inheritdoc />
    public async Task RebuildFromAsync(IAsyncEnumerable<KeyValuePair<int, string>> entries, IProgress<float>? progress = null, CancellationToken ct = default)
    {
        // Consume the async stream outside the lock: awaiting while holding a lock
        // would block the thread pool thread for the duration of the I/O.
        var buffer = new Dictionary<int, IndexedEntry>();
        await foreach (var (key, value) in entries.WithCancellation(ct))
            buffer[key] = new IndexedEntry(value, value);

        lock (_rebuildGate)
            ReplaceAll(buffer, progress);
    }

    // ── Full rebuild (IndexedEntry) ──

    /// <inheritdoc />
    public void RebuildFrom(IDictionary<int, IndexedEntry> entries)
    {
        lock (_rebuildGate)
            ReplaceAll(new Dictionary<int, IndexedEntry>(entries));
    }

    /// <inheritdoc />
    public async Task RebuildFromAsync(IAsyncEnumerable<KeyValuePair<int, IndexedEntry>> entries, IProgress<float>? progress = null, CancellationToken ct = default)
    {
        var buffer = new Dictionary<int, IndexedEntry>();
        await foreach (var (key, value) in entries.WithCancellation(ct))
            buffer[key] = value;

        lock (_rebuildGate)
            ReplaceAll(buffer, progress);
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
            var undo = new List<Undo>(1);
            try
            {
                Set(id, entry, undo);
                RebuildAndPublish();
            }
            catch
            {
                Rollback(undo);
                throw;
            }
        }
    }

    /// <inheritdoc />
    public bool RemoveEntry(int id)
    {
        lock (_rebuildGate)
        {
            var undo = new List<Undo>(1);
            try
            {
                if (!Unset(id, undo))
                    return false;

                RebuildAndPublish();
                return true;
            }
            catch
            {
                Rollback(undo);
                throw;
            }
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
            var undo = new List<Undo>(1);
            try
            {
                bool existed = Set(id, entry, undo);
                RebuildAndPublish();
                return existed;
            }
            catch
            {
                Rollback(undo);
                throw;
            }
        }
    }

    // ── Batch operations (single rebuild) ──

    /// <inheritdoc />
    public void AddOrUpdateEntries(IEnumerable<KeyValuePair<int, string>> entries)
    {
        lock (_rebuildGate)
        {
            var undo = new List<Undo>();
            try
            {
                foreach (var (key, value) in entries)
                    Set(key, new IndexedEntry(value, value), undo);

                RebuildAndPublish();
            }
            catch
            {
                Rollback(undo);
                throw;
            }
        }
    }

    /// <inheritdoc />
    public void AddOrUpdateEntries(IEnumerable<KeyValuePair<int, IndexedEntry>> entries)
    {
        lock (_rebuildGate)
        {
            var undo = new List<Undo>();
            try
            {
                foreach (var (key, value) in entries)
                    Set(key, value, undo);

                RebuildAndPublish();
            }
            catch
            {
                Rollback(undo);
                throw;
            }
        }
    }

    /// <inheritdoc />
    public int RemoveEntries(IEnumerable<int> ids)
    {
        lock (_rebuildGate)
        {
            var undo = new List<Undo>();
            try
            {
                foreach (var id in ids)
                    Unset(id, undo);

                if (undo.Count > 0)
                    RebuildAndPublish();

                return undo.Count;
            }
            catch
            {
                Rollback(undo);
                throw;
            }
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_rebuildGate)
        {
            _entries.Clear();
            _provider.Publish(IndexSnapshot.EmptyFor(_tokenization));
        }
    }

    private void RebuildAndPublish(IProgress<float>? progress = null)
    {
        var snapshot = IndexSnapshotBuilder.Build(_entries, _tokenization, progress);
        _provider.Publish(snapshot);
    }

    // Full rebuild: the new entries become the source of truth only after their snapshot was built.
    private void ReplaceAll(Dictionary<int, IndexedEntry> next, IProgress<float>? progress = null)
    {
        _provider.Publish(IndexSnapshotBuilder.Build(next, _tokenization, progress));
        _entries = next;
    }

    // Stores the entry and records the value it replaced. Returns true when the id already existed.
    private bool Set(int id, IndexedEntry entry, List<Undo> undo)
    {
        bool existed = _entries.TryGetValue(id, out var previous);
        undo.Add(new Undo(id, previous));
        _entries[id] = entry;
        return existed;
    }

    private bool Unset(int id, List<Undo> undo)
    {
        if (!_entries.Remove(id, out var previous))
            return false;

        undo.Add(new Undo(id, previous));
        return true;
    }

    // Puts back what a failed mutation changed; the published snapshot is untouched because publishing is the last step.
    private void Rollback(List<Undo> undo)
    {
        for (int i = undo.Count - 1; i >= 0; i--)
        {
            var (id, previous) = undo[i];
            if (previous is null)
                _entries.Remove(id);
            else
                _entries[id] = previous;
        }
    }

    private readonly record struct Undo(int Id, IndexedEntry? Previous);
}

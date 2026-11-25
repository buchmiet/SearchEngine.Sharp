using System.Runtime.InteropServices;
using SearchEngine.Snapshots;

namespace SearchEngine;

// IndexUpdater owns the mutable source of truth (_entries) and is the only place
// that calls IndexSnapshotProvider.Publish. All mutating operations are serialised
// through _rebuildGate so concurrent callers never see a partially-updated index.
//
// Reason for a full rebuild on every mutation:
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
//   untouched. Batch mutations read the caller's sequence before taking the lock, so a failing
//   or re-entrant enumeration changes nothing. Single-entry and batch mutations then record the
//   previous value of every id they change and restore it if building the snapshot throws (Apply);
//   full rebuilds fill a new dictionary and only swap it in after the build succeeded.

/// <summary>
/// Keeps the entries of an index and publishes a new snapshot to the <see cref="IndexSnapshotProvider"/> after
/// each change. Changes are serialized; queries read the published snapshot without taking the lock.
/// </summary>
/// <remarks>All members are safe to call from several threads.</remarks>
public sealed class IndexUpdater : IIndexUpdater
{
    private readonly IndexSnapshotProvider _provider;
    private readonly SearchTokenization _tokenization;
    private readonly Lock _rebuildGate = new();
    private Dictionary<int, IndexedEntry> _entries;

    /// <summary>Creates an empty updater and publishes an empty snapshot with its preset to
    /// <paramref name="provider"/>, so queries use that preset from the start.</summary>
    /// <param name="provider">The provider that receives every snapshot this updater builds. Use one updater per
    /// provider: two updaters on one provider overwrite each other's snapshots.</param>
    /// <param name="tokenization">The preset of every snapshot this updater builds, including the empty ones it
    /// publishes when it is created and on <see cref="Clear"/>; <see cref="SearchTokenization.Default"/> when
    /// <see langword="null"/>.</param>
    /// <remarks>The updater does not read the snapshot that <paramref name="provider"/> already holds: it replaces
    /// it with an empty one, and every later snapshot is built only from the entries given to the updater.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="provider"/> is <see langword="null"/>.</exception>
    public IndexUpdater(IndexSnapshotProvider provider, SearchTokenization? tokenization = null)
        : this(provider, tokenization, seed: null)
    {
        _provider.Publish(IndexSnapshot.EmptyFor(_tokenization));
    }

    // Dependency injection: the provider already holds the snapshot of seed (or, without seed, the empty snapshot of
    // the preset), so nothing is published here and the first mutation rebuilds from seed instead of dropping it.
    internal IndexUpdater(IndexSnapshotProvider provider, SearchTokenization? tokenization, IReadOnlyDictionary<int, IndexedEntry>? seed)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _tokenization = tokenization ?? SearchTokenization.Default;
        _entries = seed is null ? [] : new Dictionary<int, IndexedEntry>(seed);
    }

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
        ArgumentNullException.ThrowIfNull(entries);
        var next = new Dictionary<int, IndexedEntry>(entries.Count);
        foreach (var (key, value) in entries)
            next[key] = IndexedEntry.FromText(key, value, nameof(entries));

        lock (_rebuildGate)
            ReplaceAll(next);
    }

    /// <inheritdoc />
    public Task RebuildFromAsync(IAsyncEnumerable<KeyValuePair<int, string>> entries, IProgress<float>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return RebuildFromStreamAsync(entries, static (key, value) => IndexedEntry.FromText(key, value, nameof(entries)), progress, ct);
    }

    // ── Full rebuild (IndexedEntry) ──

    /// <inheritdoc />
    public void RebuildFrom(IDictionary<int, IndexedEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var next = new Dictionary<int, IndexedEntry>(entries.Count);
        foreach (var (key, value) in entries)
            next[key] = IndexedEntry.Require(key, value, nameof(entries));

        lock (_rebuildGate)
            ReplaceAll(next);
    }

    /// <inheritdoc />
    public Task RebuildFromAsync(IAsyncEnumerable<KeyValuePair<int, IndexedEntry>> entries, IProgress<float>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return RebuildFromStreamAsync(entries, static (key, value) => IndexedEntry.Require(key, value, nameof(entries)), progress, ct);
    }

    // Reads the stream outside the lock (awaiting while holding it would block other changes for the duration of
    // the I/O), then replaces all entries. The callers check the arguments before the task starts.
    private async Task RebuildFromStreamAsync<TValue>(
        IAsyncEnumerable<KeyValuePair<int, TValue>> entries,
        Func<int, TValue, IndexedEntry> toEntry,
        IProgress<float>? progress,
        CancellationToken ct)
    {
        var buffer = new Dictionary<int, IndexedEntry>();
        await foreach (var (key, value) in entries.WithCancellation(ct).ConfigureAwait(false))
            buffer[key] = toEntry(key, value);

        lock (_rebuildGate)
            ReplaceAll(buffer, progress);
    }

    // ── Single entry operations ──

    /// <inheritdoc />
    public void AddEntry(int id, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        AddEntry(id, new IndexedEntry(text, text));
    }

    /// <inheritdoc />
    public void AddEntry(int id, IndexedEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Apply([new Change(id, entry)]);
    }

    /// <inheritdoc />
    public bool RemoveEntry(int id) => Apply([new Change(id, null)]).Count > 0;

    /// <inheritdoc />
    public bool RefreshEntry(int id, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return RefreshEntry(id, new IndexedEntry(text, text));
    }

    /// <inheritdoc />
    public bool RefreshEntry(int id, IndexedEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Apply([new Change(id, entry)])[0].Previous is not null;
    }

    // ── Batch operations (single rebuild) ──

    /// <inheritdoc />
    public void AddOrUpdateEntries(IEnumerable<KeyValuePair<int, string>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        List<Change> changes = [];
        foreach (var (key, value) in entries)
            changes.Add(new Change(key, IndexedEntry.FromText(key, value, nameof(entries))));

        Apply(CollectionsMarshal.AsSpan(changes));
    }

    /// <inheritdoc />
    public void AddOrUpdateEntries(IEnumerable<KeyValuePair<int, IndexedEntry>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        List<Change> changes = [];
        foreach (var (key, value) in entries)
            changes.Add(new Change(key, IndexedEntry.Require(key, value, nameof(entries))));

        Apply(CollectionsMarshal.AsSpan(changes));
    }

    /// <inheritdoc />
    public int RemoveEntries(IEnumerable<int> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        List<Change> changes = [];
        foreach (int id in ids)
            changes.Add(new Change(id, null));

        return Apply(CollectionsMarshal.AsSpan(changes)).Count;
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

    // Applies the changes in order under the lock and publishes one snapshot when any entry changed. When the build
    // throws, the changed entries are restored and the published snapshot stays as it was, because publishing is the
    // last step. The callers read the caller's sequence into the changes first: enumerating it under the lock would
    // let code inside the enumeration (on this thread, the lock is re-entrant) publish a half-applied batch that a
    // later rollback could not take back. Returns what was changed, one record per entry that existed or was added.
    private List<Undo> Apply(ReadOnlySpan<Change> changes)
    {
        lock (_rebuildGate)
        {
            List<Undo> undo = [with(capacity: changes.Length)];
            try
            {
                foreach (var (id, entry) in changes)
                {
                    if (entry is not null)
                    {
                        undo.Add(new Undo(id, _entries.GetValueOrDefault(id)));
                        _entries[id] = entry;
                    }
                    else if (_entries.Remove(id, out var removed))
                    {
                        undo.Add(new Undo(id, removed));
                    }
                }

                if (undo.Count > 0)
                    RebuildAndPublish();

                return undo;
            }
            catch
            {
                for (int i = undo.Count - 1; i >= 0; i--)
                {
                    var (id, previous) = undo[i];
                    if (previous is null)
                        _entries.Remove(id);
                    else
                        _entries[id] = previous;
                }

                throw;
            }
        }
    }

    // One change of a mutation: Entry stores or replaces the entry of Id; null removes it.
    private readonly record struct Change(int Id, IndexedEntry? Entry);

    private readonly record struct Undo(int Id, IndexedEntry? Previous);
}

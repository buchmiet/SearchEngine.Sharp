using System.Diagnostics;
using System.Runtime.InteropServices;
using SearchEngine.Snapshots;

namespace SearchEngine;

// IndexUpdater owns the mutable source of truth (_entries) and is the only place that calls
// IndexSnapshotProvider.Publish.
//
// ── Cost ──
// The structures of a segment (flat arrays, sorted token list, bigram index) cannot be updated in place, so a snapshot
// is a base segment plus a small delta segment plus the dead base ordinals (IndexSnapshot). A change keeps the base of
// the published snapshot, marks the base documents it removes or replaces as dead and builds a new delta from every
// entry stored since the base was built. When the delta and the dead ordinals together pass MergeThreshold, the
// change is a merge: it builds a new base from all entries, and the next snapshot starts with an empty delta.
// RebuildFrom always builds a new base.
//
// ── Invariants ──
// _entries holds every entry with the ordinal of its document in the base of the published snapshot, or NotInBase
// when the entry was stored after that base was built. The delta ids and the dead ordinals of the next snapshot are
// derived from the published snapshot (_published) and the changes, so nothing else has to be kept in step.
//
// ── Thread safety ──
// _gate serializes all writes to _entries and _published and every Publish.
//
// ── Atomicity ──
// A change either completes (_entries updated, new snapshot published) or leaves both as they were. The callers of
// Apply read the caller's sequence before the lock (Apply has the reason); Apply records the previous value of every
// id it changes and restores it when the build throws, and base ordinals are written only after a build succeeded
// (AssignBaseOrdinals). RebuildFrom swaps in its new dictionary only after the build succeeded (ReplaceAll).

/// <summary>
/// Keeps the entries of an index and publishes a new snapshot to the <see cref="IndexSnapshotProvider"/> after
/// each change.
/// </summary>
/// <remarks>All members are safe to call from several threads.</remarks>
public sealed class IndexUpdater : IIndexUpdater
{
    // The base ordinal of an entry stored after the base of the published snapshot was built.
    private const int NotInBase = -1;

    private readonly IndexSnapshotProvider _provider;
    private readonly SearchTokenization _tokenization;
    private readonly Lock _gate = new();
    // Every entry, with its SearchText folded (SearchTokenization.FoldEntry).
    private Dictionary<int, StoredEntry> _entries;
    // The last snapshot this updater published, or the one the provider started with.
    private IndexSnapshot _published;

    /// <summary>Creates an empty updater and publishes an empty snapshot with its preset to
    /// <paramref name="provider"/>, so queries use that preset from the start.</summary>
    /// <param name="provider">The provider that receives every snapshot this updater builds. Use one updater per
    /// provider: two updaters on one provider overwrite each other's snapshots.</param>
    /// <param name="tokenization">The preset of every snapshot this updater builds, including the empty ones it
    /// publishes when it is created and on <see cref="Clear"/>; <see cref="SearchTokenization.Default"/> when
    /// <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="provider"/> is <see langword="null"/>.</exception>
    /// <remarks>The updater does not read the snapshot that <paramref name="provider"/> already holds: it replaces
    /// it with an empty one, and every later snapshot is built only from the entries given to the updater.</remarks>
    public IndexUpdater(IndexSnapshotProvider provider, SearchTokenization? tokenization = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _tokenization = tokenization ?? SearchTokenization.Default;
        _entries = [];
        _published = IndexSnapshot.EmptyFor(_tokenization);
        _provider.Publish(_published);
    }

    // Dependency injection: the provider already holds seedSnapshot, built from seed with tokenization
    // (IndexSnapshotBuilder.BuildFolded), so nothing is published here and the first change keeps its base. The
    // entries of seed are folded with tokenization already (SearchTokenization.FoldEntry).
    internal IndexUpdater(
        IndexSnapshotProvider provider,
        SearchTokenization tokenization,
        IReadOnlyDictionary<int, IndexedEntry> seed,
        IndexSnapshot seedSnapshot)
    {
        Debug.Assert(seedSnapshot.Delta is null && seedSnapshot.Base.DocumentCount == seed.Count);
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _tokenization = tokenization;
        _entries = new Dictionary<int, StoredEntry>(seed.Count);
        foreach (var (id, entry) in seed)
            _entries[id] = new(entry, NotInBase);
        AssignBaseOrdinals(_entries, seedSnapshot.Base);
        _published = seedSnapshot;
    }

    /// <inheritdoc />
    public int EntryCount
    {
        get { lock (_gate) return _entries.Count; }
    }

    /// <inheritdoc />
    public bool ContainsEntry(int id)
    {
        lock (_gate) return _entries.ContainsKey(id);
    }

    // The number of delta entries plus dead base ordinals past which a change is a merge. A change costs a build of
    // the delta, and every query pays a little for the delta and the dead ordinals, while a merge costs a build of
    // all entries; about 2 * sqrt(entries) keeps both small. The IIndexUpdater remarks and docs/performance.md
    // (Keeping an index current) quote 2 * sqrt(entries), 256 and 4,096: change them with this line.
    internal static int MergeThreshold(int entryCount) => Math.Clamp((int)(2 * Math.Sqrt(entryCount)), 256, 4096);

    /// <inheritdoc />
    public void RebuildFrom(IDictionary<int, string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var next = new Dictionary<int, StoredEntry>(entries.Count);
        foreach (var (key, value) in entries)
            next[key] = new(_tokenization.FoldEntry(IndexedEntry.FromText(key, value, nameof(entries))), NotInBase);

        lock (_gate)
            ReplaceAll(next);
    }

    /// <inheritdoc />
    public Task RebuildFromAsync(IAsyncEnumerable<KeyValuePair<int, string>> entries, IProgress<float>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return RebuildFromStreamAsync(entries, static (key, value) => IndexedEntry.FromText(key, value, nameof(entries)), progress, ct);
    }

    /// <inheritdoc />
    public void RebuildFrom(IDictionary<int, IndexedEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var next = new Dictionary<int, StoredEntry>(entries.Count);
        foreach (var (key, value) in entries)
            next[key] = new(_tokenization.FoldEntry(IndexedEntry.Require(key, value, nameof(entries))), NotInBase);

        lock (_gate)
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
        var buffer = new Dictionary<int, StoredEntry>();
        await foreach (var (key, value) in entries.WithCancellation(ct).ConfigureAwait(false))
            buffer[key] = new(_tokenization.FoldEntry(toEntry(key, value)), NotInBase);

        lock (_gate)
            ReplaceAll(buffer, progress);
    }

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
        Apply([new(id, _tokenization.FoldEntry(entry))]);
    }

    /// <inheritdoc />
    public bool RemoveEntry(int id) => Apply([new(id, null)]).Count > 0;

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
        return Apply([new(id, _tokenization.FoldEntry(entry))])[0].Previous is not null;
    }

    /// <inheritdoc />
    public void AddOrUpdateEntries(IEnumerable<KeyValuePair<int, string>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        List<Change> changes = [];
        foreach (var (key, value) in entries)
            changes.Add(new(key, _tokenization.FoldEntry(IndexedEntry.FromText(key, value, nameof(entries)))));

        Apply(CollectionsMarshal.AsSpan(changes));
    }

    /// <inheritdoc />
    public void AddOrUpdateEntries(IEnumerable<KeyValuePair<int, IndexedEntry>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        List<Change> changes = [];
        foreach (var (key, value) in entries)
            changes.Add(new(key, _tokenization.FoldEntry(IndexedEntry.Require(key, value, nameof(entries)))));

        Apply(CollectionsMarshal.AsSpan(changes));
    }

    /// <inheritdoc />
    public int RemoveEntries(IEnumerable<int> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        List<Change> changes = [];
        foreach (int id in ids)
            changes.Add(new(id, null));

        return Apply(CollectionsMarshal.AsSpan(changes)).Count;
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            Publish(IndexSnapshot.EmptyFor(_tokenization));
        }
    }

    private void Publish(IndexSnapshot snapshot)
    {
        _provider.Publish(snapshot);
        _published = snapshot;
    }

    // RebuildFrom: the new entries become the source of truth only after their snapshot was built.
    private void ReplaceAll(Dictionary<int, StoredEntry> next, IProgress<float>? progress = null)
    {
        var snapshot = BuildBase(next, progress);
        AssignBaseOrdinals(next, snapshot.Base);
        Publish(snapshot);
        _entries = next;
    }

    // A merge: a new base from all entries, without delta or dead ordinals.
    private void Merge()
    {
        var snapshot = BuildBase(_entries, progress: null);
        AssignBaseOrdinals(_entries, snapshot.Base);
        Publish(snapshot);
    }

    private IndexSnapshot BuildBase(Dictionary<int, StoredEntry> entries, IProgress<float>? progress)
    {
        var @base = IndexSnapshotBuilder.BuildFoldedSegment(entries, static stored => stored.Entry, _tokenization, firstOrdinal: 0, progress);
        return @base.DocumentCount == 0 ? IndexSnapshot.EmptyFor(_tokenization) : new IndexSnapshot(@base, null, [], _tokenization);
    }

    // Records the ordinal of each entry in a base just built from entries. Called only after the build succeeded,
    // and it cannot throw, so a failed build leaves every base ordinal as it was.
    private static void AssignBaseOrdinals(Dictionary<int, StoredEntry> entries, IndexSegment @base)
    {
        var ids = @base.RecordIds;
        for (int ordinal = 0; ordinal < ids.Length; ordinal++)
        {
            ref var stored = ref CollectionsMarshal.GetValueRefOrNullRef(entries, ids[ordinal]);
            stored = stored with { BaseOrdinal = ordinal };
        }
    }

    // Publishes the snapshot after the changes in undo were applied to _entries: the base of the published snapshot,
    // its dead ordinals plus the base documents of the changed entries, and a delta built from every entry stored
    // since that base was built; or a merge when the delta and the dead ordinals pass MergeThreshold.
    private void PublishChanges(List<Undo> undo)
    {
        if (_entries.Count == 0)
        {
            Publish(IndexSnapshot.EmptyFor(_tokenization));
            return;
        }

        var previous = _published;
        int[] dead = WithNewDeadOrdinals(previous.DeadOrdinals, undo);

        // The delta keeps the order of the previous one, then takes the changed entries in the order of the changes.
        List<int> deltaIds = [];
        HashSet<int> inDelta = [];
        if (previous.Delta is not null)
        {
            foreach (int id in previous.Delta.RecordIds)
            {
                if (_entries.ContainsKey(id) && inDelta.Add(id))
                    deltaIds.Add(id);
            }
        }

        foreach (var change in undo)
        {
            if (_entries.ContainsKey(change.Id) && inDelta.Add(change.Id))
                deltaIds.Add(change.Id);
        }

        if (deltaIds.Count + dead.Length > MergeThreshold(_entries.Count))
        {
            Merge();
            return;
        }

        IndexSegment? delta = null;
        if (deltaIds.Count > 0)
        {
            var deltaEntries = new List<KeyValuePair<int, IndexedEntry>>(deltaIds.Count);
            foreach (int id in deltaIds)
                deltaEntries.Add(new(id, _entries[id].Entry));
            delta = IndexSnapshotBuilder.BuildFoldedSegment(deltaEntries, static entry => entry, _tokenization, previous.Base.DocumentCount);
        }

        Publish(new IndexSnapshot(previous.Base, delta, dead, _tokenization));
    }

    // The dead ordinals of the previous snapshot plus the base ordinals of the entries the changes removed or
    // replaced, ascending. An entry with a base ordinal is live in the base, so the two sets do not overlap.
    private static int[] WithNewDeadOrdinals(int[] previousDead, List<Undo> undo)
    {
        List<int> added = [];
        foreach (var change in undo)
        {
            if (change.Previous is { BaseOrdinal: >= 0 } previous)
                added.Add(previous.BaseOrdinal);
        }

        if (added.Count == 0)
            return previousDead;

        var dead = new int[previousDead.Length + added.Count];
        previousDead.CopyTo(dead, 0);
        added.CopyTo(dead, previousDead.Length);
        Array.Sort(dead);
        return dead;
    }

    // Applies the changes in order under the lock and publishes one snapshot when any entry changed. When the build
    // throws, the changed entries are restored and the published snapshot stays as it was, because publishing is the
    // last step. The callers read the caller's sequence into the changes first: enumerating it under the lock would
    // let code inside the enumeration (on this thread, the lock is re-entrant) publish a half-applied batch that a
    // later rollback could not take back. Returns what was changed, one record per entry that existed or was added.
    private List<Undo> Apply(ReadOnlySpan<Change> changes)
    {
        lock (_gate)
        {
            List<Undo> undo = [with(capacity: changes.Length)];
            try
            {
                foreach (var (id, entry) in changes)
                {
                    if (entry is not null)
                    {
                        undo.Add(new(id, _entries.TryGetValue(id, out var previous) ? previous : null));
                        _entries[id] = new(entry, NotInBase);
                    }
                    else if (_entries.Remove(id, out var removed))
                    {
                        undo.Add(new(id, removed));
                    }
                }

                if (undo.Count > 0)
                    PublishChanges(undo);

                return undo;
            }
            catch
            {
                for (int i = undo.Count - 1; i >= 0; i--)
                {
                    var (id, previous) = undo[i];
                    if (previous is { } stored)
                        _entries[id] = stored;
                    else
                        _entries.Remove(id);
                }

                throw;
            }
        }
    }

    // An entry and the ordinal of its document in the base of the published snapshot, or NotInBase.
    private readonly record struct StoredEntry(IndexedEntry Entry, int BaseOrdinal);

    // One change of a mutation: Entry stores or replaces the entry of Id; null removes it.
    private readonly record struct Change(int Id, IndexedEntry? Entry);

    private readonly record struct Undo(int Id, StoredEntry? Previous);
}

using System.Runtime.InteropServices;
using SearchEngine.Snapshots;

namespace SearchEngine;

// IndexUpdater owns the list of segments and is the only place that calls IndexSnapshotProvider.Publish.
//
// ── Cost ──
// The structures of a segment (flat arrays, sorted token list, bigram index) cannot be updated in place, so an index
// is a list of immutable segments (IndexSnapshot). A change builds one small segment from the entries it adds or
// replaces, marks the documents it removes or replaces as dead in the segments that hold them (DeadSet) and publishes
// the list with the new segment last. Its cost depends on the size of the change, not on the size of the index: it
// rebuilds nothing that exists.
//
// Many small segments would slow every query down, so neighbouring segments of about the same size are merged
// (MergePolicy). A merge runs on a background thread, reads only immutable segments and replaces the segments it
// read with one segment in a single publish. A change that arrives while a merge runs is not lost: a document of a
// merged segment that the change removes or replaces is marked dead in the merged segment when the merge completes
// (MergeJob.KilledIds). The merge also drops the dead documents of its segments, so the index does not keep what was
// removed. RebuildFrom builds one segment and replaces everything.
//
// ── Invariants ──
// An id is live in at most one segment. Segment.Ids finds the copy of an id in a segment, and the dead set of the
// segment says whether that copy is still live. Every segment in _segments has at least one live document, except
// while it is an input of a running merge. _liveCount is the number of live documents.
//
// ── Thread safety ──
// _gate serializes all reads and writes of _segments, _dead, _liveCount and the merge bookkeeping, and every Publish.
// A merge builds its segment outside the lock and takes the lock to complete.
//
// ── Atomicity ──
// A change either completes (segment built, new list published) or leaves everything as it was: the segment is built
// before any state changes, and the state is replaced in one step. RebuildFrom swaps in its new segment only after
// the build succeeded.

/// <summary>
/// Keeps the entries of an index and publishes a new snapshot to the <see cref="IndexSnapshotProvider"/> after
/// each change.
/// </summary>
/// <remarks>All members are safe to call from several threads.</remarks>
public sealed class IndexUpdater : IIndexUpdater
{
    private readonly IndexSnapshotProvider _provider;
    private readonly SearchTokenization _tokenization;
    private readonly MergePolicy _policy;
    private readonly Lock _gate = new();

    // The published segments in snapshot order and the dead documents of each. Replaced, never changed in place: a
    // published IndexSnapshot keeps the arrays it was built with.
    private IndexSegment[] _segments = [];
    private DeadSet?[] _dead = [];
    private int _liveCount;

    // Merge bookkeeping. A segment in _merging is an input of a running merge. _epoch changes when the index is
    // replaced as a whole, which discards the merges that started before.
    private readonly HashSet<IndexSegment> _merging = [with(ReferenceEqualityComparer.Instance)];
    private readonly List<MergeJob> _jobs = [];
    private readonly bool[] _laneActive = new bool[2];
    private long _epoch;
    private Exception? _mergeFault;

    // What the merges did so far, for tests and benchmarks.
    internal MergeStatistics Statistics { get; } = new();

    // Called on the background thread after a merge built its segment and before it takes the lock to complete (tests
    // hold a merge here while changes arrive).
    internal Action? OnMergeBuilt { get; set; }

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
        : this(provider, tokenization, MergePolicy.Default)
    {
    }

    internal IndexUpdater(IndexSnapshotProvider provider, SearchTokenization? tokenization, MergePolicy policy)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _tokenization = tokenization ?? SearchTokenization.Default;
        _policy = policy;
        _provider.Publish(IndexSnapshot.EmptyFor(_tokenization));
    }

    // Dependency injection: the provider already holds seedSnapshot, built with tokenization
    // (IndexSnapshotBuilder.BuildFolded), so nothing is published here and the first change keeps its segment.
    internal IndexUpdater(IndexSnapshotProvider provider, SearchTokenization tokenization, IndexSnapshot seedSnapshot)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _tokenization = tokenization;
        _policy = MergePolicy.Default;
        _segments = [.. seedSnapshot.Segments];
        _dead = new DeadSet?[_segments.Length];
        foreach (var segment in _segments)
            _ = segment.Ids;
        _liveCount = seedSnapshot.DocumentCount;
    }

    /// <inheritdoc />
    public int EntryCount
    {
        get { lock (_gate) return _liveCount; }
    }

    /// <inheritdoc />
    public bool ContainsEntry(int id)
    {
        lock (_gate) return TryFindLive(id, out _, out _);
    }

    /// <inheritdoc />
    public void RebuildFrom(IDictionary<int, string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var next = new Dictionary<int, IndexedEntry>(entries.Count);
        foreach (var (key, value) in entries)
            next[key] = _tokenization.FoldEntry(IndexedEntry.FromText(key, value, nameof(entries)));

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
        var next = new Dictionary<int, IndexedEntry>(entries.Count);
        foreach (var (key, value) in entries)
            next[key] = _tokenization.FoldEntry(IndexedEntry.Require(key, value, nameof(entries)));

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
        var buffer = new Dictionary<int, IndexedEntry>();
        await foreach (var (key, value) in entries.WithCancellation(ct).ConfigureAwait(false))
            buffer[key] = _tokenization.FoldEntry(toEntry(key, value));

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
    public bool RemoveEntry(int id) => Apply([new(id, null)])[0];

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
        return Apply([new(id, _tokenization.FoldEntry(entry))])[0];
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

        int removed = 0;
        foreach (bool existed in Apply(CollectionsMarshal.AsSpan(changes)))
        {
            if (existed)
                removed++;
        }

        return removed;
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_gate)
        {
            _epoch++;
            _merging.Clear();
            _segments = [];
            _dead = [];
            _liveCount = 0;
            Publish();
        }
    }

    // The number of segments of the published snapshot (tests).
    internal int SegmentCount
    {
        get { lock (_gate) return _segments.Length; }
    }

    // The first exception a background merge threw, or null. After one, merges run only through the MaxSegments rule.
    internal Exception? MergeFault
    {
        get { lock (_gate) return _mergeFault; }
    }

    // Blocks until no merge runs and none is due (tests, benchmarks).
    internal void WaitForMerges()
    {
        while (true)
        {
            lock (_gate)
            {
                if (_jobs.Count == 0 && !_laneActive[0] && !_laneActive[1] && (_mergeFault is not null || !_policy.Background || PickRun(int.MaxValue) is null))
                    return;
            }

            Thread.Sleep(1);
        }
    }

    // Merges every due run on the calling thread, until none is due or the segments cannot be merged further
    // (tests of a policy without a background thread).
    internal void MergeNow()
    {
        lock (_gate)
        {
            while (PickRun(int.MaxValue) is { } run)
            {
                var job = ClaimJob(run);
                job.Result = SegmentMerger.Merge(job.Inputs, job.DeadAtStart);
                CompleteJob(job);
            }
        }
    }

    // The documents currently dead, per segment, as local ordinals (tests).
    internal IReadOnlyList<int>[] DeadOrdinalsPerSegment()
    {
        lock (_gate)
        {
            var result = new IReadOnlyList<int>[_segments.Length];
            for (int i = 0; i < result.Length; i++)
                result[i] = _dead[i]?.ToList() ?? [];
            return result;
        }
    }

    private void Publish()
    {
        _provider.Publish(_segments.Length == 0 ? IndexSnapshot.EmptyFor(_tokenization) : new IndexSnapshot(_segments, _dead, _tokenization));
    }

    // RebuildFrom: the new entries become the index only after their segment was built.
    private void ReplaceAll(Dictionary<int, IndexedEntry> next, IProgress<float>? progress = null)
    {
        var segment = IndexSnapshotBuilder.BuildFoldedSegment(next, static entry => entry, _tokenization, progress);
        if (segment.DocumentCount > 0)
            _ = segment.Ids;

        _epoch++;
        _merging.Clear();
        _segments = segment.DocumentCount == 0 ? [] : [segment];
        _dead = new DeadSet?[_segments.Length];
        _liveCount = segment.DocumentCount;
        Publish();
    }

    // The segment that holds the live copy of an id, and its local ordinal.
    private bool TryFindLive(int id, out int segment, out int local)
    {
        for (int s = _segments.Length - 1; s >= 0; s--)
        {
            if (_segments[s].Ids.TryFind(id, out local) && _dead[s]?.Contains(local) != true)
            {
                segment = s;
                return true;
            }
        }

        segment = local = -1;
        return false;
    }

    // Applies the changes in order and publishes one snapshot when anything changed. The result says, per change,
    // whether the id had a document before it (the changes before it in the same call included). A document that a
    // change removes or replaces becomes dead where it is; the entries that stay are built into one new segment, in
    // the order in which their ids first occur in the changes. The segment is built before any state changes, so a
    // build that throws changes nothing. The callers read the caller's sequence into the changes first: enumerating
    // it under the lock would let code inside the enumeration (on this thread, the lock is re-entrant) publish a
    // half-applied batch.
    private bool[] Apply(ReadOnlySpan<Change> changes)
    {
        var existed = new bool[changes.Length];
        if (changes.Length == 0)
            return existed;

        lock (_gate)
        {
            // The last change of each id wins; null means removed.
            Dictionary<int, IndexedEntry?> latest = [with(changes.Length)];
            List<int> order = [with(changes.Length)];
            List<(int Segment, int Local)> kills = [];
            for (int i = 0; i < changes.Length; i++)
            {
                var (id, entry) = changes[i];
                if (latest.TryGetValue(id, out var current))
                {
                    existed[i] = current is not null;
                }
                else
                {
                    existed[i] = TryFindLive(id, out int segment, out int local);
                    if (existed[i])
                        kills.Add((segment, local));
                    order.Add(id);
                }

                latest[id] = entry;
            }

            List<KeyValuePair<int, IndexedEntry>> added = [with(order.Count)];
            foreach (int id in CollectionsMarshal.AsSpan(order))
            {
                if (latest[id] is { } entry)
                    added.Add(new(id, entry));
            }

            if (kills.Count == 0 && added.Count == 0)
                return existed;

            var fresh = added.Count == 0 ? null : IndexSnapshotBuilder.BuildFoldedSegment(added, static entry => entry, _tokenization);
            if (fresh is not null)
                _ = fresh.Ids;

            var segments = new List<IndexSegment>(_segments.Length + 1);
            var dead = new List<DeadSet?>(_segments.Length + 1);
            for (int s = 0; s < _segments.Length; s++)
            {
                DeadSet? set = _dead[s];
                var locals = CollectKills(kills, s);
                if (locals.Count > 0)
                {
                    set = DeadSet.With(set, _segments[s].DocumentCount, CollectionsMarshal.AsSpan(locals));
                    foreach (int local in CollectionsMarshal.AsSpan(locals))
                        LogKill(_segments[s], _segments[s].RecordIds[local]);
                }

                // A segment without a live document is dropped, unless a merge still reads it.
                if (set is not null && set.Count == _segments[s].DocumentCount && !_merging.Contains(_segments[s]))
                    continue;

                segments.Add(_segments[s]);
                dead.Add(set);
            }

            if (fresh is not null)
            {
                segments.Add(fresh);
                dead.Add(null);
            }

            if (segments.Count > _policy.MaxSegments)
                MergeNewestSynchronously(segments, dead);

            _segments = [.. segments];
            _dead = [.. dead];
            _liveCount += added.Count - kills.Count;
            Publish();
            KickMerges();
            return existed;
        }
    }

    private static List<int> CollectKills(List<(int Segment, int Local)> kills, int segment)
    {
        List<int> locals = [];
        foreach (var (s, local) in CollectionsMarshal.AsSpan(kills))
        {
            if (s == segment)
                locals.Add(local);
        }

        return locals;
    }

    // Tells the running merges that read this segment that a document of it died after they started.
    private void LogKill(IndexSegment segment, int recordId)
    {
        foreach (var job in CollectionsMarshal.AsSpan(_jobs))
        {
            if (Array.IndexOf(job.Inputs, segment) >= 0)
                job.KilledIds.Add(recordId);
        }
    }

    // The writer outran the background merges: merges the newest small segments that no merge reads, on this thread.
    // They are the segments the background merges would take next, and few, so this takes a few milliseconds.
    private void MergeNewestSynchronously(List<IndexSegment> segments, List<DeadSet?> dead)
    {
        if (PickRun(segments, dead, _policy.SmallLaneLimit, minCount: 2) is not { } run)
            return;

        long began = System.Diagnostics.Stopwatch.GetTimestamp();
        var merged = SegmentMerger.Merge(segments.GetRange(run.Start, run.Length), dead.GetRange(run.Start, run.Length));
        Statistics.Record(synchronous: true, merged.DocumentCount, System.Diagnostics.Stopwatch.GetElapsedTime(began));
        segments.RemoveRange(run.Start, run.Length);
        dead.RemoveRange(run.Start, run.Length);
        if (merged.DocumentCount > 0)
        {
            segments.Insert(run.Start, merged);
            dead.Insert(run.Start, null);
        }
    }

    // ── Merging ──

    // The run of adjacent segments to merge next: its first segment and length, or null when nothing is due. A run
    // ends at some segment and extends towards older ones while each older segment has at most MergePolicy.SimilarFactor
    // times as many live documents as the largest segment of the run so far (and the run has at most
    // MergePolicy.MaxRun segments). So the newest small segments merge with each other, and an old segment joins only
    // once its newer neighbours have grown to about its size: a document is rewritten once per size class, not once
    // per change. A run needs at least minCount segments and at most maxLive live documents; of the runs that qualify
    // the smallest wins, the cheapest merge. No run crosses a segment that a running merge reads. A segment that is
    // mostly dead is a run of one.
    private (int Start, int Length)? PickRun(IReadOnlyList<IndexSegment> segments, IReadOnlyList<DeadSet?> dead, int maxLive, int minCount)
    {
        int count = segments.Count;
        if (count == 0)
            return null;

        Span<int> lives = count <= 256 ? stackalloc int[count] : new int[count];
        for (int i = 0; i < count; i++)
            lives[i] = segments[i].DocumentCount - (dead[i]?.Count ?? 0);

        (int Start, int Length)? best = null;
        long bestTotal = long.MaxValue;
        for (int end = count - 1; end >= 0; end--)
        {
            if (_merging.Contains(segments[end]))
                continue;

            int length = 1;
            int largest = Math.Max(lives[end], 1);
            long total = largest;
            for (int i = end - 1; i >= 0 && length < _policy.MaxRun && !_merging.Contains(segments[i]); i--)
            {
                int size = Math.Max(lives[i], 1);
                if (size > _policy.SimilarFactor * largest)
                    break;

                largest = Math.Max(largest, size);
                total += size;
                length++;
            }

            if (length >= minCount && total <= maxLive && total < bestTotal)
            {
                best = (end - length + 1, length);
                bestTotal = total;
            }
        }

        if (best is not null)
            return best;

        for (int i = 0; i < count; i++)
        {
            int deadCount = dead[i]?.Count ?? 0;
            if (deadCount > 0 && deadCount >= _policy.DeadRatio * segments[i].DocumentCount && !_merging.Contains(segments[i]) && lives[i] <= maxLive)
                return (i, 1);
        }

        return null;
    }

    // The next merge of the published segments with the normal fan-out.
    private (int Start, int Length)? PickRun(int maxLive) => PickRun(_segments, _dead, maxLive, _policy.FanOut);

    private MergeJob ClaimJob((int Start, int Length) run)
    {
        var job = new MergeJob(
            _segments[run.Start..(run.Start + run.Length)],
            _dead[run.Start..(run.Start + run.Length)],
            _epoch);
        foreach (var segment in job.Inputs)
            _merging.Add(segment);
        _jobs.Add(job);
        return job;
    }

    // Starts a background worker for each lane that is idle and has a merge due. Lane 0 takes any merge, lane 1 only
    // small ones (MergePolicy.SmallLaneLimit).
    private void KickMerges()
    {
        if (!_policy.Background || _mergeFault is not null)
            return;

        for (int lane = 0; lane < _laneActive.Length; lane++)
        {
            if (_laneActive[lane] || PickRun(LaneLimit(lane)) is null)
                continue;

            _laneActive[lane] = true;
            StartWorker(lane);
        }
    }

    private void StartWorker(int lane) => Task.Run(() => MergeWorker(lane));

    private int LaneLimit(int lane) => lane == 0 ? int.MaxValue : _policy.SmallLaneLimit;

    private void MergeWorker(int lane)
    {
        while (true)
        {
            MergeJob job;
            lock (_gate)
            {
                if (_mergeFault is not null || PickRun(LaneLimit(lane)) is not { } run)
                {
                    _laneActive[lane] = false;
                    return;
                }

                job = ClaimJob(run);
            }

            try
            {
                long began = System.Diagnostics.Stopwatch.GetTimestamp();
                job.Result = SegmentMerger.Merge(job.Inputs, job.DeadAtStart);
                Statistics.Record(synchronous: false, job.Result.DocumentCount, System.Diagnostics.Stopwatch.GetElapsedTime(began));
                OnMergeBuilt?.Invoke();
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    _mergeFault ??= ex;
                    ReleaseJob(job);
                    _laneActive[lane] = false;
                }

                return;
            }

            lock (_gate)
            {
                CompleteJob(job);
                // Completing may make another run due for a lane that is idle; this lane loops to pick it up.
                KickMerges();
            }
        }
    }

    private void ReleaseJob(MergeJob job)
    {
        _jobs.Remove(job);
        foreach (var segment in job.Inputs)
            _merging.Remove(segment);
    }

    // Replaces the inputs of a finished merge with its result and publishes, unless the index was replaced as a whole
    // since the merge started.
    private void CompleteJob(MergeJob job)
    {
        ReleaseJob(job);
        if (job.Epoch != _epoch)
            return;

        int start = Array.IndexOf(_segments, job.Inputs[0]);
        if (start < 0)
            return;

        var merged = job.Result!;
        DeadSet? mergedDead = null;
        if (merged.DocumentCount > 0 && job.KilledIds.Count > 0)
        {
            List<int> locals = [];
            foreach (int id in CollectionsMarshal.AsSpan(job.KilledIds))
            {
                if (merged.Ids.TryFind(id, out int local))
                    locals.Add(local);
            }

            if (locals.Count > 0)
                mergedDead = DeadSet.With(null, merged.DocumentCount, CollectionsMarshal.AsSpan(locals));
        }

        var segments = new List<IndexSegment>(_segments);
        var dead = new List<DeadSet?>(_dead);
        segments.RemoveRange(start, job.Inputs.Length);
        dead.RemoveRange(start, job.Inputs.Length);
        if (merged.DocumentCount > 0 && (mergedDead is null || mergedDead.Count < merged.DocumentCount))
        {
            segments.Insert(start, merged);
            dead.Insert(start, mergedDead);
        }

        _segments = [.. segments];
        _dead = [.. dead];
        Publish();
    }

    private sealed class MergeJob(IndexSegment[] inputs, DeadSet?[] deadAtStart, long epoch)
    {
        internal IndexSegment[] Inputs { get; } = inputs;

        // The dead documents of the inputs when the merge started: the merge drops these.
        internal DeadSet?[] DeadAtStart { get; } = deadAtStart;

        internal long Epoch { get; } = epoch;

        // Record ids of documents of the inputs that died after the merge started.
        internal List<int> KilledIds { get; } = [];

        internal IndexSegment? Result { get; set; }
    }

    // One change of a mutation: Entry stores or replaces the entry of Id; null removes it.
    private readonly record struct Change(int Id, IndexedEntry? Entry);
}

// Counts of the merges an IndexUpdater ran (IndexUpdater.Statistics). Merges are rare next to changes, so a lock is
// cheaper than the cache-line padding that independent atomic counters would need.
internal sealed class MergeStatistics
{
    private readonly Lock _lock = new();
    private long _background;
    private long _synchronous;
    private long _documents;
    private TimeSpan _time;

    // Merges on a background thread.
    internal long BackgroundMerges
    {
        get { lock (_lock) return _background; }
    }

    // Merges that a change ran itself because the segments passed MergePolicy.MaxSegments.
    internal long SynchronousMerges
    {
        get { lock (_lock) return _synchronous; }
    }

    // Documents written by all merges: divided by the documents added, the write amplification of the index.
    internal long DocumentsWritten
    {
        get { lock (_lock) return _documents; }
    }

    internal TimeSpan MergeTime
    {
        get { lock (_lock) return _time; }
    }

    internal void Record(bool synchronous, int documents, TimeSpan elapsed)
    {
        lock (_lock)
        {
            if (synchronous)
                _synchronous++;
            else
                _background++;
            _documents += documents;
            _time += elapsed;
        }
    }
}

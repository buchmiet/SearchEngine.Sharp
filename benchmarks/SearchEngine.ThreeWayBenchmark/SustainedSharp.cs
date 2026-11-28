using System.Reflection;
using SearchEngine;

namespace ThreeWay;

/// <summary>SearchEngine.Sharp, public API only; segment and merge figures come from reflection so the same adapter runs on the layout before segments.</summary>
public sealed class SustainedSharp(string label) : ILifecycle
{
    public string Name => label;

    private readonly IndexSnapshotProvider _provider = new();
    private IndexUpdater _updater = null!;
    private SearchEngineSharp _engine = null!;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public void Build(string[] names, string? durableDir)
    {
        if (durableDir is not null)
            throw new NotSupportedException("SearchEngine.Sharp has no persistent index.");
        _updater = new IndexUpdater(_provider);
        _engine = new SearchEngineSharp(_provider);
        var dict = new Dictionary<int, string>(names.Length);
        for (int i = 0; i < names.Length; i++)
            dict[i] = names[i];
        _updater.RebuildFrom(dict);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    }

    public long AddVisible(int firstId, string[] names, string probeToken)
    {
        if (names.Length == 1)
            _updater.AddEntry(firstId, names[0]);
        else
        {
            var batch = new List<KeyValuePair<int, string>>(names.Length);
            for (int i = 0; i < names.Length; i++)
                batch.Add(new(firstId + i, names[i]));
            _updater.AddOrUpdateEntries(batch);
        }

        return _engine.CountMatches(probeToken, WordMatchMethod.Exact);
    }

    public (int Segments, long DeltaDocuments) Layout()
    {
        object snapshot = _provider.Current;
        var type = snapshot.GetType();
        if (type.GetField("Segments", Any)?.GetValue(snapshot) is Array segments)
        {
            long total = 0, largest = 0;
            foreach (object? s in segments)
            {
                long n = Convert.ToInt64(s!.GetType().GetProperty("DocumentCount", Any)!.GetValue(s));
                total += n;
                largest = Math.Max(largest, n);
            }

            return (segments.Length, total - largest);
        }

        // The layout before segments: a base, an optional delta and the dead base ordinals.
        object? delta = type.GetField("Delta", Any)?.GetValue(snapshot);
        long deltaDocs = delta is null ? 0 : Convert.ToInt64(delta.GetType().GetProperty("DocumentCount", Any)!.GetValue(delta));
        long dead = type.GetField("DeadOrdinals", Any)?.GetValue(snapshot) is int[] d ? d.Length : 0;
        return (delta is null ? 1 : 2, deltaDocs + dead);
    }

    public long Apply(OpKind kind, int id, string? text, string probeToken)
    {
        switch (kind)
        {
            case OpKind.Add: _updater.AddEntry(id, text!); break;
            case OpKind.Update: _updater.RefreshEntry(id, text!); break;
            default: _updater.RemoveEntry(id); break;
        }

        return _engine.CountMatches(probeToken, WordMatchMethod.Exact);
    }

    public long CountAnd(string[] tokens) => _engine.CountMatches(string.Join(" AND ", tokens), WordMatchMethod.Exact, true);

    private object? _largestRef;
    private long _largestKey;
    private System.Reflection.FieldInfo? _segmentsField, _deadField;
    private System.Reflection.PropertyInfo? _docCount, _deadCount;

    // Segments and dead sets of the current snapshot, through reflection (internal types); the largest segment is told apart by reference.
    public LayoutDetail Detail()
    {
        object snapshot = _provider.Current;
        var type = snapshot.GetType();
        _segmentsField ??= type.GetField("Segments", Any);
        _deadField ??= type.GetField("Dead", Any);
        if (_segmentsField?.GetValue(snapshot) is not Array segments)
        {
            var (seg, delta) = Layout();
            return new(seg, delta, 0, 0, 0, 0);
        }

        var dead = _deadField?.GetValue(snapshot) as Array;
        long total = 0, largest = -1, largestDead = 0, deadTotal = 0;
        object? largestSeg = null;
        for (int i = 0; i < segments.Length; i++)
        {
            object seg = segments.GetValue(i)!;
            _docCount ??= seg.GetType().GetProperty("DocumentCount", Any)!;
            long n = Convert.ToInt64(_docCount.GetValue(seg));
            long d = 0;
            if (dead?.GetValue(i) is { } set)
            {
                _deadCount ??= set.GetType().GetProperty("Count", Any)!;
                d = Convert.ToInt64(_deadCount.GetValue(set));
            }

            total += n; deadTotal += d;
            if (n > largest) { largest = n; largestDead = d; largestSeg = seg; }
        }

        if (!ReferenceEquals(largestSeg, _largestRef))
        {
            _largestRef = largestSeg;
            _largestKey++;
        }

        return new(segments.Length, total - largest, deadTotal, largest, largestDead, _largestKey);
    }

    public long ReaderQuery(int which)
    {
        var q = ReaderQueries.Cycle[which % ReaderQueries.Cycle.Length].Sharp!.Value;
        return _engine.CountMatches(q.Expr, q.Method, q.Ops);
    }

    public int[] FindIds(QuerySpec query)
    {
        var q = query.Sharp!.Value;
        int[] ids = [.. _engine.Find(q.Expr, q.Method, q.Ops)];
        Array.Sort(ids);
        return ids;
    }

    public long TopId(string token)
    {
        var ids = _engine.Find(token, WordMatchMethod.Exact);
        return ids.Count == 0 ? -1 : ids[0];
    }

    public void Settle() => _updater.GetType().GetMethod("WaitForMerges", Any)?.Invoke(_updater, null);

    public MergeReport Merges()
    {
        object? stats = _updater.GetType().GetProperty("Statistics", Any)?.GetValue(_updater);
        if (stats is null)
            return new("n/a", 0, 0, 0, "the previous layout rebuilds the delta and the base inside the write call, so every rebuild is a spike in AddEntry itself");
        long Get(string p) => Convert.ToInt64(stats.GetType().GetProperty(p, Any)!.GetValue(stats));
        double seconds = ((TimeSpan)stats.GetType().GetProperty("MergeTime", Any)!.GetValue(stats)!).TotalSeconds;
        return new("exact", Get("BackgroundMerges") + Get("SynchronousMerges"), Get("DocumentsWritten"), seconds,
            $"{Get("BackgroundMerges")} background, {Get("SynchronousMerges")} inside the write call");
    }

    private System.Collections.ICollection? _jobs;

    // The updater's list of running merge jobs, read without its lock: a racy read of a count, good enough to tag a
    // query sample. Null on the layout before segments, which has no background merges.
    public bool? MergeActive
    {
        get
        {
            _jobs ??= _updater.GetType().GetField("_jobs", Any)?.GetValue(_updater) as System.Collections.ICollection;
            return _jobs is null ? null : _jobs.Count > 0;
        }
    }

    // The updater counts merges and their total time but does not publish each one.
    public IReadOnlyList<MergeEvent> MergeEvents => [];

    public void Dispose() { }
}

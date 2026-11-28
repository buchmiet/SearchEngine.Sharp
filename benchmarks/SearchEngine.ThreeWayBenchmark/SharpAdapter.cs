using SearchEngine;

namespace ThreeWay;

// ---------------------------------------------------------------- SearchEngine.Sharp
public sealed class SharpEngine : IEngine
{
    public string Name => "SearchEngine.Sharp";
    IndexSnapshotProvider _provider = new();
    IndexUpdater _updater = null!;
    SearchEngineSharp _engine = null!;

    public void Build(string[] names)
    {
        _provider = new IndexSnapshotProvider();
        _updater = new IndexUpdater(_provider); // SearchTokenization.Default
        _engine = new SearchEngineSharp(_provider);
        var dict = new Dictionary<int, string>(names.Length);
        for (int i = 0; i < names.Length; i++) dict[i] = names[i];
        _updater.RebuildFrom(dict);
    }
    public bool Supports(QuerySpec q) => q.Sharp is not null;
    public long Count(QuerySpec q) { var s = q.Sharp!.Value; return _engine.CountMatches(s.Expr, s.Method, s.Ops); }
    public long All(QuerySpec q) { var s = q.Sharp!.Value; var l = _engine.Find(s.Expr, s.Method, s.Ops); long sum = l.Count; foreach (var i in l) sum += i; return sum; }
    // The engine stops after ten ids, in snapshot order; the checksum is the ids.
    public long Top10(QuerySpec q) { var s = q.Sharp!.Value; var l = _engine.Find(s.Expr, s.Method, s.Ops, SearchSortMode.SnapshotOrder, null, 10); long sum = 0; foreach (var i in l) sum += i; return sum; }
    public long AddOneAndSearch(int id, string name, string tok)
    {
        _updater.AddEntry(id, name);
        return _engine.CountMatches(tok, WordMatchMethod.Exact);
    }
    public (double, long, double)? DiskBuild(string[] names, string dir) => null; // no persistent index
    public void ForceMerge() { }
    public void Dispose() { }
}


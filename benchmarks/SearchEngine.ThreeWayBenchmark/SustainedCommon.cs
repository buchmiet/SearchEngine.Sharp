using System.Diagnostics;

namespace ThreeWay;

/// <summary>What an engine reports about its own background merging.</summary>
public sealed record MergeReport(string Source, long Merges, long DocumentsWritten, double MergeSeconds, string Note);

/// <summary>
/// The operations of the sustained-indexing benchmark. Every engine gives the same guarantee: when
/// <see cref="AddVisible"/> returns, a query on the freshest searcher sees every document that was added (read your own
/// writes), without durability. Durable mode adds a commit that reaches the disk.
/// </summary>
public interface ISustained : IDisposable
{
    string Name { get; }

    /// <summary>Builds the starting index from names[i] with id i. Disk-backed when durable.</summary>
    void Build(string[] names, string? durableDir);

    /// <summary>Adds the documents (ids firstId, firstId+1, ...) and returns the number of hits of probeToken once they are visible.</summary>
    long AddVisible(int firstId, string[] names, string probeToken);

    /// <summary>The number of segments and the number of documents outside the largest one, read untimed.</summary>
    (int Segments, long DeltaDocuments) Layout();

    /// <summary>One search on the freshest state, safe to call from another thread while a writer works.</summary>
    long ReaderQuery(int which);

    /// <summary>The ids of the documents that match, sorted ascending, read from the engine's stored ids.</summary>
    int[] FindIds(QuerySpec query);

    /// <summary>The id of the best hit for a token, or -1.</summary>
    long TopId(string token);

    /// <summary>Waits for background merges where the engine allows it; returns the layout afterwards.</summary>
    void Settle();

    MergeReport Merges();
}

public static class ReaderQueries
{
    // The queries a concurrent reader cycles through: a term, an AND and a prefix.
    public static readonly QuerySpec[] Cycle = [Queries.All[1], Queries.All[3], Queries.All[6]];
}

public sealed class RssSampler : IDisposable
{
    private readonly Thread _thread;
    private volatile bool _stop;
    private long _peak;

    public RssSampler(int intervalMs = 20)
    {
        _peak = Proc.RssBytes();
        _thread = new Thread(() =>
        {
            while (!_stop)
            {
                long rss = Proc.RssBytes();
                if (rss > Interlocked.Read(ref _peak))
                    Interlocked.Exchange(ref _peak, rss);
                Thread.Sleep(intervalMs);
            }
        }) { IsBackground = true };
        _thread.Start();
    }

    public long PeakBytes => Interlocked.Read(ref _peak);

    public void Dispose()
    {
        _stop = true;
        _thread.Join();
    }
}

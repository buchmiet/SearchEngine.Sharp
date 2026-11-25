using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Threading.Channels;

namespace SearchEngine.Profiling;

/// <summary>Facet predicates of a request evaluated on enumeration data (no extra stat).</summary>
public readonly struct EntryFacets(FindRequest r)
{
    readonly long _min = r.SizeMin ?? 0, _max = r.SizeMax ?? long.MaxValue;
    readonly bool _size = r.SizeMin is not null || r.SizeMax is not null;
    readonly long _cutoff = r.NotOlderThanDays is { } d and > 0 ? CommanderPattern.Cutoff(d) : long.MinValue;
    readonly long _mustHave = r.AttributeMustHave;

    public bool Matches(ref FileSystemEntry e)
    {
        if (_size) { long len = e.Length; if (len < _min || len > _max) return false; }
        if (_cutoff != long.MinValue && e.LastWriteTimeUtc.UtcTicks < _cutoff) return false;
        if (_mustHave != 0 && ((long)e.Attributes & _mustHave) != _mustHave) return false;
        return true;
    }
}

/// <summary>
/// Streaming scans without an index: single-threaded (<c>scan</c>) and with parallel directory workers
/// (<c>scan-parallel</c>). Names are matched by the package's <see cref="NameMatcher"/> with <see cref="SearchTokenization.FileMask"/>, so a hit is exactly what
/// <c>Find</c> would return on an index of the same names.
/// </summary>
public static class StreamingScan
{
    static readonly EnumerationOptions Recursive = new()
    {
        RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0, ReturnSpecialDirectories = false,
    };
    static readonly EnumerationOptions Flat = new()
    {
        RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = 0, ReturnSpecialDirectories = false,
    };

    /// <summary>C: one FileSystemEnumerable pass; name first, stat-backed facets only for name matches.</summary>
    public static IEnumerable<string> Find(FindRequest r)
    {
        var name = CreateNameMatcher(r);
        var facets = new EntryFacets(r);
        return new FileSystemEnumerable<string>(Path.GetFullPath(r.Root), static (ref FileSystemEntry e) => e.ToFullPath(), Recursive)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory && name.IsMatch(e.FileName) && facets.Matches(ref e),
            // Recursive enumeration follows directory symlinks on Unix; /usr/bin/X11 -> . makes that explode.
            ShouldRecursePredicate = static (ref FileSystemEntry e) => (e.Attributes & FileAttributes.ReparsePoint) == 0,
        };
    }

    static NameMatcher CreateNameMatcher(FindRequest r)
    {
        var (expr, method, ops) = CommanderPattern.BuildQuery(r);
        return NameMatcher.Create(expr, method, ops, SearchTokenization.FileMask);
    }

    /// <summary>D: directories processed by <paramref name="threads"/> workers; hits streamed through a channel.</summary>
    public static async IAsyncEnumerable<string> FindParallel(FindRequest r, int threads)
    {
        var name = CreateNameMatcher(r);
        var facets = new EntryFacets(r);
        var hits = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        var pending = new BlockingCollection<string>(new ConcurrentStack<string>()); // LIFO: depth-first like Commander
        int outstanding = 1;
        pending.Add(Path.GetFullPath(r.Root));

        var workers = new Task[threads];
        for (int t = 0; t < threads; t++)
        {
            workers[t] = Task.Factory.StartNew(() =>
            {
                foreach (var dir in pending.GetConsumingEnumerable())
                {
                    try
                    {
                        var en = new FileSystemEnumerable<string?>(dir, (ref FileSystemEntry e) =>
                        {
                            if (e.IsDirectory)
                            {
                                if ((e.Attributes & FileAttributes.ReparsePoint) == 0) { Interlocked.Increment(ref outstanding); pending.Add(e.ToFullPath()); }
                                return null;
                            }
                            return name.IsMatch(e.FileName) && facets.Matches(ref e) ? e.ToFullPath() : null;
                        }, Flat);
                        foreach (var hit in en)
                            if (hit is not null) hits.Writer.TryWrite(hit);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    if (Interlocked.Decrement(ref outstanding) == 0) pending.CompleteAdding();
                }
            }, TaskCreationOptions.LongRunning);
        }
        _ = Task.WhenAll(workers).ContinueWith(_ => hits.Writer.TryComplete());

        await foreach (var h in hits.Reader.ReadAllAsync())
            yield return h;
    }
}

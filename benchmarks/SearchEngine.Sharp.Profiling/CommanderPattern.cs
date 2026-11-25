using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using SearchEngine.Filters;
using SearchEngine.Ingestion;

namespace SearchEngine.Profiling;

/// <summary>Search request mirroring the fields the Commander find-name engine uses.</summary>
public sealed record FindRequest(
    string Root,
    string? NamePattern,
    bool UseRegex = false,
    long? SizeMin = null,
    long? SizeMax = null,
    int? NotOlderThanDays = null,
    long AttributeMustHave = 0);

/// <summary>Per-component accumulators (Stopwatch ticks). Producer and consumer run on different threads.</summary>
public sealed class Breakdown
{
    public long EnumTicks, ExistsTicks, StatTicks, FacetTicks, QueryTicks, DedupTicks, YieldTicks;
    public int Files, Dirs, QueryRuns;
    public readonly List<(int Docs, int Batch, double RebuildMs)> Publishes = [];
    public readonly List<(int Docs, double QueryMs, int Ids)> Queries = [];
    public static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}

/// <summary>
/// Faithful copy of Leith.Commander InternalFindNameEngine (read-only reference), with optional
/// Stopwatch instrumentation and an injectable <see cref="IngestPublishOptions"/> (<c>commander-policy</c> variant).
/// Logic, call order and allocations are kept identical to the original when instrumentation is off.
/// </summary>
public sealed class CommanderPattern(IngestPublishOptions? options, bool instrument, bool fseScan = false)
{
    public Breakdown B { get; } = new();

    public async IAsyncEnumerable<(string Path, string Name)> FindAsync(
        FindRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Root) || !Directory.Exists(request.Root)) yield break;

        var paths = new List<string>();
        var provider = new IndexSnapshotProvider();
        var updater = new IndexUpdater(provider, SearchTokenization.FileMask);
        var engine = new SearchEngineSharp(provider);
        var ingestion = new ProgressiveIndexIngestion(updater);

        var yieldedIds = new HashSet<int>();
        var publishChannel = Channel.CreateUnbounded<bool>(new UnboundedChannelOptions { SingleReader = true });

        var facetFilter = BuildFacetFilter(request);
        var (nameQuery, matchMethod, enableOperators) = BuildQuery(request);

        var ingestTask = ingestion.IngestAsync(
            fseScan ? ScanFseAsync(request.Root, paths, cancellationToken) : ScanFilesAsync(request.Root, paths, cancellationToken),
            options,
            onPublished: e =>
            {
                if (instrument) lock (B.Publishes) B.Publishes.Add((e.IndexedDocumentCount, e.EntryCount, e.RebuildDuration.TotalMilliseconds));
                publishChannel.Writer.TryWrite(true);
            },
            cancellationToken: cancellationToken);

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                while (publishChannel.Reader.TryRead(out _))
                    foreach (var hit in QueryNewHits(engine, paths, yieldedIds, nameQuery, matchMethod, enableOperators, facetFilter))
                        yield return hit;

                if (ingestTask.IsCompleted) break;

                var waitPublish = publishChannel.Reader.WaitToReadAsync(cancellationToken).AsTask();
                await Task.WhenAny(ingestTask, waitPublish).ConfigureAwait(false);
            }

            await ingestTask.ConfigureAwait(false);

            foreach (var hit in QueryNewHits(engine, paths, yieldedIds, nameQuery, matchMethod, enableOperators, facetFilter))
                yield return hit;
        }
        finally
        {
            publishChannel.Writer.TryComplete();
        }
    }

    IEnumerable<(string, string)> QueryNewHits(
        ISearchEngine engine, List<string> paths, HashSet<int> yieldedIds,
        string expression, WordMatchMethod method, bool enableOperators, FacetFilter filter)
    {
        long t0 = instrument ? Stopwatch.GetTimestamp() : 0;
        int docs = instrument ? engine.DocumentCount : 0;
        var ids = engine.Find(expression, method, enableOperators, SearchSortMode.SnapshotOrder, filter);
        if (instrument)
        {
            long q = Stopwatch.GetTimestamp() - t0;
            B.QueryTicks += q;
            B.QueryRuns++;
            B.Queries.Add((docs, Breakdown.Ms(q), ids.Count));
        }

        foreach (var id in ids)
        {
            long t1 = instrument ? Stopwatch.GetTimestamp() : 0;
            if (!yieldedIds.Add(id))
            {
                if (instrument) B.DedupTicks += Stopwatch.GetTimestamp() - t1;
                continue;
            }

            var path = paths[id];
            var hit = (path, Path.GetFileName(path)!);
            if (instrument) B.DedupTicks += Stopwatch.GetTimestamp() - t1;
            yield return hit;
        }
    }

    internal static (string Expression, WordMatchMethod Method, bool EnableOperators) BuildQuery(FindRequest request)
    {
        var raw = string.IsNullOrWhiteSpace(request.NamePattern) ? "*" : request.NamePattern.Trim();
        if (request.UseRegex) return (raw == "*" ? ".*" : raw, WordMatchMethod.Regex, false);
        return (raw, WordMatchMethod.Exact, true);
    }

    internal static FacetFilter BuildFacetFilter(FindRequest request)
    {
        var parts = new List<FacetFilter> { FacetFilter.Mask("attrs", 0, (long)FileAttributes.Directory) };
        if (request.AttributeMustHave != 0) parts.Add(FacetFilter.Mask("attrs", request.AttributeMustHave));
        if (request.SizeMin is not null || request.SizeMax is not null)
            parts.Add(FacetFilter.Range("size", request.SizeMin ?? 0, request.SizeMax ?? long.MaxValue));
        if (request.NotOlderThanDays is { } days and > 0)
            parts.Add(FacetFilter.Range("modified", Cutoff(days), long.MaxValue));
        return FacetFilter.Combine([.. parts]);
    }

    /// <summary>Fixed reference clock so runs on different days select the same files.</summary>
    internal static long Cutoff(int days) => new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(-days).Ticks;

    async IAsyncEnumerable<KeyValuePair<int, IndexedEntry>> ScanFilesAsync(
        string rootDirectory, List<string> paths,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        var stack = new Stack<string>();
        stack.Push(Path.GetFullPath(rootDirectory));

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = stack.Pop();

            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(directory); }
            catch { continue; }

            // Manual enumerator so the time inside MoveNext (readdir) can be measured.
            using var e = entries.GetEnumerator();
            while (true)
            {
                long t0 = instrument ? Stopwatch.GetTimestamp() : 0;
                bool more;
                try { more = e.MoveNext(); }
                catch { more = false; } // original: exception escapes foreach; real trees with ACLs only fail at open
                if (instrument) B.EnumTicks += Stopwatch.GetTimestamp() - t0;
                if (!more) break;
                var entry = e.Current;

                cancellationToken.ThrowIfCancellationRequested();

                long t1 = instrument ? Stopwatch.GetTimestamp() : 0;
                bool isDir = Directory.Exists(entry);
                if (instrument) B.ExistsTicks += Stopwatch.GetTimestamp() - t1;
                if (isDir)
                {
                    stack.Push(entry);
                    if (instrument) B.Dirs++;
                    continue;
                }

                if (instrument) B.Files++;

                long t2 = instrument ? Stopwatch.GetTimestamp() : 0;
                FileInfo info;
                long length, modified, attrs;
                string name;
                try
                {
                    info = new FileInfo(entry);
                    length = info.Length;
                    modified = info.LastWriteTimeUtc.Ticks;
                    attrs = (long)info.Attributes;
                    name = info.Name;
                }
                catch
                {
                    continue; // original: FileInfo ctor in try; Length on a vanished file would throw out of the scan
                }
                long t3 = instrument ? Stopwatch.GetTimestamp() : 0;
                if (instrument) B.StatTicks += t3 - t2;

                var facets = FacetValues.FromDictionary(new Dictionary<string, long>
                {
                    ["size"] = length,
                    ["modified"] = modified,
                    ["attrs"] = attrs
                });

                var id = paths.Count;
                paths.Add(entry);
                var kv = new KeyValuePair<int, IndexedEntry>(id, new IndexedEntry(name, name, facets));
                if (instrument) B.FacetTicks += Stopwatch.GetTimestamp() - t3;
                yield return kv;
            }

            await Task.Yield();
        }
    }

    /// <summary>
    /// Single-pass scan (<c>commander-single-pass</c> variant): one recursive FileSystemEnumerable pass; size, time and attributes come from the
    /// enumeration entry (one lstat per file on Unix), no Directory.Exists, no FileInfo.
    /// Does not follow directory symlinks (the original Directory.Exists check does).
    /// </summary>
    async IAsyncEnumerable<KeyValuePair<int, IndexedEntry>> ScanFseAsync(
        string rootDirectory, List<string> paths,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        var files = new System.IO.Enumeration.FileSystemEnumerable<(string Path, int NameStart, long Size, long Modified, long Attrs)>(
            Path.GetFullPath(rootDirectory),
            static (ref System.IO.Enumeration.FileSystemEntry e) =>
            {
                string full = e.ToFullPath();
                return (full, full.Length - e.FileName.Length, e.Length, e.LastWriteTimeUtc.UtcTicks, (long)e.Attributes);
            },
            options)
        {
            ShouldIncludePredicate = static (ref System.IO.Enumeration.FileSystemEntry e) => !e.IsDirectory,
            ShouldRecursePredicate = static (ref System.IO.Enumeration.FileSystemEntry e) => (e.Attributes & FileAttributes.ReparsePoint) == 0,
        };
        int sinceYield = 0;
        foreach (var f in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = f.Path[f.NameStart..];
            var facets = FacetValues.Create([new("size", f.Size), new("modified", f.Modified), new("attrs", f.Attrs)]);
            var id = paths.Count;
            paths.Add(f.Path);
            yield return new KeyValuePair<int, IndexedEntry>(id, new IndexedEntry(name, name, facets));
            if (++sinceYield == 256) { sinceYield = 0; await Task.Yield(); }
        }
    }
}

using System.Diagnostics;
using SearchEngine;
using SearchEngine.Ingestion;

const string defaultQuery = "file";
int fileCount = GetInt(args, "--count", 20_000);
int seed = GetInt(args, "--seed", 42);
string query = GetQueryArg(args) ?? defaultQuery;
TimeSpan scanDelay = TimeSpan.FromMilliseconds(GetInt(args, "--scan-delay-ms", 1));

var provider = new IndexSnapshotProvider();
var updater = new IndexUpdater(provider);
var engine = new SearchEngineSharp(provider);
var ingestion = new ProgressiveIndexIngestion(updater);

var options = new IngestPublishOptions
{
    Policy = IngestPublishPolicy.Adaptive,
    FixedBatchSize = 2_000,
    MinInterval = TimeSpan.FromMilliseconds(100),
    AdaptiveMultiplier = 2.0,
};

Console.WriteLine("Progressive ingestion demo — match count during scan");
Console.WriteLine($"Query: '{query}'  |  files: {fileCount:N0}  |  policy: adaptive (batch 2k, min 100ms, k=2)");
Console.WriteLine();

int lastCount = 0;
var publishMarks = new List<string>();

var watch = Stopwatch.StartNew();

var ingestTask = ingestion.IngestAsync(
    SyntheticPathFeed.EnumerateAsync(fileCount, seed, scanDelay),
    options,
    onPublished: evt =>
    {
        int count = engine.CountMatches(query, WordMatchMethod.Within);
        publishMarks.Add(
            $"publish #{evt.PublishSequence + 1,2}: indexed {evt.IndexedDocumentCount,6:N0} docs, " +
            $"matches '{query}' = {count,5:N0}, rebuild {evt.RebuildDuration.TotalMilliseconds,5:F0} ms");
        Interlocked.Exchange(ref lastCount, count);
    });

while (!ingestTask.IsCompleted)
{
    int count = engine.CountMatches(query, WordMatchMethod.Within);
    if (count != lastCount)
    {
        Console.WriteLine($"[{watch.Elapsed.TotalSeconds,6:F1}s] matches '{query}': {count,6:N0}");
        lastCount = count;
    }

    await Task.Delay(75);
}

var result = await ingestTask;
watch.Stop();

foreach (string line in publishMarks)
    Console.WriteLine(line);

Console.WriteLine();
Console.WriteLine($"Done in {watch.Elapsed.TotalSeconds:F1}s — publishes: {result.PublishCount}, " +
                  $"rebuild CPU: {result.TotalRebuildCpu.TotalMilliseconds:F0} ms, " +
                  $"worst staleness: {result.WorstCaseStaleness.TotalMilliseconds:F0} ms");
Console.WriteLine($"Final matches for '{query}': {engine.CountMatches(query, WordMatchMethod.Within):N0}");

static int GetInt(string[] args, string flag, int defaultValue)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == flag && int.TryParse(args[i + 1], out int v))
            return v;
    return defaultValue;
}

static string? GetQueryArg(string[] args)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == "--query")
            return args[i + 1];
    return null;
}

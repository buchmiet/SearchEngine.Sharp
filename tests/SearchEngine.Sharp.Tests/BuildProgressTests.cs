using SearchEngine.Snapshots;

namespace SearchEngine.Sharp.Tests;

// The builder reports at most once per whole percent and always ends with 100, so a progress handler that posts
// to a UI thread is not flooded by large builds.
public class BuildProgressTests
{
    private sealed class RecordingProgress : IProgress<float>
    {
        public List<float> Values { get; } = [];
        public void Report(float value) => Values.Add(value);
    }

    private static Dictionary<int, string> Entries(int count)
        => Enumerable.Range(0, count).ToDictionary(i => i, i => $"name{i} part{i % 97} common");

    private static async Task AssertCadence(List<float> values)
    {
        await Assert.That(values.Count).IsBetween(2, 102);
        await Assert.That(values[^1]).IsEqualTo(100f);
        for (int i = 1; i < values.Count; i++)
            await Assert.That((int)values[i]).IsGreaterThan((int)values[i - 1]).Because($"Report {i} ({values[i]}) does not reach a new whole percent after {values[i - 1]}.");
    }

    [Test]
    [Arguments(1)]
    [Arguments(50)]
    [Arguments(20_000)]
    public async Task Build_reports_once_per_whole_percent_and_ends_at_100(int count)
    {
        var progress = new RecordingProgress();

        IndexSnapshotBuilder.Build(Entries(count), progress);

        await AssertCadence(progress.Values);
        await Assert.That(progress.Values[0]).IsEqualTo(0f);
    }

    [Test]
    public async Task Build_of_a_sequence_without_a_count_reports_the_first_half_once()
    {
        static IEnumerable<KeyValuePair<int, string>> Stream()
        {
            for (int i = 0; i < 5_000; i++)
                yield return new(i, $"name{i}");
        }

        var progress = new RecordingProgress();

        IndexSnapshotBuilder.Build(Stream(), progress);

        await AssertCadence(progress.Values);
        await Assert.That(progress.Values[0]).IsEqualTo(50f);
    }

    [Test]
    public async Task Build_of_no_entries_reports_100()
    {
        var progress = new RecordingProgress();

        IndexSnapshotBuilder.Build(new Dictionary<int, string>(), progress);

        await Assert.That(progress.Values).IsEquivalentTo([100f], CollectionOrdering.Matching);
    }

    [Test]
    public async Task RebuildFromAsync_passes_the_same_cadence_through()
    {
        static async IAsyncEnumerable<KeyValuePair<int, string>> Stream()
        {
            for (int i = 0; i < 20_000; i++)
            {
                if (i % 5_000 == 0)
                    await Task.Yield();
                yield return new(i, $"name{i}");
            }
        }

        var progress = new RecordingProgress();

        await new IndexUpdater(new IndexSnapshotProvider()).RebuildFromAsync(Stream(), progress);

        await AssertCadence(progress.Values);
    }
}

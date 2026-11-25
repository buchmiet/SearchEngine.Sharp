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

    private static void AssertCadence(List<float> values)
    {
        Assert.InRange(values.Count, 2, 102);
        Assert.Equal(100f, values[^1]);
        for (int i = 1; i < values.Count; i++)
            Assert.True((int)values[i] > (int)values[i - 1], $"Report {i} ({values[i]}) does not reach a new whole percent after {values[i - 1]}.");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(20_000)]
    public void Build_reports_once_per_whole_percent_and_ends_at_100(int count)
    {
        var progress = new RecordingProgress();

        IndexSnapshotBuilder.Build(Entries(count), progress);

        AssertCadence(progress.Values);
        Assert.Equal(0f, progress.Values[0]);
    }

    [Fact]
    public void Build_of_a_sequence_without_a_count_reports_the_first_half_once()
    {
        static IEnumerable<KeyValuePair<int, string>> Stream()
        {
            for (int i = 0; i < 5_000; i++)
                yield return new(i, $"name{i}");
        }

        var progress = new RecordingProgress();

        IndexSnapshotBuilder.Build(Stream(), progress);

        AssertCadence(progress.Values);
        Assert.Equal(50f, progress.Values[0]);
    }

    [Fact]
    public void Build_of_no_entries_reports_100()
    {
        var progress = new RecordingProgress();

        IndexSnapshotBuilder.Build(new Dictionary<int, string>(), progress);

        Assert.Equal([100f], progress.Values);
    }

    [Fact]
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

        AssertCadence(progress.Values);
    }
}

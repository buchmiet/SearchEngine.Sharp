using SearchEngine.Snapshots;

namespace SearchEngine.Sharp.Tests;

// What an IndexUpdater keeps besides its entries: the preset it was created with (separators and Unicode options),
// published even while the index is empty, after Clear and after the last entry is removed, so queries and NameMatcher
// instances built from Current.Tokenization keep matching like the filled index. It stores search texts folded once,
// and its async rebuild does not post back to the caller's synchronization context (a UI thread). Atomicity is in
// IndexUpdaterAtomicityTests, the segment layout in IndexUpdaterSegmentTests, and the answers after every kind of
// change in IndexUpdaterDifferentialTests.
public class IndexUpdaterTests
{
    private static SearchTokenization PresetByName(string name) => name switch
    {
        "default" => SearchTokenization.Default,
        "filemask" => SearchTokenization.FileMask,
        _ => SearchTokenization.Create("_", " "),
    };

    [Test]
    [Arguments("default")]
    [Arguments("filemask")]
    [Arguments("custom")]
    public Task Clear_keeps_the_preset_of_the_updater(string preset)
        => AssertClearKeepsThePreset(PresetByName(preset));

    // Compose needs Unicode normalization, so this preset is skipped in globalization-invariant mode.
    [Test]
    [SkipUnlessNormalization]
    public Task Clear_keeps_the_preset_with_its_Unicode_options()
        => AssertClearKeepsThePreset(
            SearchTokenization.FileMask.WithUnicodeMatching(UnicodeMatching.Compose | UnicodeMatching.CaseFold));

    // The updater stores every search text folded, so one AddEntry rebuilds without folding the stored names again.
    // Measured on 20,000 accented names: 22.7 MiB per AddEntry, against 49.2 MiB when every rebuild folded the
    // names as given.
    // The bound is the measurement plus about 15%.
    [Test]
    [SkipUnlessNormalization]
    public async Task AddEntry_does_not_fold_the_stored_names_again()
    {
        const long Bound = 26L * 1024 * 1024;
        string[] parts = ["żółw", "łąka", "café", "Ärger", "señor", "naïve", "Straße", "über", "piñata", "crème", "doc", "plik"];
        var random = new Random(42);
        var entries = new Dictionary<int, string>(20_000);
        for (int i = 0; i < 20_000; i++)
            entries[i] = $"{parts[random.Next(parts.Length)]}_{parts[random.Next(parts.Length)]}{i % 977} {parts[random.Next(parts.Length)]}-{i}.pdf";

        var (_, updater, _) = TestIndex.Create(
            SearchTokenization.Default.WithUnicodeMatching(UnicodeMatching.Compose | UnicodeMatching.CaseFold | UnicodeMatching.RemoveDiacritics));
        updater.RebuildFrom(entries);
        updater.AddEntry(20_000, "Żółta łódź café-0.pdf"); // first call: JIT and one-time setup

        long before = GC.GetAllocatedBytesForCurrentThread();
        updater.AddEntry(20_001, "Żółta łódź café-1.pdf");
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        await Assert.That(allocated).IsLessThan(Bound);
    }

    private static async Task AssertClearKeepsThePreset(SearchTokenization tokenization)
    {
        var (engine, updater, provider) = TestIndex.Create(tokenization);
        var entries = new Dictionary<int, string>
        {
            [1] = "Report_Final.PDF",
            [2] = "report draft.txt",
            [3] = "STRASSE-notes.pdf",
            [4] = "Straße.pdf",
        };
        string[] queries = ["report", "final", "pdf", "strasse", "Straße", "report draft", "_final"];

        updater.RebuildFrom(entries);
        var before = queries.Select(q => TestIndex.Ids(engine, q)).ToArray();

        updater.Clear();

        await Assert.That(provider.Current.Tokenization.IndexSeparators).IsEqualTo(tokenization.IndexSeparators);
        await Assert.That(provider.Current.Tokenization.QuerySeparators).IsEqualTo(tokenization.QuerySeparators);
        await Assert.That(provider.Current.Tokenization.UnicodeMatching).IsEqualTo(tokenization.UnicodeMatching);
        await Assert.That(updater.EntryCount).IsEqualTo(0);
        foreach (string q in queries)
            await Assert.That(TestIndex.Ids(engine, q)).IsEmpty();

        updater.RebuildFrom(entries);
        await Assert.That(queries.Select(q => TestIndex.Ids(engine, q)).ToArray()).IsEquivalentTo(before, CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_new_updater_publishes_an_empty_snapshot_of_its_preset()
    {
        var (_, _, provider) = TestIndex.Create(SearchTokenization.FileMask);

        await Assert.That(provider.Current.Tokenization).IsSameReferenceAs(SearchTokenization.FileMask);
        await Assert.That(provider.Current.DocumentCount).IsEqualTo(0);
    }

    [Test]
    public async Task Removing_the_last_entry_keeps_the_preset()
    {
        var tokenization = SearchTokenization.FileMask.WithUnicodeMatching(UnicodeMatching.CaseFold);
        var (_, updater, provider) = TestIndex.Create(tokenization);
        updater.AddEntry(1, "only.txt");

        updater.RemoveEntry(1);

        await Assert.That(provider.Current.Tokenization.UnicodeMatching).IsEqualTo(UnicodeMatching.CaseFold);
        await Assert.That(provider.Current.Tokenization.QuerySeparators).IsEqualTo(tokenization.QuerySeparators);
    }

    [Test]
    public async Task Clear_with_the_default_preset_publishes_the_shared_empty_snapshot()
    {
        var (_, updater, provider) = TestIndex.Create();
        updater.AddEntry(1, "a");

        updater.Clear();

        await Assert.That(provider.Current).IsSameReferenceAs(IndexSnapshot.Empty);
    }

    [Test]
    public async Task RebuildFromAsync_does_not_resume_on_the_callers_synchronization_context()
    {
        var (engine, updater, _) = TestIndex.Create();
        var context = new CountingSynchronizationContext();
        Task rebuild;

        // Restore what the test runner had installed, not null.
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            rebuild = updater.RebuildFromAsync(SlowStream());
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        await rebuild;

        await Assert.That(context.PostCount).IsEqualTo(0);
        await Assert.That(TestIndex.Ids(engine, "alpha")).IsEquivalentTo([1], CollectionOrdering.Matching);
    }

    // Completes asynchronously without capturing a context of its own, so only the updater could post to one.
    private static async IAsyncEnumerable<KeyValuePair<int, string>> SlowStream()
    {
        await Task.Delay(1).ConfigureAwait(false);
        yield return new(1, "alpha");
    }

    private sealed class CountingSynchronizationContext : SynchronizationContext
    {
        private int _postCount;

        public int PostCount => Volatile.Read(ref _postCount);

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _postCount);
            base.Post(d, state);
        }
    }
}

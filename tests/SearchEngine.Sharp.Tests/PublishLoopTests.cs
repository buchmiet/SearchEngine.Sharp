using SearchEngine.Ingestion;

namespace SearchEngine.Sharp.Tests;

// The publisher's decisions, unit tested on a manual clock (src/Ingestion/PublishSchedule.cs, PendingBatch.cs,
// WakeTimer.cs, PublishLoop.cs): when a batch is full for each policy, when each policy's timer is due, how Adaptive
// paces publishes after a slow one (PublishMultiplier, StrictMaxStaleness), that no setting overflows the timer
// arithmetic, and that waiting for the next entry ends cleanly when the scanner failed or the caller cancelled, so the
// final flush is never skipped. ProgressiveIndexIngestionTests runs the same rules end to end.
public class PublishLoopTests
{
    private static readonly TimeSpan Ms = TimeSpan.FromMilliseconds(1);

    private static void AddEntries(PendingBatch batch, ManualTimeProvider clock, int count)
    {
        for (int i = 0; i < count; i++)
            batch.Add(new BufferedEntry(batch.Count, new IndexedEntry($"file-{i}.txt", $"file-{i}.txt"), clock.GetTimestamp()));
    }

    [Test]
    public async Task A_batch_is_full_at_the_size_setting_of_each_policy()
    {
        var clock = new ManualTimeProvider();
        var fixedBatch = new PublishSchedule(new IngestPublishPolicy.FixedBatch { BatchSize = 3 }, clock);
        var adaptive = new PublishSchedule(new IngestPublishPolicy.Adaptive { BatchCap = 5 }, clock);
        var perEntry = new PublishSchedule(new IngestPublishPolicy.PerEntry(), clock);
        var debounce = new PublishSchedule(new IngestPublishPolicy.TimeDebounce(), clock);

        await Assert.That(fixedBatch.IsBatchFull(2)).IsFalse();
        await Assert.That(fixedBatch.IsBatchFull(3)).IsTrue();
        await Assert.That(adaptive.IsBatchFull(4)).IsFalse();
        await Assert.That(adaptive.IsBatchFull(5)).IsTrue();
        await Assert.That(perEntry.IsBatchFull(0)).IsFalse();
        await Assert.That(perEntry.IsBatchFull(1)).IsTrue();
        await Assert.That(debounce.IsBatchFull(int.MaxValue - 1)).IsFalse();
    }

    [Test]
    [Arguments(true, 100)]
    [Arguments(false, 10)]
    public async Task Adaptive_batch_cap_grows_with_the_index_only_when_growth_aware(bool growthAware, int expectedCap)
    {
        var schedule = new PublishSchedule(
            new IngestPublishPolicy.Adaptive { BatchCap = 10, GrowthAwareBatchCap = growthAware },
            new ManualTimeProvider());

        schedule.RecordPublish(TimeSpan.Zero, indexedCount: 100);

        await Assert.That(schedule.IsBatchFull(expectedCap - 1)).IsFalse();
        await Assert.That(schedule.IsBatchFull(expectedCap)).IsTrue();
    }

    [Test]
    public async Task FixedBatch_and_PerEntry_arm_no_timer_and_FixedBatch_is_due_once_stale()
    {
        var clock = new ManualTimeProvider();
        var fixedSchedule = new PublishSchedule(new IngestPublishPolicy.FixedBatch { MaxStaleness = 100 * Ms }, clock);
        var perEntrySchedule = new PublishSchedule(new IngestPublishPolicy.PerEntry(), clock);
        var batch = new PendingBatch();

        await Assert.That(fixedSchedule.TimeUntilTimerPublish(batch)).IsNull();
        AddEntries(batch, clock, 1);
        await Assert.That(fixedSchedule.TimeUntilTimerPublish(batch)).IsNull();

        clock.Advance(100 * Ms);

        await Assert.That(fixedSchedule.TimeUntilTimerPublish(batch)).IsEqualTo(TimeSpan.Zero);
        await Assert.That(perEntrySchedule.TimeUntilTimerPublish(batch)).IsNull();
    }

    [Test]
    public async Task TimeDebounce_is_due_MinInterval_after_the_last_publish()
    {
        var clock = new ManualTimeProvider();
        var schedule = new PublishSchedule(new IngestPublishPolicy.TimeDebounce { MinInterval = 100 * Ms }, clock);
        var batch = new PendingBatch();
        AddEntries(batch, clock, 1);

        clock.Advance(30 * Ms);
        await Assert.That(schedule.TimeUntilTimerPublish(batch)).IsEqualTo(70 * Ms);

        clock.Advance(80 * Ms);
        await Assert.That(schedule.TimeUntilTimerPublish(batch)).IsEqualTo(TimeSpan.Zero);
    }

    [Test]
    public async Task Adaptive_timer_waits_PublishMultiplier_times_the_last_publish_only_from_MinTimerPublishBatch()
    {
        // Rebuild 400 ms, k = 2: the interval is max(100 ms, 800 ms). 300 ms later, 500 ms are left. One entry is
        // below MinTimerPublishBatch, so only staleness counts: max(1000 - 300, 800 - 300) = 700 ms.
        var clock = new ManualTimeProvider();
        var schedule = new PublishSchedule(new IngestPublishPolicy.Adaptive { MinTimerPublishBatch = 2 }, clock);
        schedule.RecordPublish(400 * Ms, indexedCount: 0);
        var batch = new PendingBatch();
        AddEntries(batch, clock, 1);
        clock.Advance(300 * Ms);

        await Assert.That(schedule.TimeUntilTimerPublish(batch)).IsEqualTo(700 * Ms);

        AddEntries(batch, clock, 1);
        await Assert.That(schedule.TimeUntilTimerPublish(batch)).IsEqualTo(500 * Ms);
    }

    [Test]
    [Arguments(false, 200)]
    [Arguments(true, 0)]
    public async Task Adaptive_staleness_waits_for_the_pacing_gap_unless_strict(bool strict, int expectedMs)
    {
        // Rebuild 600 ms, k = 2: a staleness publish may start 1200 ms after the last publish. The entry is
        // 1000 ms old, at MaxStaleness.
        var clock = new ManualTimeProvider();
        var schedule = new PublishSchedule(new IngestPublishPolicy.Adaptive { StrictMaxStaleness = strict }, clock);
        schedule.RecordPublish(600 * Ms, indexedCount: 0);
        var batch = new PendingBatch();
        AddEntries(batch, clock, 1);
        clock.Advance(1000 * Ms);

        await Assert.That(schedule.TimeUntilTimerPublish(batch)).IsEqualTo(expectedMs * Ms);
    }

    [Test]
    public async Task The_largest_settings_never_overflow_the_timer_arithmetic()
    {
        // The largest values of every time setting, a rebuild of a day and an entry a year old.
        var clock = new ManualTimeProvider();
        IngestPublishPolicy[] policies =
        [
            new IngestPublishPolicy.Adaptive
            {
                MinInterval = TimeSpan.MaxValue,
                MaxStaleness = TimeSpan.MaxValue,
                PublishMultiplier = IngestPublishPolicy.Adaptive.MaxPublishMultiplier,
                MinTimerPublishBatch = 1,
            },
            new IngestPublishPolicy.TimeDebounce { MinInterval = TimeSpan.MaxValue, MaxStaleness = TimeSpan.MaxValue },
            new IngestPublishPolicy.FixedBatch { BatchSize = int.MaxValue, MaxStaleness = TimeSpan.MaxValue },
        ];
        int failures = 0;

        foreach (var policy in policies)
        {
            var schedule = new PublishSchedule(policy, clock);
            schedule.RecordPublish(TimeSpan.FromDays(1), indexedCount: int.MaxValue);
            var batch = new PendingBatch();
            AddEntries(batch, clock, 1);
            clock.Advance(TimeSpan.FromDays(365));
            try
            {
                if (schedule.TimeUntilTimerPublish(batch) is { } wait)
                {
                    using var timer = new WakeTimer(clock);
                    _ = timer.Arm(wait);
                    timer.Disarm();
                }
            }
            catch (OverflowException)
            {
                failures++;
            }
        }

        await Assert.That(failures).IsEqualTo(0);
    }

    [Test]
    public async Task A_pending_batch_keeps_the_timestamp_of_its_oldest_entry_until_cleared()
    {
        var clock = new ManualTimeProvider();
        var batch = new PendingBatch();
        AddEntries(batch, clock, 1);
        long oldest = clock.GetTimestamp();
        clock.Advance(50 * Ms);
        AddEntries(batch, clock, 1);

        await Assert.That(batch.Count).IsEqualTo(2);
        await Assert.That(batch.OldestTimestamp).IsEqualTo(oldest);

        batch.Clear();
        AddEntries(batch, clock, 1);
        await Assert.That(batch.OldestTimestamp).IsEqualTo(clock.GetTimestamp());
    }

    [Test]
    public async Task The_wake_timer_rounds_up_to_whole_milliseconds_and_reuses_its_task_until_it_fires()
    {
        var clock = new ManualTimeProvider();
        using var timer = new WakeTimer(clock);

        Task first = timer.Arm(TimeSpan.FromMilliseconds(1.2));
        Task rearmed = timer.Arm(TimeSpan.FromMilliseconds(1.2));
        await Assert.That(rearmed).IsSameReferenceAs(first);
        await Assert.That(first.IsCompleted).IsFalse();

        await Assert.That(clock.TryFireEarliest(TimeSpan.Zero)).IsTrue();
        await first.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(clock.GetTimestamp()).IsEqualTo((2 * Ms).Ticks);

        Task second = timer.Arm(Ms);
        await Assert.That(second).IsNotSameReferenceAs(first);
        await Assert.That(second.IsCompleted).IsFalse();

        timer.Disarm();
        await Assert.That(clock.ArmedTimerCount).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Waiting_for_an_entry_reports_the_end_of_input_when_the_scanner_failed(bool callerCancelled)
    {
        // A failed channel throws the scanner's exception from WaitToReadAsync. That can happen before the
        // channel's Completion task is complete, and after the caller cancelled; neither may skip the final flush.
        using var cts = new CancellationTokenSource();
        if (callerCancelled)
            cts.Cancel();
        var completion = callerCancelled ? Task.FromException(new IOException("scan failed")) : new TaskCompletionSource().Task;
        var reader = new FailedChannelReader(new IOException("scan failed"), completion);

        bool entriesAvailable = await PublishLoop.WaitForEntryAsync(reader, cts.Token);

        await Assert.That(entriesAvailable).IsFalse();
    }

    [Test]
    public async Task Waiting_for_an_entry_stops_on_the_callers_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var reader = new FailedChannelReader(new OperationCanceledException(cts.Token), new TaskCompletionSource().Task);

        await Assert.That(() => PublishLoop.WaitForEntryAsync(reader, cts.Token)).ThrowsExactly<OperationCanceledException>();
    }

    // A channel reader whose wait fails with error, as a channel completed with an error does.
    private sealed class FailedChannelReader(Exception error, Task completion) : System.Threading.Channels.ChannelReader<int>
    {
        public override Task Completion => completion;

        public override bool TryRead(out int item)
        {
            item = 0;
            return false;
        }

        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<bool>(error);
    }
}

namespace SearchEngine.Ingestion;

// One timer for the whole ingestion, re-armed for each wait, so a wake-up needs no new delay or cancellation source.
// A firing that arrives before the publish is due, or after the publisher has moved on, causes one extra pass of the
// publish loop, which re-arms the timer for the time that is left.
internal sealed class WakeTimer : IDisposable
{
    // Caps a wait within what a timer accepts; the loop re-arms for the rest.
    private static readonly TimeSpan LongestWait = TimeSpan.FromDays(1);

    private readonly ITimer _timer;
    private TaskCompletionSource _fired = NewSource();

    public WakeTimer(TimeProvider timeProvider)
    {
        _timer = timeProvider.CreateTimer(
            static state => ((WakeTimer)state!).Fire(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    // Arms the timer for wait, rounded up to whole milliseconds, and returns a task that completes when it fires.
    // Timers truncate to whole milliseconds, so a wait that is not rounded up would spin over the last fraction.
    public Task Arm(TimeSpan wait)
    {
        if (_fired.Task.IsCompleted)
            Volatile.Write(ref _fired, NewSource());

        TimeSpan capped = wait < LongestWait ? wait : LongestWait;
        _timer.Change(TimeSpan.FromMilliseconds(Math.Ceiling(capped.TotalMilliseconds)), Timeout.InfiniteTimeSpan);
        return _fired.Task;
    }

    public void Disarm() => _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    public void Dispose() => _timer.Dispose();

    private void Fire() => Volatile.Read(ref _fired).TrySetResult();

    // The publisher must not run its publish on the timer's callback thread.
    private static TaskCompletionSource NewSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

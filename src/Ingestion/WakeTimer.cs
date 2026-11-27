namespace SearchEngine.Ingestion;

/// <summary>
/// One timer for the whole ingestion, re-armed for each wait instead of a new delay and cancellation source per
/// wake-up. A firing that arrives after the publisher has moved on only causes one extra pass of the publish loop,
/// which re-arms the timer for the time that is really left.
/// </summary>
internal sealed class WakeTimer : IDisposable
{
    // Timers have millisecond resolution and truncate: rounding up keeps a re-armed wait from degenerating into a
    // spin over the last fraction of a millisecond. The cap keeps very long waits within what a timer accepts; the
    // loop simply re-arms.
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

    /// <summary>Arms the timer for <paramref name="wait"/>, rounded up to whole milliseconds, and returns a task
    /// that completes when it fires.</summary>
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

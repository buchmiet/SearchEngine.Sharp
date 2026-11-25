namespace SearchEngine.Sharp.Tests;

/// <summary>
/// A clock that moves only when a test or a simulated rebuild moves it. Timers fire when a test
/// calls <see cref="FireTimersUntilAsync"/>, optionally just before they are due, like a real
/// timer that wakes a fraction of a millisecond early.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private long _now;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Volatile.Read(ref _now);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _now, by.Ticks);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>
    /// Fires the earliest armed timer until <paramref name="done"/> holds. Only the first firing is
    /// <paramref name="early"/> (before its due time): a timer re-armed for the remaining
    /// sub-millisecond must fire on time or the clock would never get there.
    /// </summary>
    public async Task FireTimersUntilAsync(Func<bool> done, TimeSpan early, TimeSpan timeout)
    {
        var elapsed = global::System.Diagnostics.Stopwatch.StartNew();
        while (!done())
        {
            await Assert.That(elapsed.Elapsed).IsLessThan(timeout).Because("Timed out: no timer was armed and the expected publish never happened.");
            if (TryFireEarliest(early))
                early = TimeSpan.Zero;
            else
                await Task.Delay(1);
        }
    }

    /// <summary>Fires the earliest armed timer, <paramref name="early"/> before its due time; false when none is armed.</summary>
    public bool TryFireEarliest(TimeSpan early)
    {
        ManualTimer? next;
        lock (_gate)
        {
            next = _timers.MinBy(static t => t.DueTimestamp);
            if (next is null)
                return false;

            _timers.Remove(next);
            Volatile.Write(ref _now, Math.Max(Volatile.Read(ref _now), next.DueTimestamp - early.Ticks));
        }

        next.Fire(); // outside the lock: the publisher continues inline and may arm or dispose timers
        return true;
    }

    public int ArmedTimerCount
    {
        get
        {
            lock (_gate)
                return _timers.Count;
        }
    }

    private void Register(ManualTimer timer)
    {
        lock (_gate)
        {
            if (!_timers.Contains(timer))
                _timers.Add(timer);
        }
    }

    private void Unregister(ManualTimer timer)
    {
        lock (_gate)
            _timers.Remove(timer);
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public long DueTimestamp { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime == Timeout.InfiniteTimeSpan)
            {
                owner.Unregister(this);
                return true;
            }

            DueTimestamp = owner.GetTimestamp() + dueTime.Ticks;
            owner.Register(this);
            return true;
        }

        public void Fire() => callback(state);

        public void Dispose() => owner.Unregister(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

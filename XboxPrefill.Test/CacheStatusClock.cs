namespace XboxPrefill.Test;

public sealed class CacheStatusClock : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ClockTimer> _timers = new();
    private DateTimeOffset _utcNow;

    public CacheStatusClock(DateTimeOffset utcNow)
    {
        _utcNow = utcNow;
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _utcNow;
        }
    }

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new ClockTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed));
        }

        List<ClockTimer> due;
        lock (_gate)
        {
            _utcNow += elapsed;
            due = _timers
                .Where(timer => !timer.Disposed && timer.DueAt <= _utcNow)
                .ToList();

            foreach (var timer in due)
            {
                if (timer.Period == Timeout.InfiniteTimeSpan)
                {
                    _timers.Remove(timer);
                    timer.DueAt = DateTimeOffset.MaxValue;
                }
                else
                {
                    timer.DueAt += timer.Period;
                }
            }
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    private bool Change(ClockTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        if (dueTime < TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(dueTime));
        }

        if (period < TimeSpan.Zero && period != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(period));
        }

        lock (_gate)
        {
            if (timer.Disposed)
            {
                return false;
            }

            _timers.Remove(timer);
            timer.Period = period;
            timer.DueAt = dueTime == Timeout.InfiniteTimeSpan
                ? DateTimeOffset.MaxValue
                : _utcNow + dueTime;
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                _timers.Add(timer);
            }

            return true;
        }
    }

    private void Remove(ClockTimer timer)
    {
        lock (_gate)
        {
            timer.Disposed = true;
            _timers.Remove(timer);
        }
    }

    private sealed class ClockTimer : ITimer
    {
        private readonly CacheStatusClock _clock;
        private readonly TimerCallback _callback;
        private readonly object? _state;

        public ClockTimer(CacheStatusClock clock, TimerCallback callback, object? state)
        {
            _clock = clock;
            _callback = callback;
            _state = state;
        }

        public DateTimeOffset DueAt { get; set; }
        public TimeSpan Period { get; set; }
        public bool Disposed { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
            => _clock.Change(this, dueTime, period);

        public void Dispose()
            => _clock.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public void Fire()
        {
            if (!Disposed)
            {
                _callback(_state);
            }
        }
    }
}

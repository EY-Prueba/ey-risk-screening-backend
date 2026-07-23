namespace EyRiskScreening.UnitTests.Infrastructure;

internal sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private readonly object _sync = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _utcNow = utcNow;
    private long _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_sync)
        {
            return _utcNow;
        }
    }

    public override long GetTimestamp()
    {
        lock (_sync)
        {
            return _timestamp;
        }
    }

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new ManualTimer(this, callback, state, dueTime, period);

        lock (_sync)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    public void Advance(TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);

        lock (_sync)
        {
            _utcNow = _utcNow.Add(duration);
            _timestamp = checked(_timestamp + duration.Ticks);
        }

        while (true)
        {
            ManualTimer[] dueTimers;
            lock (_sync)
            {
                dueTimers = _timers
                    .Where(timer => timer.IsDue(_timestamp))
                    .ToArray();

                foreach (var timer in dueTimers)
                {
                    timer.PrepareNext(_timestamp);
                }
            }

            if (dueTimers.Length == 0)
            {
                return;
            }

            foreach (var timer in dueTimers)
            {
                timer.Invoke();
            }
        }
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private long _dueTimestamp = long.MaxValue;
        private long _periodTicks = Timeout.InfiniteTimeSpan.Ticks;
        private bool _disposed;

        public ManualTimer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
            _ = Change(dueTime, period);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            ValidateTimeout(dueTime, nameof(dueTime));
            ValidateTimeout(period, nameof(period));

            lock (_owner._sync)
            {
                if (_disposed)
                {
                    return false;
                }

                _dueTimestamp = dueTime == Timeout.InfiniteTimeSpan
                    ? long.MaxValue
                    : checked(_owner._timestamp + dueTime.Ticks);
                _periodTicks = period.Ticks;
                return true;
            }
        }

        public void Dispose()
        {
            lock (_owner._sync)
            {
                _disposed = true;
                _ = _owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public bool IsDue(long timestamp) =>
            !_disposed && _dueTimestamp <= timestamp;

        public void PrepareNext(long timestamp)
        {
            _dueTimestamp = _periodTicks == Timeout.InfiniteTimeSpan.Ticks
                ? long.MaxValue
                : checked(timestamp + _periodTicks);
        }

        public void Invoke()
        {
            if (!_disposed)
            {
                _callback(_state);
            }
        }

        private static void ValidateTimeout(TimeSpan value, string parameterName)
        {
            if (value < TimeSpan.Zero && value != Timeout.InfiniteTimeSpan)
            {
                throw new ArgumentOutOfRangeException(parameterName);
            }
        }
    }
}

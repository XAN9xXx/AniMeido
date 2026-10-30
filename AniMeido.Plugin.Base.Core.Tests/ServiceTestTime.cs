namespace AniMeido.Tests;

/// <summary>只推进测试明确指定的时间；同时驱动冷却等待和尝试超时。</summary>
internal sealed class ServiceTestTime : TimeProvider
{
    private readonly object _sync = new();
    private readonly List<TestTimer> _timers = [];
    private readonly SemaphoreSlim _changed = new(0);
    private long _ticks;
    private static readonly DateTimeOffset Origin = new(2026, 10, 30, 0, 0, 0, TimeSpan.Zero);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() { lock (_sync) return _ticks; }
    public override DateTimeOffset GetUtcNow() { lock (_sync) return Origin.AddTicks(_ticks); }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new TestTimer(this, callback, state);
        lock (_sync) _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    internal async Task WaitForDelayAsync(TimeSpan delay, int count = 1)
    {
        while (true)
        {
            lock (_sync)
                if (_timers.Count(timer => !timer.Disposed && timer.Due - _ticks == delay.Ticks) >= count)
                    return;
            if (!await _changed.WaitAsync(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Expected test timer was not created.");
        }
    }

    internal void Advance(TimeSpan elapsed)
    {
        List<TestTimer> due;
        lock (_sync)
        {
            _ticks += elapsed.Ticks;
            due = _timers.Where(timer => !timer.Disposed && timer.Due <= _ticks).ToList();
            foreach (var timer in due)
                timer.Due = timer.Period > 0 ? _ticks + timer.Period : long.MaxValue;
        }
        foreach (var timer in due)
            timer.Callback(timer.State);
    }

    private sealed class TestTimer(ServiceTestTime owner, TimerCallback callback, object? state) : ITimer
    {
        internal TimerCallback Callback { get; } = callback;
        internal object? State { get; } = state;
        internal long Due { get; set; } = long.MaxValue;
        internal long Period { get; private set; }
        internal bool Disposed { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._sync)
            {
                if (Disposed) return false;
                Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner._ticks + dueTime.Ticks;
                Period = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
            }
            owner._changed.Release();
            return true;
        }

        public void Dispose() { lock (owner._sync) Disposed = true; }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

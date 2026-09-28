namespace Penghou.Zhinu.Testing;

/// <summary>
/// Deterministic <see cref="TimeProvider"/> for tests. Time only moves when
/// <see cref="Advance"/> is called, and timers scheduled through
/// <see cref="CreateTimer"/> fire when the clock passes their due time. This
/// makes <c>Task.Delay</c>, cancellation timeouts, and parked retry or deadline
/// waits controllable without real waiting.
/// </summary>
public sealed class TestTimeProvider : TimeProvider
{
    private readonly object gate = new();
    private readonly List<TestTimer> timers = [];
    private DateTimeOffset utcNow;

    /// <summary>Creates a clock frozen at <paramref name="utcNow"/>.</summary>
    public TestTimeProvider(DateTimeOffset utcNow) => this.utcNow = utcNow;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow()
    {
        lock (gate)
            return utcNow;
    }

    /// <inheritdoc />
    public override long GetTimestamp()
    {
        lock (gate)
            return utcNow.UtcTicks;
    }

    /// <inheritdoc />
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <inheritdoc />
    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new TestTimer(this, callback, state, dueTime, period);
        if (timer.Pending)
            Register(timer);
        return timer;
    }

    /// <summary>
    /// Moves the clock forward and fires every timer that becomes due, in due
    /// order. Callbacks run after the clock is updated and outside the internal
    /// lock so they may schedule or dispose timers.
    /// </summary>
    /// <param name="delta">The non-negative amount to advance.</param>
    public void Advance(TimeSpan delta)
    {
        if (delta < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(delta),
                delta,
                "Fake time cannot move backwards.");

        var due = new List<TestTimer>();
        lock (gate)
        {
            utcNow = utcNow.Add(delta);
            foreach (var timer in timers)
                timer.CollectDue(utcNow, due);
        }
        foreach (var timer in due)
            timer.Fire();
    }

    private void Register(TestTimer timer)
    {
        lock (gate)
        {
            if (!timers.Contains(timer))
                timers.Add(timer);
        }
    }

    private void Unregister(TestTimer timer)
    {
        lock (gate)
            timers.Remove(timer);
    }

    private sealed class TestTimer : ITimer
    {
        private readonly TestTimeProvider owner;
        private readonly TimerCallback callback;
        private readonly object? state;
        private readonly object gate = new();
        private DateTimeOffset? dueAt;
        private TimeSpan period;
        private bool disposed;

        public TestTimer(
            TestTimeProvider owner,
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            this.owner = owner;
            this.callback = callback;
            this.state = state;
            Schedule(dueTime, period);
        }

        public bool Pending
        {
            get
            {
                lock (gate)
                    return !disposed && dueAt is not null;
            }
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            bool pending;
            lock (gate)
            {
                if (disposed)
                    return false;
                Schedule(dueTime, period);
                pending = dueAt is not null;
            }
            if (pending)
                owner.Register(this);
            return true;
        }

        /// <summary>Called under the owner's lock. Records one due fire.</summary>
        public void CollectDue(DateTimeOffset now, List<TestTimer> due)
        {
            lock (gate)
            {
                if (disposed || dueAt is null || dueAt.Value > now)
                    return;
                due.Add(this);
                dueAt = period > TimeSpan.Zero && period != Timeout.InfiniteTimeSpan
                    ? dueAt.Value + period
                    : null;
            }
        }

        public void Fire()
        {
            lock (gate)
            {
                if (disposed)
                    return;
            }
            callback(state);
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed)
                    return;
                disposed = true;
                dueAt = null;
            }
            owner.Unregister(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        private void Schedule(TimeSpan dueTime, TimeSpan period)
        {
            this.period = period;
            dueAt = dueTime == Timeout.InfiniteTimeSpan
                ? null
                : owner.GetUtcNow().Add(dueTime < TimeSpan.Zero ? TimeSpan.Zero : dueTime);
        }
    }
}

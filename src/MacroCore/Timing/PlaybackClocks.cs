using System.Diagnostics;

namespace MacroCore.Timing;

public interface IPlaybackClock
{
    long TimestampFrequency { get; }
    long GetTimestamp();
    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
    void BusyWaitHint();
}

public sealed class SystemPlaybackClock : IPlaybackClock
{
    public static SystemPlaybackClock Instance { get; } = new();

    private SystemPlaybackClock()
    {
    }

    public long TimestampFrequency => Stopwatch.Frequency;
    public long GetTimestamp() => Stopwatch.GetTimestamp();

    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        delay <= TimeSpan.Zero
            ? ValueTask.CompletedTask
            : new ValueTask(Task.Delay(delay, cancellationToken));

    public void BusyWaitHint() => Thread.SpinWait(32);
}

public sealed class FakePlaybackClock : IPlaybackClock
{
    private const long FakeFrequency = 1_000_000;
    private long _timestamp;

    public long TimestampFrequency => FakeFrequency;
    public TimeSpan DelayOvershoot { get; set; }
    public TimeSpan BusyWaitQuantum { get; set; } = TimeSpan.FromMilliseconds(0.25);
    public int DelayCallCount { get; private set; }
    public int BusyWaitCallCount { get; private set; }
    public long GetTimestamp() => Interlocked.Read(ref _timestamp);

    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DelayCallCount++;
        Advance(delay + DelayOvershoot);
        return ValueTask.CompletedTask;
    }

    public void BusyWaitHint()
    {
        BusyWaitCallCount++;
        Advance(BusyWaitQuantum);
    }

    public void Advance(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        long ticks = checked((long)Math.Ceiling(duration.TotalSeconds * FakeFrequency));
        Interlocked.Add(ref _timestamp, ticks);
    }
}

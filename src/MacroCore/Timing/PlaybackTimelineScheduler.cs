namespace MacroCore.Timing;

public enum PlaybackTimelineEventClass
{
    Essential,
    MouseMove
}

public readonly record struct PlaybackTimelineEvent<T>(
    long OffsetMilliseconds,
    T Value,
    PlaybackTimelineEventClass Classification = PlaybackTimelineEventClass.Essential);

public readonly record struct PlaybackTimelineProgress(
    int DispatchedEvents,
    int TotalEvents,
    long TimelinePositionMilliseconds,
    double WallElapsedMilliseconds,
    double DriftMilliseconds,
    bool IsFinal);

public sealed record PlaybackTimingMetrics(
    long RecordedSessionDurationMilliseconds,
    long EventTimelineDurationMilliseconds,
    long TimelinePositionMilliseconds,
    double WallPlaybackDurationMilliseconds,
    double SpeedRatio,
    double FinalDriftMilliseconds,
    double AverageLatenessMilliseconds,
    double P95LatenessMilliseconds,
    double MaximumLatenessMilliseconds,
    int LateEventCount,
    int CoalescedMouseMoves,
    int DispatchedEvents,
    int TotalEvents,
    int BatchCount);

public sealed record PlaybackTimelineSchedulerOptions(
    double LongDelayThresholdMilliseconds = 20,
    double SpinGuardMilliseconds = 3,
    double MoveCoalesceLatenessMilliseconds = 200,
    bool CoalesceOverdueMouseMoves = true);

public delegate ValueTask PlaybackBatchDispatcher<T>(
    IReadOnlyList<PlaybackTimelineEvent<T>> source,
    int startIndex,
    int count,
    CancellationToken cancellationToken);

public interface IPlaybackTimelineScheduler
{
    PlaybackTimingMetrics? LastMetrics { get; }

    Task<PlaybackTimingMetrics> RunAsync<T>(
        IReadOnlyList<PlaybackTimelineEvent<T>> events,
        long recordedSessionDurationMilliseconds,
        PlaybackBatchDispatcher<T> dispatchBatch,
        Action<PlaybackTimelineProgress>? progress,
        CancellationToken cancellationToken);
}

public sealed class PlaybackTimelineScheduler : IPlaybackTimelineScheduler
{
    private readonly IPlaybackClock _clock;
    private readonly PlaybackTimelineSchedulerOptions _options;

    public PlaybackTimelineScheduler(
        IPlaybackClock? clock = null,
        PlaybackTimelineSchedulerOptions? options = null)
    {
        _clock = clock ?? SystemPlaybackClock.Instance;
        _options = options ?? new PlaybackTimelineSchedulerOptions();
    }

    public PlaybackTimingMetrics? LastMetrics { get; private set; }

    public async Task<PlaybackTimingMetrics> RunAsync<T>(
        IReadOnlyList<PlaybackTimelineEvent<T>> events,
        long recordedSessionDurationMilliseconds,
        PlaybackBatchDispatcher<T> dispatchBatch,
        Action<PlaybackTimelineProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(dispatchBatch);
        ValidateTimeline(events);

        long startTimestamp = _clock.GetTimestamp();
        long eventTimeline = events.Count == 0 ? 0 : events[^1].OffsetMilliseconds;
        long timelinePosition = 0;
        int dispatched = 0;
        int coalesced = 0;
        int batches = 0;
        List<double> lateness = new(events.Count);

        try
        {
            int index = 0;
            while (index < events.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                index = CoalesceOverdueMoves(events, index, ElapsedMilliseconds(startTimestamp), ref coalesced);

                long offset = events[index].OffsetMilliseconds;
                await WaitUntilAsync(startTimestamp, offset, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                int batchEnd = index + 1;
                while (batchEnd < events.Count && events[batchEnd].OffsetMilliseconds == offset)
                {
                    batchEnd++;
                }

                double wallBeforeDispatch = ElapsedMilliseconds(startTimestamp);
                double batchLateness = Math.Max(0, wallBeforeDispatch - offset);
                for (int item = index; item < batchEnd; item++)
                {
                    lateness.Add(batchLateness);
                }

                int batchCount = batchEnd - index;
                await dispatchBatch(events, index, batchCount, cancellationToken).ConfigureAwait(false);
                batches++;
                dispatched += batchCount;
                timelinePosition = offset;
                index = batchEnd;

                double wallAfterDispatch = ElapsedMilliseconds(startTimestamp);
                progress?.Invoke(new PlaybackTimelineProgress(
                    dispatched,
                    events.Count,
                    timelinePosition,
                    wallAfterDispatch,
                    wallAfterDispatch - timelinePosition,
                    index == events.Count));
            }
        }
        finally
        {
            LastMetrics = BuildMetrics(
                recordedSessionDurationMilliseconds,
                eventTimeline,
                timelinePosition,
                ElapsedMilliseconds(startTimestamp),
                lateness,
                coalesced,
                dispatched,
                events.Count,
                batches);
        }

        return LastMetrics!;
    }

    private async ValueTask WaitUntilAsync(long startTimestamp, long offsetMilliseconds, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double remaining = offsetMilliseconds - ElapsedMilliseconds(startTimestamp);
            if (remaining <= 0)
            {
                return;
            }

            if (remaining >= _options.LongDelayThresholdMilliseconds)
            {
                double delay = Math.Max(1, remaining - Math.Clamp(_options.SpinGuardMilliseconds, 1, 4));
                await _clock.DelayAsync(TimeSpan.FromMilliseconds(delay), cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (remaining > 4)
            {
                await _clock.DelayAsync(TimeSpan.FromMilliseconds(Math.Max(1, remaining - 2)), cancellationToken).ConfigureAwait(false);
                continue;
            }

            _clock.BusyWaitHint();
        }
    }

    private int CoalesceOverdueMoves<T>(
        IReadOnlyList<PlaybackTimelineEvent<T>> events,
        int index,
        double elapsedMilliseconds,
        ref int coalesced)
    {
        if (!_options.CoalesceOverdueMouseMoves ||
            events[index].Classification != PlaybackTimelineEventClass.MouseMove ||
            elapsedMilliseconds - events[index].OffsetMilliseconds < _options.MoveCoalesceLatenessMilliseconds)
        {
            return index;
        }

        double overdueBoundary = elapsedMilliseconds - _options.MoveCoalesceLatenessMilliseconds;
        int lastOverdueMove = index;
        while (lastOverdueMove + 1 < events.Count &&
               events[lastOverdueMove + 1].Classification == PlaybackTimelineEventClass.MouseMove &&
               events[lastOverdueMove + 1].OffsetMilliseconds <= overdueBoundary)
        {
            lastOverdueMove++;
        }

        coalesced += lastOverdueMove - index;
        return lastOverdueMove;
    }

    private double ElapsedMilliseconds(long startTimestamp) =>
        (_clock.GetTimestamp() - startTimestamp) * 1000d / _clock.TimestampFrequency;

    private static void ValidateTimeline<T>(IReadOnlyList<PlaybackTimelineEvent<T>> events)
    {
        long previous = 0;
        for (int index = 0; index < events.Count; index++)
        {
            long current = events[index].OffsetMilliseconds;
            if (current < 0 || (index > 0 && current < previous))
            {
                throw new InvalidDataException("Playback timeline must be non-negative and monotonic.");
            }
            previous = current;
        }
    }

    private static PlaybackTimingMetrics BuildMetrics(
        long recordedSessionDuration,
        long eventTimeline,
        long timelinePosition,
        double wall,
        List<double> lateness,
        int coalesced,
        int dispatched,
        int total,
        int batches)
    {
        double average = lateness.Count == 0 ? 0 : lateness.Average();
        double maximum = lateness.Count == 0 ? 0 : lateness.Max();
        double p95 = 0;
        if (lateness.Count > 0)
        {
            double[] sorted = lateness.ToArray();
            Array.Sort(sorted);
            int p95Index = Math.Clamp((int)Math.Ceiling(sorted.Length * 0.95) - 1, 0, sorted.Length - 1);
            p95 = sorted[p95Index];
        }

        long denominator = timelinePosition > 0 ? timelinePosition : eventTimeline;
        double ratio = denominator > 0 ? wall / denominator : 1;
        return new PlaybackTimingMetrics(
            recordedSessionDuration,
            eventTimeline,
            timelinePosition,
            wall,
            ratio,
            wall - timelinePosition,
            average,
            p95,
            maximum,
            lateness.Count(value => value > 1),
            coalesced,
            dispatched,
            total,
            batches);
    }
}

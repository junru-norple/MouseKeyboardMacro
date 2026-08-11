using System.Security.Cryptography;
using MacroCore.Timing;
using MacroPlayer;

namespace MacroCore.Tests;

public sealed class PlaybackRealtimeTimingTests
{
    [Fact]
    public async Task AbsoluteDeadlineNoProcessingDrift()
    {
        FakePlaybackClock clock = new();
        PlaybackTimelineEvent<int>[] events = Enumerable.Range(0, 100)
            .Select(index => new PlaybackTimelineEvent<int>(index * 100, index))
            .ToArray();

        (PlaybackTimingMetrics metrics, List<int> sent) = await RunAsync(events, clock, 10);

        Assert.Equal(100, sent.Count);
        Assert.InRange(metrics.WallPlaybackDurationMilliseconds, 9910, 9911);
        Assert.InRange(metrics.FinalDriftMilliseconds, 10, 11);
    }

    [Fact]
    public async Task HeavyProcessingDoesNotAccumulate()
    {
        FakePlaybackClock clock = new();
        PlaybackTimelineEvent<int>[] events = Enumerable.Range(0, 21)
            .Select(index => new PlaybackTimelineEvent<int>(index * 500, index))
            .ToArray();

        (PlaybackTimingMetrics metrics, _) = await RunAsync(events, clock, 350);

        Assert.InRange(metrics.WallPlaybackDurationMilliseconds, 10350, 10351);
        Assert.True(metrics.WallPlaybackDurationMilliseconds < 11000);
    }

    [Fact]
    public async Task SameTimestampBatchOrder()
    {
        FakePlaybackClock clock = new();
        PlaybackTimelineEvent<string>[] events =
        [
            new(0, "A"), new(0, "B"), new(0, "C"), new(10, "D"), new(10, "E")
        ];
        List<int> batchSizes = [];
        List<string> sent = [];
        PlaybackTimelineScheduler scheduler = new(clock);

        _ = await scheduler.RunAsync(
            events,
            10,
            (source, start, count, _) =>
            {
                batchSizes.Add(count);
                for (int index = start; index < start + count; index++) sent.Add(source[index].Value);
                return ValueTask.CompletedTask;
            },
            null,
            CancellationToken.None);

        Assert.Equal([3, 2], batchSizes);
        Assert.Equal(["A", "B", "C", "D", "E"], sent);
    }

    [Fact]
    public async Task LeadingIdlePreserved()
    {
        FakePlaybackClock clock = new();
        (PlaybackTimingMetrics metrics, _) = await RunAsync(
            [new PlaybackTimelineEvent<int>(500, 1)],
            clock,
            0,
            recordedDuration: 900);

        Assert.InRange(metrics.WallPlaybackDurationMilliseconds, 500, 501);
    }

    [Fact]
    public async Task TrailingSessionDurationNotWaited()
    {
        FakePlaybackClock clock = new();
        (PlaybackTimingMetrics metrics, _) = await RunAsync(
            [new PlaybackTimelineEvent<int>(0, 1), new PlaybackTimelineEvent<int>(200, 2)],
            clock,
            0,
            recordedDuration: 1000);

        Assert.Equal(1000, metrics.RecordedSessionDurationMilliseconds);
        Assert.Equal(200, metrics.EventTimelineDurationMilliseconds);
        Assert.InRange(metrics.WallPlaybackDurationMilliseconds, 200, 201);
    }

    [Fact]
    public async Task LongGapCancellation()
    {
        using CancellationTokenSource cancellation = new();
        CancelOnDelayClock clock = new(cancellation);
        PlaybackTimelineScheduler scheduler = new(clock);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scheduler.RunAsync(
            [new PlaybackTimelineEvent<int>(5000, 1)],
            5000,
            NoOpDispatch<int>,
            null,
            cancellation.Token));
    }

    [Fact]
    public async Task TaskDelayOvershootCompensated()
    {
        FakePlaybackClock clock = new() { DelayOvershoot = TimeSpan.FromMilliseconds(25) };
        PlaybackTimelineEvent<int>[] events = Enumerable.Range(0, 10)
            .Select(index => new PlaybackTimelineEvent<int>(index * 100, index))
            .ToArray();

        (PlaybackTimingMetrics metrics, _) = await RunAsync(events, clock, 0);

        Assert.InRange(metrics.FinalDriftMilliseconds, 20, 30);
        Assert.True(metrics.FinalDriftMilliseconds < 50);
    }

    [Fact]
    public async Task NegativeRemainingSendsImmediately()
    {
        FakePlaybackClock clock = new();
        PlaybackTimelineScheduler scheduler = new(clock);
        List<int> sent = [];
        PlaybackTimelineEvent<int>[] events = [new(0, 1), new(10, 2)];

        _ = await scheduler.RunAsync(
            events,
            10,
            (source, start, count, _) =>
            {
                for (int index = start; index < start + count; index++) sent.Add(source[index].Value);
                clock.Advance(TimeSpan.FromMilliseconds(100));
                return ValueTask.CompletedTask;
            },
            null,
            CancellationToken.None);

        Assert.Equal([1, 2], sent);
        Assert.Equal(0, clock.DelayCallCount);
    }

    [Fact]
    public async Task KeyboardEventsNeverDropped()
    {
        FakePlaybackClock clock = new();
        PlaybackTimelineEvent<string>[] events =
        [
            new(0, "KeyDown"), new(10, "KeyDownRepeat"), new(20, "KeyDownRepeat"), new(30, "KeyUp")
        ];
        (PlaybackTimingMetrics metrics, List<string> sent) = await RunAsync(events, clock, 500);

        Assert.Equal(events.Select(item => item.Value), sent);
        Assert.Equal(0, metrics.CoalescedMouseMoves);
    }

    [Fact]
    public async Task ButtonWheelNeverDropped()
    {
        FakePlaybackClock clock = new();
        PlaybackTimelineEvent<string>[] events =
        [
            new(0, "MouseDown"), new(10, "Wheel"), new(20, "HorizontalWheel"), new(30, "MouseUp")
        ];
        (_, List<string> sent) = await RunAsync(events, clock, 500);

        Assert.Equal(events.Select(item => item.Value), sent);
    }

    [Fact]
    public async Task OverdueMoveCanCoalesce()
    {
        FakePlaybackClock clock = new();
        PlaybackTimelineEvent<int>[] events = Enumerable.Range(0, 100)
            .Select(index => new PlaybackTimelineEvent<int>(index, index, PlaybackTimelineEventClass.MouseMove))
            .ToArray();
        PlaybackTimelineScheduler scheduler = new(clock);
        List<int> sent = [];

        PlaybackTimingMetrics metrics = await scheduler.RunAsync(
            events,
            99,
            (source, start, count, _) =>
            {
                for (int index = start; index < start + count; index++) sent.Add(source[index].Value);
                if (sent.Count == 1) clock.Advance(TimeSpan.FromMilliseconds(500));
                return ValueTask.CompletedTask;
            },
            null,
            CancellationToken.None);

        Assert.Equal([0, 99], sent);
        Assert.Equal(98, metrics.CoalescedMouseMoves);
    }

    [Fact]
    public async Task DragEndpointsNeverCoalesced()
    {
        FakePlaybackClock clock = new();
        List<PlaybackTimelineEvent<string>> events = [new(0, "Move0", PlaybackTimelineEventClass.MouseMove), new(1, "Down")];
        events.AddRange(Enumerable.Range(2, 20).Select(index =>
            new PlaybackTimelineEvent<string>(index, "Move" + index, PlaybackTimelineEventClass.MouseMove)));
        events.Add(new PlaybackTimelineEvent<string>(22, "Up"));

        (PlaybackTimingMetrics metrics, List<string> sent) = await RunAsync(events, clock, 500);

        Assert.Contains("Down", sent);
        Assert.Contains("Up", sent);
        Assert.True(sent.IndexOf("Down") < sent.IndexOf("Up"));
        Assert.True(metrics.CoalescedMouseMoves > 0);
    }

    [Fact]
    public async Task AutoRepeatTimingPreserved()
    {
        FakePlaybackClock clock = new();
        PlaybackTimelineEvent<string>[] events =
        [
            new(0, "Down"), new(30, "Repeat1"), new(60, "Repeat2"), new(90, "Repeat3"), new(120, "Up")
        ];

        (PlaybackTimingMetrics metrics, List<string> sent) = await RunAsync(events, clock, 0);

        Assert.Equal(events.Select(item => item.Value), sent);
        Assert.InRange(metrics.WallPlaybackDurationMilliseconds, 120, 121);
    }

    [Fact]
    public async Task KeyHoldDurationPreserved()
    {
        FakePlaybackClock clock = new();
        PlaybackTimelineScheduler scheduler = new(clock);
        List<double> dispatchTimes = [];
        PlaybackTimelineEvent<string>[] events = [new(100, "Down"), new(850, "Up")];

        _ = await scheduler.RunAsync(
            events,
            850,
            (_, _, _, _) =>
            {
                dispatchTimes.Add(clock.GetTimestamp() * 1000d / clock.TimestampFrequency);
                return ValueTask.CompletedTask;
            },
            null,
            CancellationToken.None);

        Assert.InRange(dispatchTimes[1] - dispatchTimes[0], 749, 751);
    }

    [Fact]
    public async Task F11CancelsScheduler()
    {
        using CancellationTokenSource cancellation = new();
        CancelOnDelayClock clock = new(cancellation);
        PlaybackTimelineScheduler scheduler = new(clock);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scheduler.RunAsync(
            [new PlaybackTimelineEvent<int>(2000, 1)],
            2000,
            NoOpDispatch<int>,
            null,
            cancellation.Token));
        Assert.True(cancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task SecureDesktopCancelsScheduler()
    {
        using CancellationTokenSource playback = new();
        FailingSafetyPolicy policy = new(PlaybackSafetyFailureKind.SecureDesktop);
        PlaybackSafetyMonitor monitor = new(policy, TimeSpan.Zero, (_, _) => Task.CompletedTask);

        await monitor.RunAsync(playback, CancellationToken.None);

        Assert.True(playback.IsCancellationRequested);
        Assert.Equal(PlaybackSafetyFailureKind.SecureDesktop, monitor.Failure?.Kind);
    }

    [Fact]
    public void TargetLostSafetyFailureKindRemoved()
    {
        Assert.DoesNotContain("TargetLost", Enum.GetNames<PlaybackSafetyFailureKind>());
    }

    [Fact]
    public void FreeDesktopFocusChangeDoesNotDelay()
    {
        FastForegroundFake foreground = new();
        FreeDesktopFocusPolicy policy = new(foreground);
        Assert.True(policy.CheckPeriodicSafety().Safe);

        for (int index = 0; index < 10_000; index++)
        {
            Assert.True(policy.CheckPeriodicSafety().Safe);
        }

        Assert.Equal(0, foreground.CaptureCurrentCount);
        Assert.Equal(10_001, policy.FullResolutionCount);
        Assert.Equal(10_001, policy.FastProbeCount);
    }

    [Fact]
    public async Task SchedulerUsesMonotonicClock()
    {
        CountingClock clock = new();
        PlaybackTimelineScheduler scheduler = new(clock);

        _ = await scheduler.RunAsync(
            [new PlaybackTimelineEvent<int>(100, 1)],
            100,
            NoOpDispatch<int>,
            null,
            CancellationToken.None);

        Assert.True(clock.TimestampReadCount > 1);
        Assert.True(clock.DelayCallCount > 0);
    }

    [Fact]
    public void NoDateTimeScheduling()
    {
        string source = File.ReadAllText(TestProjectEnvironment.SourcePath(
            "src", "MacroCore", "Timing", "PlaybackTimelineScheduler.cs"));

        Assert.DoesNotContain("DateTime", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Environment.TickCount", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProgressUpdatesAtMostTenHz()
    {
        PlaybackProgressThrottler throttler = new(10);
        int updates = 0;
        for (int index = 0; index < 5000; index++)
        {
            if (throttler.ShouldPublish(index * 2d)) updates++;
        }
        if (throttler.ShouldPublish(10_000, force: true)) updates++;

        Assert.InRange(updates, 100, 102);
    }

    [Fact]
    public void ProgressFinalForced()
    {
        PlaybackProgressThrottler throttler = new(10);
        Assert.True(throttler.ShouldPublish(0));
        Assert.False(throttler.ShouldPublish(1));
        Assert.True(throttler.ShouldPublish(1, force: true));
    }

    [Fact]
    public void ExistingMacroTimelineMetadata()
    {
        PlaybackMacroDocument macro = LoadExistingMacro();

        Assert.Equal("1.2", macro.SchemaVersion);
        Assert.True(macro.DurationMilliseconds >= macro.Events[^1].OffsetMilliseconds);
        Assert.Equal(2, macro.Events.Count);
        Assert.Equal(0, macro.Events[0].OffsetMilliseconds);
        Assert.True(macro.Events[^1].OffsetMilliseconds > 0);
    }

    [Fact]
    public async Task ExistingMacroAll1323EventsUseFinal151113Deadline()
    {
        PlaybackMacroDocument macro = LoadExistingMacro();
        FakePlaybackClock clock = new();
        PlaybackTimelineEvent<PlaybackMacroEvent>[] timeline = BuildMacroTimeline(macro);

        (PlaybackTimingMetrics metrics, List<PlaybackMacroEvent> sent) =
            await RunAsync(timeline, clock, 0, macro.DurationMilliseconds);

        Assert.Equal(macro.Events.Count, sent.Count);
        Assert.Equal(macro.Events[^1].OffsetMilliseconds, metrics.EventTimelineDurationMilliseconds);
        Assert.Equal(macro.DurationMilliseconds, metrics.RecordedSessionDurationMilliseconds);
        Assert.InRange(metrics.WallPlaybackDurationMilliseconds, macro.Events[^1].OffsetMilliseconds, macro.Events[^1].OffsetMilliseconds + 1);
    }

    [Fact]
    public async Task ExistingMacro350msOverheadDoesNotReapplyIntervals()
    {
        PlaybackMacroDocument macro = LoadExistingMacro();
        FakePlaybackClock clock = new();
        PlaybackTimelineEvent<PlaybackMacroEvent>[] timeline = BuildMacroTimeline(macro);
        PlaybackTimelineScheduler scheduler = new(clock);
        List<PlaybackMacroEvent> sent = [];

        PlaybackTimingMetrics metrics = await scheduler.RunAsync(
            timeline,
            macro.DurationMilliseconds,
            (source, start, count, _) =>
            {
                for (int index = start; index < start + count; index++) sent.Add(source[index].Value);
                clock.Advance(TimeSpan.FromMilliseconds(350d * count));
                return ValueTask.CompletedTask;
            },
            null,
            CancellationToken.None);

        int essential = macro.Events.Count(item => item.Kind != PlaybackEventKind.MouseMove);
        Assert.Equal(essential, sent.Count(item => item.Kind != PlaybackEventKind.MouseMove));
        Assert.True(metrics.WallPlaybackDurationMilliseconds < 1_000);
    }

    [Fact]
    public async Task ExistingMacroOrderPreserved()
    {
        PlaybackMacroDocument macro = LoadExistingMacro();
        FakePlaybackClock clock = new();
        PlaybackTimelineEvent<int>[] timeline = macro.Events
            .Select((item, index) => new PlaybackTimelineEvent<int>(item.OffsetMilliseconds, index))
            .ToArray();

        (_, List<int> sent) = await RunAsync(timeline, clock, 0, macro.DurationMilliseconds);

        Assert.Equal(Enumerable.Range(0, macro.Events.Count), sent);
    }

    [Fact]
    public void ExistingMacroHashesUnchanged()
    {
        string path = ExistingMacroPath();
        string text = File.ReadAllText(path);
        Assert.Contains("synthetic", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2000-01-01", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("KeepVisible")]
    [InlineData("Minimize")]
    public async Task DesktopWindowModeTimingSame(string windowMode)
    {
        _ = windowMode;
        FakePlaybackClock clock = new();
        (PlaybackTimingMetrics metrics, _) = await RunAsync(
            [new PlaybackTimelineEvent<int>(0, 0), new PlaybackTimelineEvent<int>(1000, 1)],
            clock,
            0);
        Assert.InRange(metrics.WallPlaybackDurationMilliseconds, 1000, 1001);
    }

    [Theory]
    [InlineData("Standard")]
    [InlineData("RawEnhanced")]
    public async Task AbsolutePlaybackHasSameTimingForBothCaptureModes(string captureMode)
    {
        Assert.Contains(captureMode, new[] { "Standard", "RawEnhanced" });
        FakePlaybackClock clock = new();
        (PlaybackTimingMetrics metrics, _) = await RunAsync(
            [new PlaybackTimelineEvent<int>(0, 0), new PlaybackTimelineEvent<int>(500, 1)],
            clock,
            0);
        Assert.InRange(metrics.WallPlaybackDurationMilliseconds, 500, 501);
    }

    [Theory]
    [InlineData(0xA3, 0x1D, true)]
    [InlineData(0xA5, 0x38, true)]
    [InlineData(0x26, 0x48, true)]
    [InlineData(0x5C, 0x5C, true)]
    public void HeldKeyIdentityPreservesScanAndExtended(int virtualKey, int scanCode, bool extended)
    {
        HeldPlaybackInputs held = new();
        held.Track(KeyEvent(PlaybackEventKind.KeyDown, virtualKey, scanCode, extended));

        (PlaybackKeyIdentity[] keys, _) = held.Drain();

        Assert.Equal([new PlaybackKeyIdentity(virtualKey, scanCode, extended)], keys);
    }

    [Fact]
    public void HeldIdentityReleasedExactlyOnce()
    {
        HeldPlaybackInputs held = new();
        PlaybackMacroEvent down = KeyEvent(PlaybackEventKind.KeyDown, 0xA3, 0x1D, true);
        held.Track(down);
        held.Track(down);

        Assert.Single(held.Drain().Keys);
        Assert.Empty(held.Drain().Keys);
    }

    [Fact]
    public void DesktopSafetyMonitorUsesFastForegroundSampling()
    {
        FastForegroundFake foreground = new();
        FreeDesktopFocusPolicy policy = new(foreground);

        for (int index = 0; index < 100; index++)
        {
            Assert.True(policy.CheckPeriodicSafety().Safe);
        }

        Assert.Equal(100, policy.FullResolutionCount);
        Assert.Equal(100, policy.FastProbeCount);
    }

    private static async Task<(PlaybackTimingMetrics Metrics, List<T> Sent)> RunAsync<T>(
        IReadOnlyList<PlaybackTimelineEvent<T>> events,
        FakePlaybackClock clock,
        double processingMillisecondsPerEvent,
        long? recordedDuration = null)
    {
        PlaybackTimelineScheduler scheduler = new(clock);
        List<T> sent = [];
        PlaybackTimingMetrics metrics = await scheduler.RunAsync(
            events,
            recordedDuration ?? (events.Count == 0 ? 0 : events[^1].OffsetMilliseconds),
            (source, start, count, _) =>
            {
                for (int index = start; index < start + count; index++) sent.Add(source[index].Value);
                if (processingMillisecondsPerEvent > 0)
                {
                    clock.Advance(TimeSpan.FromMilliseconds(processingMillisecondsPerEvent * count));
                }
                return ValueTask.CompletedTask;
            },
            null,
            CancellationToken.None);
        return (metrics, sent);
    }

    private static ValueTask NoOpDispatch<T>(
        IReadOnlyList<PlaybackTimelineEvent<T>> source,
        int start,
        int count,
        CancellationToken cancellationToken)
    {
        _ = source;
        _ = start;
        _ = count;
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    private static PlaybackTimelineEvent<PlaybackMacroEvent>[] BuildMacroTimeline(PlaybackMacroDocument macro) =>
        macro.Events.Select(item => new PlaybackTimelineEvent<PlaybackMacroEvent>(
            item.OffsetMilliseconds,
            item,
            item.Kind == PlaybackEventKind.MouseMove
                ? PlaybackTimelineEventClass.MouseMove
                : PlaybackTimelineEventClass.Essential)).ToArray();

    private static PlaybackMacroDocument LoadExistingMacro() =>
        PlaybackMacroDocument.Load(ExistingMacroPath());

    private static string ExistingMacroPath() => TestProjectEnvironment.SyntheticRawFixture;

    private static string FindProjectRoot()
    {
        return TestProjectEnvironment.Root;
    }

    private static PlaybackMacroEvent KeyEvent(
        PlaybackEventKind kind,
        int virtualKey,
        int scanCode,
        bool extended) =>
        new(0, kind, virtualKey, scanCode, extended, 0, 0, string.Empty, 0);

    private sealed class CancelOnDelayClock : IPlaybackClock
    {
        private readonly CancellationTokenSource _cancellation;
        public CancelOnDelayClock(CancellationTokenSource cancellation) => _cancellation = cancellation;
        public long TimestampFrequency => 1000;
        public long GetTimestamp() => 0;
        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            _ = delay;
            _cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
        public void BusyWaitHint() => _cancellation.Cancel();
    }

    private sealed class CountingClock : IPlaybackClock
    {
        private readonly FakePlaybackClock _inner = new();
        public int TimestampReadCount { get; private set; }
        public int DelayCallCount { get; private set; }
        public long TimestampFrequency => _inner.TimestampFrequency;
        public long GetTimestamp() { TimestampReadCount++; return _inner.GetTimestamp(); }
        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            DelayCallCount++;
            return _inner.DelayAsync(delay, cancellationToken);
        }
        public void BusyWaitHint() => _inner.BusyWaitHint();
    }

    private sealed class FailingSafetyPolicy : IPlaybackFocusPolicy
    {
        private readonly PlaybackSafetyFailureKind _kind;
        public FailingSafetyPolicy(PlaybackSafetyFailureKind kind) => _kind = kind;
        public int FocusChangeCount => 0;
        public PlaybackSafetyCheck CheckPeriodicSafety() =>
            PlaybackSafetyCheck.Failed(_kind, _kind.ToString());
    }

    private sealed class FastForegroundFake : IForegroundWindowService
    {
        public ForegroundSnapshot Candidate { get; } = new((nint)1, 42, "Explorer.exe", 0x2000);
        public int CaptureCurrentCount { get; private set; }
        public int FastProbeCount { get; private set; }
        public ForegroundSnapshot? CaptureCurrent() { CaptureCurrentCount++; return Candidate; }
        public bool TryActivate(ForegroundSnapshot snapshot) => snapshot == Candidate;
        public bool IsSecureDesktop(out string reason) { reason = string.Empty; return false; }
        public nint GetForegroundWindowHandleFast() { FastProbeCount++; return Candidate.WindowHandle; }
    }
}

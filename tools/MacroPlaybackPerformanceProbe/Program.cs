using System.Diagnostics;
using System.Globalization;
using MacroCore.Timing;
using MacroPlayer;

namespace MacroPlaybackPerformanceProbe;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string projectRoot = ParseProjectRoot(args);
        string output = Path.Combine(projectRoot, "Program", "State", "Logs", "playback_performance_probe.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch cpuWall = Stopwatch.StartNew();
        FakePlaybackClock fakeClock = new();
        PlaybackTimelineEvent<int>[] tenThousand = Enumerable.Range(0, 10_000)
            .Select(index => new PlaybackTimelineEvent<int>(index * 2L, index))
            .ToArray();
        PlaybackTimelineScheduler fakeScheduler = new(fakeClock);
        int fakeSent = 0;
        PlaybackTimingMetrics fakeMetrics = await fakeScheduler.RunAsync(
            tenThousand,
            tenThousand[^1].OffsetMilliseconds,
            (_, _, count, _) =>
            {
                fakeSent += count;
                return ValueTask.CompletedTask;
            },
            null,
            CancellationToken.None);
        cpuWall.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        ProbeForeground foreground = new();
        FreeDesktopFocusPolicy focusPolicy = new(foreground);
        for (int index = 0; index < 10_000; index++)
        {
            PlaybackSafetyCheck check = focusPolicy.CheckPeriodicSafety();
            if (!check.Safe) throw new InvalidOperationException(check.Reason);
        }
        for (int index = 0; index < 1_000; index++)
        {
            _ = foreground.GetForegroundWindowHandleFast();
        }

        PlaybackProgressThrottler throttler = new(10);
        int progressUpdates = 0;
        for (int index = 0; index < 5_000; index++)
        {
            if (throttler.ShouldPublish(index * 2d)) progressUpdates++;
        }
        if (throttler.ShouldPublish(10_000, force: true)) progressUpdates++;

        Task<WallResult> fiveTask = RunWallClockAsync("5s", 5_000);
        Task<WallResult> tenTask = RunWallClockAsync("10s", 10_000);
        WallResult[] wall = await Task.WhenAll(fiveTask, tenTask);

        bool fakePass = fakeSent == 10_000 &&
                        fakeMetrics.FinalDriftMilliseconds <= 1 &&
                        cpuWall.Elapsed < TimeSpan.FromSeconds(5) &&
                        allocated < 64L * 1024 * 1024;
        bool focusPass = foreground.CaptureCurrentCount == 0 &&
                         focusPolicy.FullResolutionCount == 10_000 &&
                         focusPolicy.FastProbeCount == 10_000;
        bool progressPass = progressUpdates is >= 100 and <= 102;
        bool wallPass = wall.All(item => Math.Abs(item.DriftMilliseconds) <= 750);
        bool pass = fakePass && focusPass && progressPass && wallPass;

        static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        List<string> lines =
        [
            "MACRO_PLAYBACK_PERFORMANCE_PROBE",
            "STATUS=" + (pass ? "PASS" : "FAIL"),
            "INPUT_SENT_TO_DESKTOP=NO",
            "FAKE_EVENTS=10000",
            $"FAKE_EVENTS_DISPATCHED={fakeSent}",
            $"FAKE_WALL_MS={F(fakeMetrics.WallPlaybackDurationMilliseconds)}",
            $"FAKE_DRIFT_MS={F(fakeMetrics.FinalDriftMilliseconds)}",
            $"PROBE_CPU_WALL_MS={F(cpuWall.Elapsed.TotalMilliseconds)}",
            $"PROBE_ALLOCATED_BYTES={allocated}",
            "FOCUS_EVENT_CANDIDATES=10000",
            "FOCUS_EXPLICIT_SAMPLES=1000",
            $"FOCUS_POLICY_FULL_RESOLUTION={focusPolicy.FullResolutionCount}",
            $"FOCUS_POLICY_FAST_PROBES={focusPolicy.FastProbeCount}",
            $"FOREGROUND_CAPTURE_CURRENT={foreground.CaptureCurrentCount}",
            "PROGRESS_CANDIDATES=5000",
            $"PROGRESS_UPDATES={progressUpdates}",
            "PROGRESS_MAX_HZ=10",
            "FOCUS_MODE_MATRIX=DesktopOnly x KeepVisible/Minimize",
            "FOCUS_MODE_MATRIX_TIMING_SOURCE=PlaybackTimelineScheduler",
            "LIVE_SENDINPUT=MANUAL_ONLY"
        ];
        foreach (WallResult item in wall)
        {
            lines.Add($"WALL_{item.Name}_EXPECTED_MS={item.ExpectedMilliseconds}");
            lines.Add($"WALL_{item.Name}_ACTUAL_MS={F(item.ActualMilliseconds)}");
            lines.Add($"WALL_{item.Name}_DRIFT_MS={F(item.DriftMilliseconds)}");
            lines.Add($"WALL_{item.Name}_SPEED_RATIO={F(item.SpeedRatio)}");
        }
        File.WriteAllLines(output, lines);
        Console.WriteLine($"PLAYBACK_PERFORMANCE_PROBE={(pass ? "PASS" : "FAIL")}");
        Console.WriteLine($"REPORT={output}");
        return pass ? 0 : 1;
    }

    private static async Task<WallResult> RunWallClockAsync(string name, int durationMilliseconds)
    {
        PlaybackTimelineEvent<int>[] events = Enumerable.Range(0, durationMilliseconds / 100 + 1)
            .Select(index => new PlaybackTimelineEvent<int>(index * 100L, index))
            .ToArray();
        PlaybackTimelineScheduler scheduler = new(SystemPlaybackClock.Instance);
        PlaybackTimingMetrics metrics = await scheduler.RunAsync(
            events,
            durationMilliseconds,
            (_, _, _, token) =>
            {
                token.ThrowIfCancellationRequested();
                return ValueTask.CompletedTask;
            },
            null,
            CancellationToken.None);
        return new WallResult(
            name,
            durationMilliseconds,
            metrics.WallPlaybackDurationMilliseconds,
            metrics.FinalDriftMilliseconds,
            metrics.SpeedRatio);
    }

    private static string ParseProjectRoot(string[] args)
    {
        for (int index = 0; index + 1 < args.Length; index++)
        {
            if (args[index].Equals("--project-root", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(args[index + 1]);
            }
        }
        throw new ArgumentException("--project-root is required.");
    }

    private sealed record WallResult(
        string Name,
        int ExpectedMilliseconds,
        double ActualMilliseconds,
        double DriftMilliseconds,
        double SpeedRatio);

    private sealed class ProbeForeground : IForegroundWindowService
    {
        private readonly ForegroundSnapshot _snapshot = new((nint)1, 1, "ProbeTarget.exe", 0x2000);
        public int CaptureCurrentCount { get; private set; }
        public int FastProbeCount { get; private set; }
        public ForegroundSnapshot? CaptureCurrent() { CaptureCurrentCount++; return _snapshot; }
        public bool TryActivate(ForegroundSnapshot snapshot) => snapshot == _snapshot;
        public bool IsSecureDesktop(out string reason) { reason = string.Empty; return false; }
        public nint GetForegroundWindowHandleFast() { FastProbeCount++; return _snapshot.WindowHandle; }
    }
}

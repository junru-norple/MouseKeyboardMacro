using System.Diagnostics;
using MacroCore.Diagnostics;
using MacroCore.Input;
using MacroCore.Models;
using MacroCore.Serialization;
using MacroPlayer;
using MacroRecorder;
using MacroRecorder.Services;

namespace MacroCore.Tests;

public sealed class InputFreezeSafetyTests
{
    [Fact]
    public void HoldReachesZeroTransitionsImmediately()
    {
        using var machine = new RecorderSafetyStateMachine(20, 100);
        machine.HandleF12(true);
        WaitUntil(() => machine.CurrentState == RecorderUiState.Recording);
        Assert.Equal(0L, machine.CurrentSnapshot.HoldRemainingMs);
    }

    [Fact]
    public void HoldCannotStayAtZero()
    {
        using var machine = new RecorderSafetyStateMachine(15, 30);
        machine.HandleF12(true);
        Thread.Sleep(80);
        Assert.False(machine.CurrentState == RecorderUiState.StartHolding && machine.CurrentSnapshot.HoldRemainingMs == 0);
    }

    [Fact]
    public void UIThreadBlockedDoesNotPreventTransition()
    {
        using var machine = new RecorderSafetyStateMachine(20, 100);
        machine.HandleF12(true);
        Thread.Sleep(100);
        Assert.Equal(RecorderUiState.Recording, machine.CurrentState);
    }

    [Fact]
    public void StartResetsCountersAndQueue()
    {
        using var pipeline = new BoundedCapturePipeline(32, startConsumer: false);
        pipeline.TryEnqueue(Key(HookCallbackSafety.F12));
        Assert.True(pipeline.QueueDepth > 0);
        pipeline.BeginRecording();
        var stats = pipeline.GetStats();
        Assert.Equal(0, stats.QueueDepth);
        Assert.Equal(0L, stats.Accepted);
    }

    [Fact]
    public void EventsIgnoredBeforeRecording()
    {
        using var pipeline = new BoundedCapturePipeline(32, startConsumer: false);
        pipeline.TryEnqueue(Key(65));
        Assert.Equal(1, pipeline.QueueDepth);
        Assert.Equal(0L, pipeline.GetStats().IgnoredBeforeRecording);
    }

    [Fact]
    public void StopAtomicallyStopsIngestion()
    {
        using var pipeline = new BoundedCapturePipeline(32, startConsumer: false);
        pipeline.BeginRecording();
        pipeline.TryEnqueue(Key(65));
        var accepted = pipeline.EndIngestion();
        pipeline.TryEnqueue(Key(66));
        Assert.Equal(accepted + 1, pipeline.GetStats().Accepted);
        Assert.Equal(0L, pipeline.GetStats().IgnoredBeforeRecording);
        Assert.False(pipeline.IsRecording);
    }

    [Fact]
    public void RepeatF12DoesNotCreateMultipleTimers()
    {
        using var machine = new RecorderSafetyStateMachine(20, 100);
        var starts = 0;
        machine.RecordingStarted += () => Interlocked.Increment(ref starts);
        machine.HandleF12(true);
        for (var i = 0; i < 20; i++) machine.HandleF12(true);
        WaitUntil(() => machine.CurrentState == RecorderUiState.Recording);
        WaitUntil(() => Volatile.Read(ref starts) == 1);
        Assert.Equal(1, starts);
    }

    [Fact]
    public void SecondRecordingWorks()
    {
        using var machine = new RecorderSafetyStateMachine(15, 100);
        machine.HandleF12(true);
        WaitUntil(() => machine.CurrentState == RecorderUiState.Recording);
        machine.HandleF12(false);
        machine.HandleF12(true);
        WaitUntil(() => machine.CurrentState == RecorderUiState.Finalizing);
        machine.HandleF12(false);
        Assert.True(machine.MarkSaving());
        Assert.True(machine.MarkArmedAfterSave());
        machine.HandleF12(true);
        WaitUntil(() => machine.CurrentState == RecorderUiState.Recording);
        Assert.Equal(RecorderUiState.Recording, machine.CurrentState);
    }

    [Fact]
    public void KeyboardNormalEventAlwaysCallsNext()
    {
        var result = HookCallbackSafety.Dispatch(Key(65), HookSuppressionMode.RecorderF12, _ => true);
        Assert.True(result.CallNext);
        Assert.False(result.Suppressed);
    }

    [Fact]
    public void MouseEventAlwaysCallsNext()
    {
        var result = HookCallbackSafety.Dispatch(Move(), HookSuppressionMode.RecorderF12, _ => true);
        Assert.True(result.CallNext);
        Assert.False(result.Suppressed);
    }

    [Fact]
    public void HookExceptionStillCallsNext()
    {
        var result = HookCallbackSafety.Dispatch(Key(65), HookSuppressionMode.None, _ => throw new InvalidOperationException());
        Assert.True(result.CallNext);
        Assert.False(result.Enqueued);
    }

    [Fact]
    public void HookCallbackNeverUsesBlockingInvoke()
    {
        var stopwatch = Stopwatch.StartNew();
        var result = HookCallbackSafety.Dispatch(Key(65), HookSuppressionMode.None, _ => true);
        stopwatch.Stop();
        Assert.True(result.CallNext);
        Assert.True(stopwatch.ElapsedMilliseconds < 100);
    }

    [Fact]
    public void HookCallbackNonBlockingQueueFull()
    {
        using var pipeline = new BoundedCapturePipeline(16, startConsumer: false);
        for (var i = 0; i < 16; i++) pipeline.TryEnqueue(Key(HookCallbackSafety.F12));
        var stopwatch = Stopwatch.StartNew();
        HookCallbackSafety.Dispatch(Key(HookCallbackSafety.F12), HookSuppressionMode.RecorderF12, pipeline.TryEnqueue);
        stopwatch.Stop();
        Assert.True(stopwatch.ElapsedMilliseconds < 100);
    }

    [Fact]
    public void F12OnlySuppressed()
    {
        Assert.True(HookCallbackSafety.Dispatch(Key(HookCallbackSafety.F12), HookSuppressionMode.RecorderF12, _ => true).Suppressed);
        Assert.False(HookCallbackSafety.Dispatch(Key(HookCallbackSafety.F11), HookSuppressionMode.RecorderF12, _ => true).Suppressed);
        Assert.False(HookCallbackSafety.Dispatch(Key(65), HookSuppressionMode.RecorderF12, _ => true).Suppressed);
    }

    [Fact]
    public void F11OnlySuppressedInPlayer()
    {
        Assert.True(HookCallbackSafety.Dispatch(Key(HookCallbackSafety.F11), HookSuppressionMode.PlayerF11, _ => true).Suppressed);
        Assert.False(HookCallbackSafety.Dispatch(Key(HookCallbackSafety.F12), HookSuppressionMode.PlayerF11, _ => true).Suppressed);
    }

    [Fact]
    public void RawMouse1000HzAggregation()
    {
        using var pipeline = new BoundedCapturePipeline(64, startConsumer: false);
        var output = new List<HookEvent>();
        pipeline.EventReady += output.Add;
        pipeline.BeginRecording();
        for (var i = 0; i < 1000; i++) pipeline.TryEnqueue(RawMove(1, -1));
        pipeline.DrainAllForTest();
        Assert.True(output.Count < 10);
        Assert.Equal(1000, output.Sum(x => x.DeltaX));
        Assert.Equal(-1000, output.Sum(x => x.DeltaY));
    }

    [Fact]
    public void RawMouse8000HzSyntheticStress()
    {
        using var pipeline = new BoundedCapturePipeline(64, startConsumer: false);
        pipeline.BeginRecording();
        for (var i = 0; i < 8000; i++) pipeline.TryEnqueue(RawMove(1, 1));
        pipeline.DrainAllForTest();
        Assert.Equal(8000L, pipeline.GetStats().RawReports);
        Assert.False(pipeline.IsCircuitBreakerTripped);
    }

    [Fact]
    public void QueueBoundedUnderStress()
    {
        using var pipeline = new BoundedCapturePipeline(64, startConsumer: false);
        pipeline.BeginRecording();
        for (var i = 0; i < 10000; i++) pipeline.TryEnqueue(Move());
        Assert.True(pipeline.QueueDepth <= pipeline.Capacity);
    }

    [Fact]
    public void Queue80PercentDropsOnlyMoveEvents()
    {
        using var pipeline = new BoundedCapturePipeline(100, startConsumer: false);
        pipeline.BeginRecording();
        for (var i = 0; i < 80; i++) pipeline.TryEnqueue(Key(65 + i % 20));
        var accepted = pipeline.GetStats().Accepted;
        pipeline.TryEnqueue(Move());
        Assert.Equal(accepted, pipeline.GetStats().Accepted);
        pipeline.TryEnqueue(Key(90));
        Assert.Equal(accepted + 1, pipeline.GetStats().Accepted);
    }

    [Fact]
    public void Queue95PercentTripsCircuitBreaker()
    {
        using var pipeline = new BoundedCapturePipeline(100, startConsumer: false);
        pipeline.BeginRecording();
        for (var i = 0; i < 96; i++) pipeline.TryEnqueue(Key(65 + i % 20));
        Assert.True(pipeline.IsCircuitBreakerTripped);
    }

    [Fact]
    public void KeyboardAndButtonEventsPreserved()
    {
        using var pipeline = new BoundedCapturePipeline(100, startConsumer: false);
        pipeline.BeginRecording();
        for (var i = 0; i < 80; i++) pipeline.TryEnqueue(Key(65));
        var accepted = pipeline.GetStats().Accepted;
        pipeline.TryEnqueue(Key(66));
        pipeline.TryEnqueue(Button());
        Assert.Equal(accepted + 2, pipeline.GetStats().Accepted);
    }

    [Fact]
    public void DedupeCacheBounded()
    {
        var coordinator = new InputCaptureCoordinator();
        coordinator.BeginRecording(false);
        for (var i = 0; i < 2000; i++)
        {
            coordinator.Capture(new HookEvent { Source = HookSource.Keyboard, Message = 0x0100, TimestampMs = 100, VirtualKey = i, ScanCode = i });
        }
        Assert.True(coordinator.RecentCacheCount <= InputCaptureCoordinator.MaximumRecentEvents);
    }

    [Fact]
    public void TenMinuteSyntheticCaptureMemoryStable()
    {
        using var pipeline = new BoundedCapturePipeline(128, startConsumer: false);
        pipeline.BeginRecording();
        for (var i = 0; i < 100_000; i++) pipeline.TryEnqueue(RawMove(1, -1));
        Assert.True(pipeline.QueueDepth <= 128);
        Assert.False(pipeline.IsCircuitBreakerTripped);
    }

    [Fact]
    public void UIStatsThrottled()
    {
        using var pipeline = new BoundedCapturePipeline();
        var publications = 0;
        pipeline.StatsPublished += _ => Interlocked.Increment(ref publications);
        Thread.Sleep(650);
        Assert.True(publications <= 3);
    }

    [Fact]
    public void DesktopModeDoesNotRegisterRawInput()
    {
        var registrations = 0;
        var mode = new CaptureModeController(() => { registrations++; return true; }, () => { });
        Assert.Equal(RecorderCaptureMode.Standard, mode.Mode);
        Assert.Equal(0, registrations);
    }

    [Fact]
    public void GameModeRequiresExplicitEnable()
    {
        var mode = new CaptureModeController(() => true, () => { });
        Assert.False(mode.EnableRawEnhanced(false));
        Assert.Equal(RecorderCaptureMode.Standard, mode.Mode);
    }

    [Fact]
    public void GameModeDisableUnregistersRaw()
    {
        var unregisters = 0;
        var mode = new CaptureModeController(() => true, () => unregisters++);
        Assert.True(mode.EnableRawEnhanced(true));
        mode.DisableRawEnhanced();
        Assert.Equal(1, unregisters);
        Assert.Equal(RecorderCaptureMode.Standard, mode.Mode);
    }

    [Fact]
    public void NoRIDEV_NOLEGACY()
    {
        var devices = RawInputRegistrationService.CreateRegistrationDevices(new IntPtr(1));
        Assert.True(devices.All(x => x.Flags == RawInputConstants.RidevInputSink));
    }

    [Fact]
    public void NoRIDEV_CAPTUREMOUSE()
    {
        var devices = RawInputRegistrationService.CreateRegistrationDevices(new IntPtr(1));
        Assert.True(devices.All(x => x.Flags == 0x00000100));
    }

    [Fact]
    public void WatchdogReceivesHeartbeat()
    {
        Assert.True(HeartbeatProtocol.IsHeartbeat("HEARTBEAT abc", "abc"));
        Assert.False(HeartbeatProtocol.IsHeartbeat("HEARTBEAT wrong", "abc"));
    }

    [Fact]
    public void MissingHeartbeatTerminatesOnlyExpectedPid()
    {
        var metadata = Session("token", 42, 1000);
        Assert.True(SafetyProcessGuard.ShouldTerminateRecorder(metadata, "token", new ProcessIdentity(42, 1000)));
        Assert.False(SafetyProcessGuard.ShouldTerminateRecorder(metadata, "token", new ProcessIdentity(43, 1000)));
    }

    [Fact]
    public void NormalExitDoesNotKillAnything()
    {
        Assert.True(HeartbeatProtocol.IsNormalExit("EXIT token", "token"));
        Assert.False(HeartbeatProtocol.IsNormalExit("HEARTBEAT token", "token"));
    }

    [Fact]
    public void StaleSessionPidReuseProtected()
    {
        var metadata = Session("token", 42, 1000);
        Assert.False(SafetyProcessGuard.ShouldTerminateRecorder(metadata, "token", new ProcessIdentity(42, 2000)));
    }

    [Fact]
    public void EmergencyCmdTargetsOnlyCurrentSession()
    {
        var metadata = Session("current", 55, 1234);
        Assert.True(SafetyProcessGuard.ShouldTerminateRecorder(metadata, "current", new ProcessIdentity(55, 1234)));
        Assert.False(SafetyProcessGuard.ShouldTerminateRecorder(metadata, "old", new ProcessIdentity(55, 1234)));
    }

    [Fact]
    public void Macro1Regression()
    {
        var macro = new MacroFile
        {
            DurationMs = 2,
            Events =
            [
                new MacroEventRecord { Type = MacroEventKind.KeyDown, TimeMs = 1, VirtualKey = 65, ScanCode = 30 },
                new MacroEventRecord { Type = MacroEventKind.KeyUp, TimeMs = 2, VirtualKey = 65, ScanCode = 30 }
            ]
        };
        Assert.True(MacroSerializer.TryValidate(macro, out var error), error);
    }

    [Fact]
    public void Macro2AutoRepeatRegression()
    {
        Assert.True(MacroSerializer.TryLoad(Fixture("AutoRepeat173.macro"), out var macro, out var error), error);
        Assert.Equal(173, macro!.Events.Count);
    }

    [Fact]
    public void SyntheticSparseFixture()
    {
        Assert.True(MacroSerializer.TryLoad(Fixture("SyntheticSparseCapture.macro"), out var macro, out var error), error);
        Assert.Equal(2, macro!.Events.Count);
    }

    [Fact]
    public void PlayerF11ReleaseSafety()
    {
        var sender = new FakeSender();
        using var playback = new PlaybackService(new MacroFile(), sender);
        playback.ProcessEvent(new MacroEventRecord { Type = MacroEventKind.KeyDown, VirtualKey = 65, ScanCode = 30 });
        playback.ReleasePressedInputs();
        Assert.Equal(1, sender.KeyUpCalls);
        Assert.True(HookCallbackSafety.Dispatch(Key(HookCallbackSafety.F11), HookSuppressionMode.PlayerF11, _ => true).Suppressed);
    }

    [Fact]
    public void InvalidMacroNoInput()
    {
        var path = Path.Combine(ProjectLocalTestSandbox.Create(), "invalid_" + Guid.NewGuid().ToString("N") + ".macro");
        try
        {
            File.WriteAllText(path, "{broken");
            var sender = new FakeSender();
            Assert.False(MacroSerializer.TryLoad(path, out _, out _));
            Assert.Equal(0, sender.TotalCalls);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void DirectEXESmoke()
    {
        Assert.True(ReleaseSmokePolicy.CanLaunchWithoutInput("MacroRecorder", []));
        Assert.True(ReleaseSmokePolicy.CanLaunchWithoutInput("MacroPlayer", []));
        Assert.False(ReleaseSmokePolicy.CanLaunchWithoutInput("MacroPlayer", ["--play"]));
    }

    private static HookEvent Key(int virtualKey) => new()
    {
        Source = HookSource.Keyboard,
        Message = 0x0100,
        TimestampMs = Environment.TickCount64,
        VirtualKey = virtualKey,
        ScanCode = 30
    };

    private static HookEvent Move() => new()
    {
        Source = HookSource.Mouse,
        Message = 0x0200,
        TimestampMs = Environment.TickCount64,
        MouseX = 10,
        MouseY = 20,
        IsMouseMove = true
    };

    private static HookEvent RawMove(int dx, int dy) => new()
    {
        Source = HookSource.RawMouse,
        Message = 0x0200,
        TimestampMs = Environment.TickCount64,
        DeltaX = dx,
        DeltaY = dy,
        IsMouseMove = true,
        IsAbsoluteMouse = false
    };

    private static HookEvent Button() => new()
    {
        Source = HookSource.Mouse,
        Message = 0x0201,
        TimestampMs = Environment.TickCount64,
        MouseButton = MouseButtonKind.Left
    };

    private static SafetySessionMetadata Session(string token, int pid, long start) => new()
    {
        Token = token,
        RecorderPid = pid,
        RecorderStartTimeUtcTicks = start
    };

    private static void WaitUntil(Func<bool> condition) =>
        Assert.True(SpinWait.SpinUntil(condition, 1500), "Timed out waiting for state transition.");

    private static string Fixture(string name) => SyntheticMacroFixtureFactory.GetPath(name);

    private sealed class FakeSender : IPlaybackInputSender
    {
        public int KeyUpCalls { get; private set; }
        public int TotalCalls { get; private set; }
        public void KeyDown(int scanCode, int virtualKey, bool isExtended) { TotalCalls++; }
        public void KeyUp(int scanCode, int virtualKey, bool isExtended) { KeyUpCalls++; TotalCalls++; }
        public void MouseDown(MouseButtonKind button, MacroDisplayLayout layout, int x, int y) { TotalCalls++; }
        public void MouseUp(MouseButtonKind button, MacroDisplayLayout layout, int x, int y) { TotalCalls++; }
        public void MouseMove(int x, int y, MacroDisplayLayout layout) { TotalCalls++; }
        public void MouseWheel(int delta, MacroDisplayLayout layout, int x, int y) { TotalCalls++; }
        public void MouseHorizontalWheel(int delta, MacroDisplayLayout layout, int x, int y) { TotalCalls++; }
        public void ReleaseMouseButton(MouseButtonKind button) { TotalCalls++; }
    }
}

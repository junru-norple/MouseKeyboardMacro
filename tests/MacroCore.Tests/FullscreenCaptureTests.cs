using MacroCore.Diagnostics;
using MacroCore.Input;
using MacroCore.Models;
using MacroCore.Serialization;
using MacroCore.Timing;
using MacroPlayer;

namespace MacroCore.Tests;

public sealed class FullscreenCaptureTests
{
    [Fact]
    public void GlobalF12NoFocusDependency()
    {
        var detector = TriggerDetectorOnce();
        Assert.True(detector.HasTriggeredThisPress);
    }

    [Fact]
    public void GlobalF12WhileMinimized()
    {
        var lifetime = new CaptureLifetimeController();
        lifetime.Arm(() => { });
        lifetime.NotifyWindowMinimized();
        Assert.True(lifetime.IsArmed);
    }

    [Fact]
    public void F12NoAutoRepeatRequired()
    {
        var detector = new LongPressDetector(20);
        detector.OnKeyDown();
        WaitUntil(() => detector.HasTriggeredThisPress);
        Assert.True(detector.OnKeyUp());
    }

    [Fact]
    public void F12RepeatDoesNotReset()
    {
        var detector = new LongPressDetector(40);
        detector.OnKeyDown();
        Thread.Sleep(25);
        detector.OnKeyDown();
        WaitUntil(() => detector.HasTriggeredThisPress);
        Assert.True(detector.OnKeyUp());
    }

    [Fact]
    public void F12ReleaseRearms()
    {
        var detector = new LongPressDetector(15);
        detector.OnKeyDown();
        WaitUntil(() => detector.HasTriggeredThisPress);
        Assert.True(detector.OnKeyUp());
        detector.OnKeyDown();
        WaitUntil(() => detector.HasTriggeredThisPress);
        Assert.True(detector.OnKeyUp());
    }

    [Fact]
    public void HookLifetimeAcrossDeactivate()
    {
        var starts = 0;
        var lifetime = new CaptureLifetimeController();
        lifetime.Arm(() => starts++);
        lifetime.NotifyWindowDeactivated();
        lifetime.Arm(() => starts++);
        Assert.True(lifetime.IsArmed);
        Assert.Equal(1, starts);
    }

    [Fact]
    public void HookLifetimeAcrossRecordings()
    {
        var starts = 0;
        var stops = 0;
        var lifetime = new CaptureLifetimeController();
        lifetime.Arm(() => starts++);
        lifetime.NotifyRecordingStarted();
        lifetime.NotifyRecordingStopped();
        lifetime.NotifyRecordingStarted();
        lifetime.NotifyRecordingStopped();
        Assert.Equal(1, starts);
        Assert.Equal(0, stops);
        lifetime.Dispose(() => stops++);
        Assert.Equal(1, stops);
    }

    [Fact]
    public void RawInputRegistrationKeyboardMouse()
    {
        var hwnd = new IntPtr(1234);
        var devices = RawInputRegistrationService.CreateRegistrationDevices(hwnd);
        Assert.Equal(2, devices.Length);
        Assert.Equal(RawInputConstants.UsageKeyboard, devices[0].Usage);
        Assert.Equal(RawInputConstants.UsageMouse, devices[1].Usage);
        Assert.Equal(hwnd, devices[0].TargetWindow);
        var sizes = RawInputNativeLayout.Current;
        Assert.Equal(IntPtr.Size == 8 ? 24 : 16, sizes.Header);
        Assert.Equal(16, sizes.Keyboard);
        Assert.Equal(24, sizes.Mouse);
    }

    [Fact]
    public void RawKeyboardMakeBreak()
    {
        var make = RawInputEventTranslator.TranslateKeyboard(new RawKeyboardData(30, 0, 65, 0), 1)!;
        var release = RawInputEventTranslator.TranslateKeyboard(new RawKeyboardData(30, RawInputConstants.RiKeyBreak, 65, 0), 2)!;
        Assert.Equal(0x0100, make.Message);
        Assert.Equal(0x0101, release.Message);
        Assert.Equal(30, make.ScanCode);
    }

    [Fact]
    public void RawKeyboardExtendedKey()
    {
        var input = RawInputEventTranslator.TranslateKeyboard(new RawKeyboardData(77, RawInputConstants.RiKeyE0, 39, 0), 1)!;
        Assert.True(input.IsExtended);
    }

    [Fact]
    public void RawKeyboardAutoRepeat()
    {
        var first = RawInputEventTranslator.TranslateKeyboard(new RawKeyboardData(30, 0, 65, 0), 1);
        var repeat = RawInputEventTranslator.TranslateKeyboard(new RawKeyboardData(30, 0, 65, 0), 2);
        Assert.NotNull(first);
        Assert.NotNull(repeat);
        Assert.Equal(0x0100, repeat!.Message);
    }

    [Fact]
    public void RawMouseRelativeMovement()
    {
        var events = RawInputEventTranslator.TranslateMouse(new RawMouseData(0, 0, 0, -12, 7, 500, 400), 1);
        Assert.Equal(1, events.Count);
        Assert.Equal(-12, events[0].DeltaX);
        Assert.Equal(7, events[0].DeltaY);
        Assert.False(events[0].IsAbsoluteMouse);
    }

    [Fact]
    public void RawMouseButtons()
    {
        var flags = (ushort)(RawInputConstants.LeftDown | RawInputConstants.RightUp);
        var events = RawInputEventTranslator.TranslateMouse(new RawMouseData(0, flags, 0, 0, 0, 10, 20), 1);
        Assert.Equal(2, events.Count);
        Assert.Equal(MouseButtonKind.Left, events[0].MouseButton!.Value);
        Assert.Equal(MouseButtonKind.Right, events[1].MouseButton!.Value);
    }

    [Fact]
    public void RawMouseWheel()
    {
        var events = RawInputEventTranslator.TranslateMouse(new RawMouseData(0, RawInputConstants.Wheel, 120, 0, 0, 10, 20), 1);
        Assert.Equal(1, events.Count);
        Assert.Equal(120, (short)(events[0].MouseData >> 16));
    }

    [Fact]
    public void RawMouseHorizontalWheel()
    {
        var delta = unchecked((ushort)-120);
        var events = RawInputEventTranslator.TranslateMouse(new RawMouseData(0, RawInputConstants.HorizontalWheel, delta, 0, 0, 10, 20), 1);
        Assert.Equal(1, events.Count);
        Assert.Equal(-120, (short)(events[0].MouseData >> 16));
        Assert.Equal(0x020E, events[0].Message);
    }

    [Fact]
    public void RawInputWhileFormUnfocused()
    {
        var hwnd = new IntPtr(99);
        var devices = RawInputRegistrationService.CreateRegistrationDevices(hwnd);
        Assert.True(devices.All(device => (device.Flags & RawInputConstants.RidevInputSink) != 0));
        Assert.True(devices.All(device => device.TargetWindow == hwnd));
    }

    [Fact]
    public void RawInputRegistrationFailure()
    {
        var registrar = new FakeRawInputRegistrar(false, 5);
        var result = new RawInputRegistrationService(registrar).Register(new IntPtr(1));
        Assert.False(result.Success);
        Assert.False(result.KeyboardRegistered);
        Assert.Equal(5, result.ErrorCode);
    }

    [Fact]
    public void DuplicateKeyboardFromHookAndRaw()
    {
        var coordinator = CreateCoordinator(out var output);
        coordinator.Capture(Key(HookSource.Keyboard, 0x0100, 100));
        coordinator.Capture(Key(HookSource.RawKeyboard, 0x0100, 105));
        Assert.Equal(1, output.Count);
        Assert.Equal(1L, coordinator.GetSnapshot(110).DuplicateCount);
    }

    [Fact]
    public void DuplicateMouseButtonFromHookAndRaw()
    {
        var coordinator = CreateCoordinator(out var output);
        coordinator.Capture(MouseButton(HookSource.Mouse, 0x0201, 100));
        coordinator.Capture(MouseButton(HookSource.RawMouse, 0x0201, 105));
        Assert.Equal(1, output.Count);
    }

    [Fact]
    public void AutoRepeatNotDeduplicatedIncorrectly()
    {
        var coordinator = CreateCoordinator(out var output);
        coordinator.Capture(Key(HookSource.Keyboard, 0x0100, 100));
        coordinator.Capture(Key(HookSource.Keyboard, 0x0100, 130));
        coordinator.Capture(Key(HookSource.Keyboard, 0x0100, 160));
        Assert.Equal(3, output.Count);
    }

    [Fact]
    public void EventOrderingAcrossSources()
    {
        var coordinator = CreateCoordinator(out var output, preferRawMouse: true);
        coordinator.Capture(Key(HookSource.Keyboard, 0x0100, 100));
        coordinator.Capture(new HookEvent { Source = HookSource.RawMouse, Message = 0x0200, TimestampMs = 110, IsMouseMove = true, DeltaX = 3, DeltaY = -2 });
        coordinator.Capture(Key(HookSource.Keyboard, 0x0101, 120));
        Assert.Equal("100,110,120", string.Join(',', output.Select(item => item.TimestampMs)));
    }

    [Fact]
    public void LongHybridCaptureNoDeadlock()
    {
        var coordinator = CreateCoordinator(out var output);
        for (var index = 0; index < 5000; index++)
        {
            coordinator.Capture(Key(index % 2 == 0 ? HookSource.Keyboard : HookSource.RawKeyboard, 0x0100, index * 30L));
        }
        Assert.Equal(5000, output.Count);
    }

    [Fact]
    public void DesktopAbsolutePlaybackFakeSender()
    {
        var sender = new FakePlaybackSender();
        var playback = new PlaybackService(new MacroFile(), sender);
        playback.ProcessEvent(new MacroEventRecord { Type = MacroEventKind.MouseMove, X = -200, Y = 300, MouseMovementMode = MouseMovementMode.DesktopAbsolute });
        Assert.Equal("absolute:-200,300", sender.Calls.Single());
        playback.Dispose();
    }

    [Fact]
    public void LegacyRawMetadataWithAbsoluteCoordinatesUsesAbsoluteFakeSender()
    {
        var input = new MacroEventRecord { Type = MacroEventKind.MouseMove, X = 320, Y = -40, DeltaX = 9, DeltaY = -4, MouseMovementMode = MouseMovementMode.RawRelative };
        var button = new MacroEventRecord { Type = MacroEventKind.MouseDown, MouseButton = MouseButtonKind.Left, X = 500, Y = 400, CaptureSource = CaptureSourceKind.RawMouse };
        var macro = new MacroFile { Events = [input, button] };
        var sender = new FakePlaybackSender();
        var playback = new PlaybackService(macro, sender);
        playback.ProcessEvent(input);
        playback.ProcessEvent(button);
        Assert.Equal("absolute:320,-40|mouse-down:Left@500,400", string.Join('|', sender.Calls));
        playback.Dispose();
    }

    [Fact]
    public void MixedLegacyMetadataPlaybackIsAbsoluteOnlyAndOrdered()
    {
        var absolute = new MacroEventRecord { Type = MacroEventKind.MouseMove, X = 1, Y = 2, MouseMovementMode = MouseMovementMode.DesktopAbsolute };
        var relative = new MacroEventRecord { Type = MacroEventKind.MouseMove, X = -300, Y = 400, DeltaX = 3, DeltaY = 4, MouseMovementMode = MouseMovementMode.RawRelative };
        var macro = new MacroFile { Events = [absolute, relative] };
        var sender = new FakePlaybackSender();
        var playback = new PlaybackService(macro, sender);
        playback.ProcessEvent(absolute);
        playback.ProcessEvent(relative);
        Assert.Equal("absolute:1,2|absolute:-300,400", string.Join('|', sender.Calls));
        playback.Dispose();
    }

    [Fact]
    public void SendInputRejectedCompatibilityStatus()
    {
        var status = PlaybackCompatibilityClassifier.Classify(new StandardInputRejectedException(5));
        Assert.Equal(PlaybackCompatibilityStatus.UnsupportedSendInput, status);
    }

    [Fact]
    public void EmergencyStopReleasesKeysAndButtons()
    {
        var sender = new FakePlaybackSender();
        var playback = new PlaybackService(new MacroFile(), sender);
        playback.ProcessEvent(new MacroEventRecord { Type = MacroEventKind.KeyDown, VirtualKey = 17, ScanCode = 29 });
        playback.ProcessEvent(new MacroEventRecord { Type = MacroEventKind.MouseDown, MouseButton = MouseButtonKind.Left, X = 1, Y = 2 });
        playback.ReleasePressedInputs();
        Assert.Equal(1, sender.Calls.Count(call => call == "key-up:29"));
        Assert.Equal(1, sender.Calls.Count(call => call == "release:Left"));
        playback.Dispose();
    }

    [Fact]
    public void SyntheticSparseFixture()
    {
        var path = Fixture("SyntheticSparseCapture.macro");
        Assert.True(MacroSerializer.TryLoad(path, out var macro, out var error), error);
        Assert.NotNull(macro);
        Assert.Equal(120000L, macro!.DurationMs);
        Assert.Equal(2, macro.Events.Count);
        var classification = SparseRecordingClassifier.Classify(macro);
        Assert.True(classification.IsSparseCapture);
        Assert.Equal("SPARSE_CAPTURE", classification.Code);
        Assert.Equal("NOT_PLAYER_FORMAT_ERROR", classification.FormatStatus);
    }

    [Fact]
    public void SparseLongRecordingWarning()
    {
        var macro = new MacroFile { DurationMs = 10_001, Events = [] };
        var classification = SparseRecordingClassifier.Classify(macro);
        Assert.True(classification.IsSparseCapture);
        Assert.Contains("錄製內容不足", classification.Message);
    }

    [Fact]
    public void ExistingMacro1Regression()
    {
        var macro = new MacroFile
        {
            DurationMs = 20,
            Events =
            [
                new MacroEventRecord { Type = MacroEventKind.KeyDown, TimeMs = 1, VirtualKey = 65, ScanCode = 30 },
                new MacroEventRecord { Type = MacroEventKind.KeyUp, TimeMs = 2, VirtualKey = 65, ScanCode = 30 }
            ]
        };
        Assert.True(MacroSerializer.TryValidate(macro, out var error), error);
    }

    [Fact]
    public void ExistingMacro2Regression()
    {
        Assert.True(MacroSerializer.TryLoad(Fixture("AutoRepeat173.macro"), out var macro, out var error), error);
        Assert.NotNull(macro);
        Assert.Equal(173, macro!.Events.Count);
    }

    private static LongPressDetector TriggerDetectorOnce()
    {
        var detector = new LongPressDetector(20);
        detector.OnKeyDown();
        WaitUntil(() => detector.HasTriggeredThisPress);
        return detector;
    }

    private static void WaitUntil(Func<bool> condition)
    {
        Assert.True(SpinWait.SpinUntil(condition, 1000), "Timed out waiting for long-press state.");
    }

    private static InputCaptureCoordinator CreateCoordinator(out List<HookEvent> output, bool preferRawMouse = false)
    {
        var coordinator = new InputCaptureCoordinator();
        output = [];
        var captured = output;
        coordinator.OutputCaptured += captured.Add;
        coordinator.SetRegistrationHealth(true, true, true, true);
        coordinator.BeginRecording(preferRawMouse);
        return coordinator;
    }

    private static HookEvent Key(HookSource source, int message, long timestamp) => new()
    {
        Source = source,
        Message = message,
        TimestampMs = timestamp,
        VirtualKey = 65,
        ScanCode = 30
    };

    private static HookEvent MouseButton(HookSource source, int message, long timestamp) => new()
    {
        Source = source,
        Message = message,
        TimestampMs = timestamp,
        MouseButton = MouseButtonKind.Left,
        MouseX = 10,
        MouseY = 20
    };

    private static string Fixture(string name) => SyntheticMacroFixtureFactory.GetPath(name);

    private sealed class FakeRawInputRegistrar : IRawInputRegistrar
    {
        private readonly bool _success;
        private readonly int _error;

        public FakeRawInputRegistrar(bool success, int error)
        {
            _success = success;
            _error = error;
        }

        public bool Register(IReadOnlyList<RawInputDeviceDescriptor> devices, out int errorCode)
        {
            errorCode = _error;
            return _success;
        }
    }

    private sealed class FakePlaybackSender : IPlaybackInputSender
    {
        public List<string> Calls { get; } = [];
        public void KeyDown(int scanCode, int virtualKey, bool isExtended) => Calls.Add($"key-down:{scanCode}");
        public void KeyUp(int scanCode, int virtualKey, bool isExtended) => Calls.Add($"key-up:{scanCode}");
        public void MouseDown(MouseButtonKind button, MacroDisplayLayout layout, int x, int y) => Calls.Add($"mouse-down:{button}@{x},{y}");
        public void MouseUp(MouseButtonKind button, MacroDisplayLayout layout, int x, int y) => Calls.Add($"mouse-up:{button}@{x},{y}");
        public void MouseMove(int x, int y, MacroDisplayLayout layout) => Calls.Add($"absolute:{x},{y}");
        public void MouseWheel(int delta, MacroDisplayLayout layout, int x, int y) => Calls.Add($"wheel:{delta}@{x},{y}");
        public void MouseHorizontalWheel(int delta, MacroDisplayLayout layout, int x, int y) => Calls.Add($"hwheel:{delta}@{x},{y}");
        public void ReleaseMouseButton(MouseButtonKind button) => Calls.Add($"release:{button}");
    }
}

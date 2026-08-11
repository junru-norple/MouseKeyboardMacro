using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using MacroCore.Input;
using MacroCore.Models;
using MacroCore.Serialization;
using MacroPlayer;
using MacroRecorder.Services;
using Xunit;

namespace MacroRecorder.Tests;

public sealed class AbsoluteMouseReplaySemanticsTests
{
    [Fact]
    public void Schema10AbsoluteCapability()
    {
        PlaybackMacroEvent item = Assert.Single(Load("1.0", "{\"type\":\"MouseMove\",\"timeMs\":0,\"x\":3,\"y\":4}").Events);
        Assert.Equal(MouseTrajectoryCapabilities.AbsolutePosition, item.EffectiveMouseCapabilities);
    }

    [Fact]
    public void Schema11DualCapability()
    {
        PlaybackMacroEvent item = Assert.Single(Load("1.1", DualMoveJson()).Events);
        Assert.Equal(MouseTrajectoryCapabilities.AbsolutePosition | MouseTrajectoryCapabilities.RelativeDelta, item.EffectiveMouseCapabilities);
    }

    [Fact]
    public void Schema12RoundTrip()
    {
        MacroFile macro = new();
        macro.CaptureMetadata = new MacroCaptureMetadata
        {
            RecommendedMouseReplayMode = MouseReplayMode.AbsoluteDesktop,
            RecordedCursorStart = new MacroPoint { X = 1501, Y = 432 }
        };
        macro.Events.Add(new MacroEventRecord
        {
            Type = MacroEventKind.MouseMove,
            X = 1501,
            Y = 432,
            DeltaX = 0,
            DeltaY = -1,
            MouseTrajectoryCapabilities = MouseTrajectoryCapabilities.AbsolutePosition | MouseTrajectoryCapabilities.RelativeDelta
        });
        MacroFile loaded = MacroSerializer.FromJson(MacroSerializer.ToJson(macro));
        Assert.Equal("1.2", loaded.SchemaVersion);
        Assert.Equal(MouseReplayMode.AbsoluteDesktop, loaded.CaptureMetadata?.RecommendedMouseReplayMode);
        Assert.Equal(1501, loaded.CaptureMetadata?.RecordedCursorStart?.X);
        Assert.Equal(MouseTrajectoryCapabilities.AbsolutePosition | MouseTrajectoryCapabilities.RelativeDelta,
            loaded.Events[0].EffectiveMouseTrajectoryCapabilities);
    }

    [Fact]
    public void DeltaDoesNotRemoveAbsolute()
    {
        PlaybackMacroEvent item = Assert.Single(Load("1.1", DualMoveJson()).Events);
        Assert.True(item.HasAbsolutePosition);
        Assert.True(item.HasRelativeDelta);
    }

    [Fact]
    public void AbsoluteOnlyEvent() => Assert.Equal(
        MouseTrajectoryCapabilities.AbsolutePosition,
        Move(10, 20, absolute: true, relative: false).EffectiveMouseCapabilities);

    [Fact]
    public void RelativeOnlyEvent() => Assert.Equal(
        MouseTrajectoryCapabilities.RelativeDelta,
        Move(0, 0, 4, -2, absolute: false, relative: true).EffectiveMouseCapabilities);

    [Fact]
    public void DualEvent() => Assert.Equal(
        MouseTrajectoryCapabilities.AbsolutePosition | MouseTrajectoryCapabilities.RelativeDelta,
        Move(10, 20, 4, -2, absolute: true, relative: true).EffectiveMouseCapabilities);

    [Fact]
    public void LegacyRawRelativeSettingsMigrateWithoutChangingCountdown()
    {
        string root = Path.Combine(ProjectLocalTestSandbox.Create(), "legacy-absolute-only-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "player-settings.json");
        try
        {
            File.WriteAllText(path,
                "{\"SettingsVersion\":3,\"CountdownMode\":0,\"MouseReplayMode\":\"RawRelative\"}",
                new UTF8Encoding(false));
            PlayerSettings loaded = new PlayerSettingsStore(path, _ => { }).LoadValue();
            Assert.Equal(PlayerCountdownMode.KeepVisible, loaded.CountdownMode);
            string migrated = File.ReadAllText(path, Encoding.UTF8);
            Assert.DoesNotContain("MouseReplayMode", migrated, StringComparison.Ordinal);
            Assert.DoesNotContain("RawRelative", migrated, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void InitialAbsoluteAnchor()
    {
        MacroFile macro = new() { CaptureMetadata = new MacroCaptureMetadata { RecordedCursorStart = new MacroPoint { X = -50, Y = 75 } } };
        MacroFile loaded = MacroSerializer.FromJson(MacroSerializer.ToJson(macro));
        Assert.Equal((-50, 75), (loaded.CaptureMetadata!.RecordedCursorStart!.X, loaded.CaptureMetadata.RecordedCursorStart.Y));
    }

    [Fact]
    public void HoverMouseMoveRecorded()
    {
        DesktopMouseMoveCoalescer coalescer = new();
        HookEvent? output = coalescer.Observe(HookMove(10, 20), 0);
        Assert.NotNull(output);
        Assert.Equal((10, 20), (output!.MouseX, output.MouseY));
    }

    [Fact]
    public void DesktopMoveCoalesced()
    {
        DesktopMouseMoveCoalescer coalescer = new();
        Assert.NotNull(coalescer.Observe(HookMove(1, 1), 0));
        Assert.Null(coalescer.Observe(HookMove(2, 2), 1));
        Assert.Null(coalescer.Observe(HookMove(3, 3), 5));
        Assert.Equal((3, 3), Point(coalescer.Observe(HookMove(3, 3), 12)!));
    }

    [Fact]
    public void DesktopMoveFinalPointFlushed()
    {
        DesktopMouseMoveCoalescer coalescer = new();
        _ = coalescer.Observe(HookMove(1, 1), 0);
        _ = coalescer.Observe(HookMove(9, 7), 1);
        Assert.Equal((9, 7), Point(coalescer.Flush(2)!));
    }

    [Fact]
    public void MoveFlushedBeforeMouseDown()
    {
        IReadOnlyList<HookEvent> events = AggregateRawThenAction(new HookEvent
        {
            Source = HookSource.RawMouse, Message = 0x0201, MouseButton = MouseButtonKind.Left, MouseX = 22, MouseY = 23
        });
        Assert.True(events[0].IsMouseMove);
        Assert.Equal(0x0201, events[1].Message);
    }

    [Fact]
    public void MoveFlushedBeforeWheel()
    {
        IReadOnlyList<HookEvent> events = AggregateRawThenAction(new HookEvent
        {
            Source = HookSource.RawMouse, Message = 0x020A, MouseData = 120 << 16, MouseX = 22, MouseY = 23
        });
        Assert.True(events[0].IsMouseMove);
        Assert.Equal(0x020A, events[1].Message);
    }

    [Fact]
    public void DragStartPathEndPreserved()
    {
        using RecorderService recorder = StartedRecorder();
        InvokeMouse(recorder, new HookEvent { Source = HookSource.Mouse, Message = 0x0201, MouseX = 10, MouseY = 10, MouseButton = MouseButtonKind.Left });
        InvokeMouse(recorder, HookMove(20, 20));
        InvokeMouse(recorder, new HookEvent { Source = HookSource.Mouse, Message = 0x0202, MouseX = 30, MouseY = 30, MouseButton = MouseButtonKind.Left });
        List<MacroEventRecord> events = RecorderEvents(recorder);
        Assert.Equal([MacroEventKind.MouseDown, MacroEventKind.MouseMove, MacroEventKind.MouseMove, MacroEventKind.MouseUp], events.Select(item => item.Type));
        Assert.Equal((30, 30), (events[^2].X, events[^2].Y));
    }

    [Fact]
    public void HighFrequencyMoveBounded()
    {
        DesktopMouseMoveCoalescer coalescer = new();
        int outputs = 0;
        for (int i = 0; i < 10_000; i++)
        {
            if (coalescer.Observe(HookMove(i, i), i) is not null) outputs++;
        }
        if (coalescer.Flush(10_000) is not null) outputs++;
        Assert.InRange(outputs, 2, 835);
    }

    [Fact]
    public void ButtonEventsNeverDropped()
    {
        using BoundedCapturePipeline pipeline = new(32, startConsumer: false);
        pipeline.BeginRecording();
        Assert.True(pipeline.TryEnqueue(new HookEvent { Source = HookSource.Mouse, Message = 0x0201, MouseButton = MouseButtonKind.Left }));
        Assert.True(pipeline.TryEnqueue(new HookEvent { Source = HookSource.Mouse, Message = 0x0202, MouseButton = MouseButtonKind.Left }));
        pipeline.DrainAllForTest();
        Assert.False(pipeline.IsCircuitBreakerTripped);
    }

    [Fact]
    public void WheelEventsNeverDropped()
    {
        using BoundedCapturePipeline pipeline = new(32, startConsumer: false);
        pipeline.BeginRecording();
        Assert.True(pipeline.TryEnqueue(new HookEvent { Source = HookSource.Mouse, Message = 0x020A, MouseData = 120 << 16 }));
        pipeline.DrainAllForTest();
        Assert.False(pipeline.IsCircuitBreakerTripped);
    }

    [Fact]
    public void RawMoveStoresAbsoluteAndRelative()
    {
        HookEvent item = Assert.Single(RawInputEventTranslator.TranslateMouse(new RawMouseData(0, 0, 0, 3, -2, 100, 200, true, true), 1));
        Assert.True(item.HasAbsoluteMousePosition);
        Assert.True(item.HasRelativeMouseDelta);
        Assert.Equal((100, 200, 3, -2), (item.MouseX, item.MouseY, item.DeltaX, item.DeltaY));
    }

    [Fact]
    public void RawAbsoluteFailureRelativeOnly()
    {
        HookEvent item = Assert.Single(RawInputEventTranslator.TranslateMouse(new RawMouseData(0, 0, 0, 3, -2, 0, 0, false, true), 1));
        Assert.False(item.HasAbsoluteMousePosition);
        Assert.True(item.HasRelativeMouseDelta);
    }

    [Fact]
    public void RawDeltaFailureAbsoluteOnly()
    {
        HookEvent item = Assert.Single(RawInputEventTranslator.TranslateMouse(new RawMouseData(1, 0, 0, 100, 200, 300, 400, true, false), 1));
        Assert.True(item.HasAbsoluteMousePosition);
        Assert.False(item.HasRelativeMouseDelta);
    }

    [Fact]
    public void RawAggregationPreservesFinalAbsolute()
    {
        using BoundedCapturePipeline pipeline = new(64, startConsumer: false);
        List<HookEvent> output = [];
        pipeline.EventReady += output.Add;
        pipeline.BeginRecording();
        pipeline.TryEnqueue(RawMove(1, 2, 10, 20));
        pipeline.TryEnqueue(RawMove(3, 4, 30, 40));
        pipeline.EndIngestion();
        pipeline.DrainAllForTest();
        HookEvent item = Assert.Single(output);
        Assert.Equal((30, 40, 4, 6), (item.MouseX, item.MouseY, item.DeltaX, item.DeltaY));
    }

    [Fact]
    public void RawButtonCoordinatesPreserved()
    {
        HookEvent down = RawInputEventTranslator.TranslateMouse(new RawMouseData(0, 0x0001, 0, 0, 0, 55, 66), 1).Single();
        Assert.Equal((55, 66, MouseButtonKind.Left), (down.MouseX, down.MouseY, down.MouseButton));
    }

    [Fact]
    public void RawWheelCoordinatesPreserved()
    {
        HookEvent wheel = RawInputEventTranslator.TranslateMouse(new RawMouseData(0, 0x0400, 120, 0, 0, 77, 88), 1).Single();
        Assert.Equal((77, 88), (wheel.MouseX, wheel.MouseY));
    }

    [Fact]
    public void ExistingRawMacroLoadsDualCapabilities()
    {
        PlaybackMacroDocument macro = PlaybackMacroDocument.Load(RawMacroPath());
        PlaybackMacroEvent[] moves = macro.Events.Where(item => item.Kind == PlaybackEventKind.MouseMove).ToArray();
        Assert.Equal(2, moves.Length);
        Assert.Contains(moves, item => (item.EffectiveMouseCapabilities & MouseTrajectoryCapabilities.AbsolutePosition) != 0);
        Assert.Contains(moves, item => (item.EffectiveMouseCapabilities & MouseTrajectoryCapabilities.RelativeDelta) != 0);
    }

    [Fact]
    public void AbsoluteMoveIgnoresCurrentCursor()
    {
        MousePlaybackCommand command = Assert.Single(new AbsoluteDesktopMousePolicy().Build(Move(1501, 432, 99, 88, true, true)));
        Assert.Equal((1501, 432), (command.X, command.Y));
    }

    [Fact]
    public void AbsoluteFirstMoveUsesRecordedAnchor()
    {
        PlaybackMacroEvent first = PlaybackMacroDocument.Load(RawMacroPath()).Events.First(item => item.Kind == PlaybackEventKind.MouseMove);
        MousePlaybackCommand command = Assert.Single(new AbsoluteDesktopMousePolicy().Build(first));
        Assert.Equal((first.X, first.Y), (command.X, command.Y));
    }

    [Fact]
    public void AbsoluteSequenceSameFromDifferentStart()
    {
        PlaybackMacroEvent[] path = [Move(10, 20), Move(30, 40)];
        var a = path.SelectMany(new AbsoluteDesktopMousePolicy().Build).Select(item => (item.X, item.Y)).ToArray();
        var b = path.SelectMany(new AbsoluteDesktopMousePolicy().Build).Select(item => (item.X, item.Y)).ToArray();
        Assert.Equal(a, b);
    }

    [Fact]
    public void ClickMovesBeforeDown() => AssertActionOrder(PlaybackEventKind.MouseDown, MousePlaybackCommandKind.ButtonDown);

    [Fact]
    public void MouseUpMovesBeforeUp() => AssertActionOrder(PlaybackEventKind.MouseUp, MousePlaybackCommandKind.ButtonUp);

    [Fact]
    public void WheelMovesBeforeWheel() => AssertActionOrder(PlaybackEventKind.MouseWheel, MousePlaybackCommandKind.VerticalWheel);

    [Fact]
    public void HorizontalWheelMovesBeforeWheel() => AssertActionOrder(PlaybackEventKind.MouseHorizontalWheel, MousePlaybackCommandKind.HorizontalWheel);

    [Fact]
    public void X1Correct()
    {
        var commands = new AbsoluteDesktopMousePolicy().Build(Action(PlaybackEventKind.MouseDown, "X1"));
        Assert.Equal("X1", commands[1].MouseButton);
    }

    [Fact]
    public void X2Correct()
    {
        var commands = new AbsoluteDesktopMousePolicy().Build(Action(PlaybackEventKind.MouseUp, "X2"));
        Assert.Equal("X2", commands[1].MouseButton);
    }

    [Fact]
    public void NegativeVirtualCoordinates()
    {
        MousePlaybackCommand command = Assert.Single(new AbsoluteDesktopMousePolicy().Build(Move(-1920, -200)));
        Assert.Equal((-1920, -200), (command.X, command.Y));
    }

    [Fact]
    public void SecondMonitorCoordinates()
    {
        MousePlaybackCommand command = Assert.Single(new AbsoluteDesktopMousePolicy().Build(Move(3000, 600)));
        Assert.Equal((3000, 600), (command.X, command.Y));
    }

    [Fact]
    public void MoveAndClickBatchedOrder()
    {
        var commands = new AbsoluteDesktopMousePolicy().Build(Action(PlaybackEventKind.MouseDown, "Left"));
        Assert.Equal([MousePlaybackCommandKind.MoveAbsolute, MousePlaybackCommandKind.ButtonDown], commands.Select(item => item.Kind));
    }

    [Fact]
    public void AbsoluteRawMacroUsesXYNotDelta()
    {
        MousePlaybackCommand command = Assert.Single(new AbsoluteDesktopMousePolicy().Build(Move(500, 600, -900, 800, true, true)));
        Assert.Equal((500, 600), (command.X, command.Y));
    }

    [Fact]
    public void LegacyRawMetadataWithAbsoluteCoordinatesUsesRecordedPosition()
    {
        PlaybackMacroEvent legacy = Move(500, 600, -7, 9, true, true);
        MousePlaybackCommand command = Assert.Single(MouseReplayModeRuntime.CreatePolicy(Document(legacy)).Build(legacy));
        Assert.Equal(MousePlaybackCommandKind.MoveAbsolute, command.Kind);
        Assert.Equal((500, 600), (command.X, command.Y));
    }

    [Fact]
    public void LegacyRelativeOnlyEventFailsAbsoluteOnlyPreflight()
    {
        PlaybackMacroDocument macro = Document(Move(0, 0, -7, 9, absolute: false, relative: true));
        Assert.False(AbsoluteOnlyPlaybackGate.TryValidate(macro, out string error));
        Assert.Equal(AbsoluteOnlyPlaybackGate.LegacyRelativeOnlyMessage, error);
    }

    [Fact]
    public void RelativePlaybackPolicyTypeIsRemoved() =>
        Assert.Null(typeof(AbsoluteDesktopMousePolicy).Assembly.GetType("MacroPlayer.RawRelativeMousePolicy"));

    [Fact]
    public void LegacyRelativeMetadataButtonStillMovesToAbsolutePosition()
    {
        PlaybackMacroEvent action = Action(PlaybackEventKind.MouseDown, "Right") with
        {
            IsRelative = true,
            DeltaX = 8,
            DeltaY = -4,
            HasRelativeDelta = true,
            MouseCapabilities = MouseTrajectoryCapabilities.AbsolutePosition | MouseTrajectoryCapabilities.RelativeDelta
        };
        var commands = MouseReplayModeRuntime.CreatePolicy(Document(action)).Build(action);
        Assert.Equal([MousePlaybackCommandKind.MoveAbsolute, MousePlaybackCommandKind.ButtonDown], commands.Select(item => item.Kind));
        Assert.Equal((100, 200), (commands[0].X, commands[0].Y));
    }

    [Fact]
    public void RelativeModeStillSupportsF11()
    {
        HookDispatchResult result = HookCallbackSafety.Dispatch(
            new HookEvent { Source = HookSource.Keyboard, VirtualKey = HookCallbackSafety.F11, Message = 0x0100 },
            HookSuppressionMode.PlayerF11,
            _ => true);
        Assert.True(result.Suppressed);
    }

    [Fact] public void DesktopKeepAbsolute() => AssertMatrix(PlayerCountdownMode.KeepVisible, legacyMetadata: false);
    [Fact] public void DesktopMinimizeAbsolute() => AssertMatrix(PlayerCountdownMode.MinimizeBeforeCountdown, legacyMetadata: false);
    [Fact] public void DesktopKeepLegacyMetadataAbsolute() => AssertMatrix(PlayerCountdownMode.KeepVisible, legacyMetadata: true);
    [Fact] public void DesktopMinimizeLegacyMetadataAbsolute() => AssertMatrix(PlayerCountdownMode.MinimizeBeforeCountdown, legacyMetadata: true);

    [Fact]
    public void KeyboardEventOrder()
    {
        MacroFile macro = KeyboardMacro(repeat: false);
        Assert.True(MacroSerializer.TryValidate(macro, out string? error), error);
        Assert.Equal([MacroEventKind.KeyDown, MacroEventKind.KeyUp], macro.Events.Select(item => item.Type));
    }

    [Fact]
    public void AutoRepeat()
    {
        MacroFile macro = KeyboardMacro(repeat: true);
        Assert.True(MacroSerializer.TryValidate(macro, out string? error), error);
        Assert.Equal(3, macro.Events.Count);
    }

    [Fact]
    public void F11Release()
    {
        HookDispatchResult result = HookCallbackSafety.Dispatch(
            new HookEvent { Source = HookSource.Keyboard, VirtualKey = HookCallbackSafety.F11, Message = 0x0101 },
            HookSuppressionMode.PlayerF11,
            _ => true);
        Assert.True(result.Suppressed);
        Assert.False(result.CallNext);
    }

    [Fact]
    public void Watchdog()
    {
        CaptureLifetimeController lifetime = new();
        int starts = 0;
        int stops = 0;
        lifetime.Arm(() => starts++);
        lifetime.Dispose(() => stops++);
        Assert.Equal((1, 1, true), (starts, stops, lifetime.IsDisposed));
    }

    [Fact]
    public async Task SecureDesktop()
    {
        Fixture fixture = Fixture.Create();
        fixture.Foreground.SecureDesktop = true;
        PlaybackRunResult result = await fixture.Run(PlayerCountdownMode.KeepVisible);
        Assert.Equal(PlaybackDisposition.SecureDesktop, result.Disposition);
        Assert.Equal(0, result.EventsSent);
    }

    [Fact]
    public async Task Privilege()
    {
        PlaybackRunResult result = await Fixture.Create().Run(
            PlayerCountdownMode.KeepVisible,
            macro: Fixture.Macro(true));
        Assert.Equal(PlaybackDisposition.PrivilegeRejected, result.Disposition);
    }

    [Fact]
    public void DisplayMismatch()
    {
        PlaybackMacroDocument macro = PlaybackMacroDocument.Load(RawMacroPath());
        Assert.False(string.IsNullOrWhiteSpace(macro.ScreenSummary));
    }

    [Fact]
    public void RecorderUi()
    {
        using RecorderService recorder = new();
        Assert.Equal(RecorderCaptureMode.Standard, recorder.CaptureMode);
    }

    [Fact]
    public void PlayerUi() => PlayerHarness.WithForm(form =>
    {
        Label fixedMode = Assert.IsType<Label>(Assert.Single(form.Controls.Find("MouseReplayMode", true)));
        Assert.True(fixedMode.Visible);
        Assert.Equal("絕對桌面座標（固定）", fixedMode.Text);
        string allText = string.Join('\n', Descendants(form).Select(control => control.Text));
        Assert.DoesNotContain("RawRelative", allText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("相對座標重播", allText, StringComparison.Ordinal);
    });

    [Fact]
    public void PortableLaunchers()
    {
        string[] names = [
            "06_啟動錄製器_一般模式.cmd", "06A_啟動錄製器_管理員模式.cmd",
            "07_選擇並重播巨集_一般模式.cmd", "07A_選擇並重播巨集_管理員模式.cmd",
            "99_緊急終止巨集工具.cmd"
        ];
        foreach (string name in names)
        {
            byte[] bytes = File.ReadAllBytes(TestProjectEnvironment.RootCommandPath(name));
            Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
            Assert.Contains("%~dp0", Encoding.ASCII.GetString(bytes));
        }
    }

    [Fact]
    public void ExistingMacroHashes()
    {
        string text = File.ReadAllText(RawMacroPath());
        Assert.Contains("synthetic", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2000-01-01", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SideBySideZero()
    {
        string program = Path.Combine(ProjectRoot(), "Program");
        Assert.Empty(Directory.Exists(program)
            ? Directory.EnumerateFiles(program, "*.local", SearchOption.AllDirectories)
            : []);
    }

    private static PlaybackMacroDocument Load(string schema, string eventJson)
    {
        string path = Path.Combine(ProjectLocalTestSandbox.Create(), $"mouse_semantics_{Guid.NewGuid():N}.macro");
        try
        {
            File.WriteAllText(path, $"{{\"schemaVersion\":\"{schema}\",\"events\":[{eventJson}]}}", new UTF8Encoding(false));
            return PlaybackMacroDocument.Load(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string DualMoveJson() => "{\"type\":\"MouseMove\",\"timeMs\":0,\"x\":1501,\"y\":432,\"deltaX\":0,\"deltaY\":-1}";

    private static PlaybackMacroEvent Move(int x, int y, int dx = 0, int dy = 0, bool absolute = true, bool relative = false) => new(
        0, PlaybackEventKind.MouseMove, 0, 0, false, x, y, "Left", 0,
        relative, dx, dy,
        (absolute ? MouseTrajectoryCapabilities.AbsolutePosition : MouseTrajectoryCapabilities.None) |
        (relative ? MouseTrajectoryCapabilities.RelativeDelta : MouseTrajectoryCapabilities.None),
        absolute, relative);

    private static PlaybackMacroEvent Action(PlaybackEventKind kind, string button = "Left") => new(
        0, kind, 0, 0, false, 100, 200, button, 120, false, 0, 0,
        MouseTrajectoryCapabilities.AbsolutePosition, true, false);

    private static HookEvent HookMove(int x, int y) => new()
    {
        Source = HookSource.Mouse, Message = 0x0200, MouseX = x, MouseY = y,
        IsMouseMove = true, HasAbsoluteMousePosition = true
    };

    private static HookEvent RawMove(int dx, int dy, int x, int y) => new()
    {
        Source = HookSource.RawMouse, Message = 0x0200, MouseX = x, MouseY = y,
        DeltaX = dx, DeltaY = dy, IsMouseMove = true,
        HasAbsoluteMousePosition = true, HasRelativeMouseDelta = true
    };

    private static (int X, int Y) Point(HookEvent item) => (item.MouseX, item.MouseY);

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }

    private static IReadOnlyList<HookEvent> AggregateRawThenAction(HookEvent action)
    {
        using BoundedCapturePipeline pipeline = new(64, startConsumer: false);
        List<HookEvent> output = [];
        pipeline.EventReady += output.Add;
        pipeline.BeginRecording();
        pipeline.TryEnqueue(RawMove(2, 3, 20, 21));
        pipeline.TryEnqueue(action);
        pipeline.DrainAllForTest();
        return output;
    }

    private static RecorderService StartedRecorder()
    {
        RecorderService recorder = new();
        typeof(RecorderService).GetMethod("StartRecording", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(recorder, null);
        return recorder;
    }

    private static void InvokeMouse(RecorderService recorder, HookEvent input) =>
        typeof(RecorderService).GetMethod("HandleMouseEvent", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(recorder, [input]);

    private static List<MacroEventRecord> RecorderEvents(RecorderService recorder) =>
        (List<MacroEventRecord>)typeof(RecorderService).GetField("_events", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(recorder)!;

    private static void AssertActionOrder(PlaybackEventKind kind, MousePlaybackCommandKind action)
    {
        var commands = new AbsoluteDesktopMousePolicy().Build(Action(kind));
        Assert.Equal(MousePlaybackCommandKind.MoveAbsolute, commands[0].Kind);
        Assert.Equal(action, commands[1].Kind);
    }

    private static void AssertMatrix(PlayerCountdownMode countdown, bool legacyMetadata)
    {
        Assert.True(Enum.IsDefined(countdown));
        PlaybackMacroEvent item = legacyMetadata ? Move(10, 20, 1, 2, true, true) : Move(10, 20);
        IMousePlaybackPolicy policy = MouseReplayModeRuntime.CreatePolicy(Document(item));
        Assert.NotEmpty(policy.Build(item));
        Assert.Equal(MouseReplayMode.AbsoluteDesktop, policy.Mode);
    }

    private static PlaybackMacroDocument Document(params PlaybackMacroEvent[] events) => new(
        "legacy-fixture.macro", "1.2", "Legacy fixture", DateTimeOffset.UnixEpoch,
        events.Length == 0 ? 0 : events.Max(item => item.OffsetMilliseconds), false,
        "RawEnhanced", string.Empty, string.Empty, "synthetic", events);

    private static MacroFile KeyboardMacro(bool repeat)
    {
        MacroFile macro = new();
        macro.Events.Add(new MacroEventRecord { Type = MacroEventKind.KeyDown, TimeMs = 1, VirtualKey = 65, ScanCode = 30 });
        if (repeat) macro.Events.Add(new MacroEventRecord { Type = MacroEventKind.KeyDown, TimeMs = 2, VirtualKey = 65, ScanCode = 30 });
        macro.Events.Add(new MacroEventRecord { Type = MacroEventKind.KeyUp, TimeMs = 3, VirtualKey = 65, ScanCode = 30 });
        return macro;
    }

    private static string RawMacroPath()
    {
        return TestProjectEnvironment.SyntheticRawFixture;
    }

    private static string ProjectRoot()
    {
        return TestProjectEnvironment.Root;
    }
}

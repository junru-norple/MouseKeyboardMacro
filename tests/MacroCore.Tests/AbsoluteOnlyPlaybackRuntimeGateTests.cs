using System.Drawing;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using MacroCore.Input;
using MacroCore.Models;
using MacroCore.Timing;
using MacroPlayer;
using MacroRecorder.Services;
using MacroRecorder.Tests;

namespace MacroCore.Tests;

/// <summary>
/// Non-live gates for the production absolute-only recording and playback seams.
/// These tests never call SendInput and never use the owner's Settings or Recordings.
/// </summary>
public sealed class AbsoluteOnlyPlaybackRuntimeGateTests
{
    private static readonly Rectangle DualMonitorDesktop = new(-1920, -200, 3840, 1200);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlaybackFactoryCreatesOnlyAbsoluteDesktopPolicy(bool legacyRawMetadata)
    {
        PlaybackMacroEvent move = Mouse(PlaybackEventKind.MouseMove, -400, 350) with
        {
            IsRelative = legacyRawMetadata,
            DeltaX = legacyRawMetadata ? 41 : 0,
            DeltaY = legacyRawMetadata ? -29 : 0,
            HasRelativeDelta = legacyRawMetadata,
            MouseCapabilities = legacyRawMetadata
                ? MouseTrajectoryCapabilities.AbsolutePosition | MouseTrajectoryCapabilities.RelativeDelta
                : MouseTrajectoryCapabilities.AbsolutePosition
        };
        PlaybackMacroDocument macro = Document(move);

        IMousePlaybackPolicy policy = MouseReplayModeRuntime.CreatePolicy(macro);

        Assert.IsType<AbsoluteDesktopMousePolicy>(policy);
        Assert.Equal(MouseReplayMode.AbsoluteDesktop, policy.Mode);
        Assert.Equal(MouseReplayMode.AbsoluteDesktop, MouseReplayModeRuntime.Recommend(macro));
        Assert.Equal(MouseReplayMode.AbsoluteDesktop, MouseReplayModeRuntime.Resolve(macro));
        MousePlaybackCommand command = Assert.Single(policy.Build(move));
        Assert.Equal(MousePlaybackCommandKind.MoveAbsolute, command.Kind);
        Assert.Equal((-400, 350), (command.X, command.Y));
    }

    [Fact]
    public async Task ProductionSessionAndComposerCreateOnlyAbsoluteVirtualDesktopMovement()
    {
        PlaybackMacroEvent move = Mouse(PlaybackEventKind.MouseMove, -1920, -200);
        AbsoluteDesktopMousePacket packet = AbsoluteDesktopInputComposer.Compose(
            move,
            DualMonitorDesktop);

        Assert.Equal((0, 0), (packet.X, packet.Y));
        Assert.Equal(0u, packet.MouseData);
        Assert.Equal(AbsoluteDesktopInputComposer.RequiredMovementFlags, packet.Flags);
        Assert.NotEqual(0u, packet.Flags & AbsoluteDesktopInputComposer.AbsoluteFlag);
        Assert.NotEqual(0u, packet.Flags & AbsoluteDesktopInputComposer.VirtualDeskFlag);
        Assert.NotEqual(0u, packet.Flags & AbsoluteDesktopInputComposer.MoveFlag);

        CollectingNativeSink sink = new();
        using SafePlaybackSession session = new(
            Document(move),
            PlaybackExecutionContext.Standard,
            new AlwaysSafeFocusPolicy(),
            new FakePlaybackClock(),
            sink);
        PlaybackRunResult result = await session.PlayAsync(CancellationToken.None);

        Assert.True(result.Completed, result.Message);
        SafePlaybackNativeInput sent = Assert.Single(sink.Inputs);
        Assert.Equal(SafePlaybackNativeInputKind.Mouse, sent.Kind);
        Assert.Equal(AbsoluteDesktopInputComposer.RequiredMovementFlags, sent.Flags);
        Assert.Equal(1, result.RuntimeCounters?.SendInputCallCount);
        Assert.Equal(1, result.RuntimeCounters?.NativeInputCount);
    }

    [Theory]
    [InlineData(-1920, -200, 0, 0)]
    [InlineData(1919, 999, 65535, 65535)]
    [InlineData(-2500, -500, 0, 0)]
    [InlineData(2500, 1400, 65535, 65535)]
    [InlineData(-1, 0, 32759, 10932)]
    public void DualMonitorNegativeAndBoundaryCoordinatesNormalizeSafely(
        int desktopX,
        int desktopY,
        int expectedX,
        int expectedY)
    {
        AbsoluteDesktopMousePacket packet = AbsoluteDesktopInputComposer.Compose(
            Mouse(PlaybackEventKind.MouseMove, desktopX, desktopY),
            DualMonitorDesktop);

        Assert.Equal((expectedX, expectedY), (packet.X, packet.Y));
        Assert.InRange(packet.X, 0, 65535);
        Assert.InRange(packet.Y, 0, 65535);
        Assert.Equal(AbsoluteDesktopInputComposer.RequiredMovementFlags, packet.Flags);
    }

    [Fact]
    public void FormalAndCompatibilityNormalizersMatchForValidDesktopGeometry()
    {
        (int Value, int Minimum, int Size, int Expected)[] cases =
        [
            (-2500, -1920, 3840, 0),
            (-1920, -1920, 3840, 0),
            (-1, -1920, 3840, 32759),
            (1919, -1920, 3840, 65535),
            (2500, -1920, 3840, 65535),
            (-100, -100, 2, 0),
            (-99, -100, 2, 65535),
            (1, 0, 3, 32768),
            (1, 0, 5, 16384)
        ];

        foreach ((int value, int minimum, int size, int expected) in cases)
        {
            MacroDisplayLayout layout = new()
            {
                VirtualBounds = new MacroRect
                {
                    X = minimum,
                    Y = minimum,
                    Width = size,
                    Height = size
                }
            };

            int formal = AbsoluteDesktopInputComposer.Normalize(value, minimum, size);
            Assert.Equal(expected, formal);
            Assert.Equal(formal, SendInputService.NormalizeToAbsoluteX(value, layout));
            Assert.Equal(formal, SendInputService.NormalizeToAbsoluteY(value, layout));
        }

        MacroDisplayLayout degenerate = new()
        {
            VirtualBounds = new MacroRect { X = 0, Y = 0, Width = 1, Height = 0 }
        };
        Assert.Equal(0, SendInputService.NormalizeToAbsoluteX(1, degenerate));
        Assert.Equal(0, SendInputService.NormalizeToAbsoluteY(1, degenerate));
        Assert.Equal(65535, AbsoluteDesktopInputComposer.Normalize(1, 0, 1));
    }

    [Fact]
    public void ClickDragWheelAndXButtonsRetainAbsoluteMovementFlags()
    {
        Assert.Equal(0x0001u, AbsoluteDesktopInputComposer.MoveFlag);
        Assert.Equal(0x0002u, AbsoluteDesktopInputComposer.LeftDownFlag);
        Assert.Equal(0x0004u, AbsoluteDesktopInputComposer.LeftUpFlag);
        Assert.Equal(0x0008u, AbsoluteDesktopInputComposer.RightDownFlag);
        Assert.Equal(0x0010u, AbsoluteDesktopInputComposer.RightUpFlag);
        Assert.Equal(0x0020u, AbsoluteDesktopInputComposer.MiddleDownFlag);
        Assert.Equal(0x0040u, AbsoluteDesktopInputComposer.MiddleUpFlag);
        Assert.Equal(0x0080u, AbsoluteDesktopInputComposer.XDownFlag);
        Assert.Equal(0x0100u, AbsoluteDesktopInputComposer.XUpFlag);
        Assert.Equal(0x0800u, AbsoluteDesktopInputComposer.WheelFlag);
        Assert.Equal(0x1000u, AbsoluteDesktopInputComposer.HorizontalWheelFlag);
        Assert.Equal(0x4000u, AbsoluteDesktopInputComposer.VirtualDeskFlag);
        Assert.Equal(0x8000u, AbsoluteDesktopInputComposer.AbsoluteFlag);
        Assert.Equal(0xC001u, AbsoluteDesktopInputComposer.RequiredMovementFlags);

        var cases = new (PlaybackMacroEvent Event, uint ActionFlag, uint MouseData)[]
        {
            (Mouse(PlaybackEventKind.MouseDown, -300, 50, "Left"), AbsoluteDesktopInputComposer.LeftDownFlag, 0),
            (Mouse(PlaybackEventKind.MouseUp, -300, 50, "Left"), AbsoluteDesktopInputComposer.LeftUpFlag, 0),
            (Mouse(PlaybackEventKind.MouseDown, 20, 60, "Right"), AbsoluteDesktopInputComposer.RightDownFlag, 0),
            (Mouse(PlaybackEventKind.MouseUp, 20, 60, "Right"), AbsoluteDesktopInputComposer.RightUpFlag, 0),
            (Mouse(PlaybackEventKind.MouseDown, 30, 70, "Middle"), AbsoluteDesktopInputComposer.MiddleDownFlag, 0),
            (Mouse(PlaybackEventKind.MouseUp, 30, 70, "Middle"), AbsoluteDesktopInputComposer.MiddleUpFlag, 0),
            (Mouse(PlaybackEventKind.MouseDown, 40, 80, "X1"), AbsoluteDesktopInputComposer.XDownFlag, 1),
            (Mouse(PlaybackEventKind.MouseUp, 40, 80, "X1"), AbsoluteDesktopInputComposer.XUpFlag, 1),
            (Mouse(PlaybackEventKind.MouseDown, 50, 90, "X2"), AbsoluteDesktopInputComposer.XDownFlag, 2),
            (Mouse(PlaybackEventKind.MouseUp, 50, 90, "X2"), AbsoluteDesktopInputComposer.XUpFlag, 2),
            (Mouse(PlaybackEventKind.MouseWheel, 60, 100, wheelDelta: 120), AbsoluteDesktopInputComposer.WheelFlag, 120),
            (Mouse(PlaybackEventKind.MouseHorizontalWheel, 70, 110, wheelDelta: -120), AbsoluteDesktopInputComposer.HorizontalWheelFlag, unchecked((uint)-120))
        };

        foreach ((PlaybackMacroEvent item, uint actionFlag, uint mouseData) in cases)
        {
            AbsoluteDesktopMousePacket packet = AbsoluteDesktopInputComposer.Compose(item, DualMonitorDesktop);
            Assert.Equal(AbsoluteDesktopInputComposer.RequiredMovementFlags, packet.Flags & AbsoluteDesktopInputComposer.RequiredMovementFlags);
            Assert.Equal(actionFlag, packet.Flags & ~AbsoluteDesktopInputComposer.RequiredMovementFlags);
            Assert.Equal(mouseData, packet.MouseData);
        }

        AbsoluteDesktopMousePacket[] drag =
        [
            AbsoluteDesktopInputComposer.Compose(Mouse(PlaybackEventKind.MouseDown, -1000, 0, "Left"), DualMonitorDesktop),
            AbsoluteDesktopInputComposer.Compose(Mouse(PlaybackEventKind.MouseMove, 500, 500), DualMonitorDesktop),
            AbsoluteDesktopInputComposer.Compose(Mouse(PlaybackEventKind.MouseUp, 1000, 700, "Left"), DualMonitorDesktop)
        ];
        Assert.Equal(AbsoluteDesktopInputComposer.LeftDownFlag, drag[0].Flags & ~AbsoluteDesktopInputComposer.RequiredMovementFlags);
        Assert.Equal(0u, drag[1].Flags & ~AbsoluteDesktopInputComposer.RequiredMovementFlags);
        Assert.Equal(AbsoluteDesktopInputComposer.LeftUpFlag, drag[2].Flags & ~AbsoluteDesktopInputComposer.RequiredMovementFlags);
        Assert.True(drag[0].X < drag[1].X && drag[1].X < drag[2].X);
    }

    [Fact]
    public void KeyboardEventsRemainValidAndNeverBecomeMousePackets()
    {
        PlaybackMacroEvent keyDown = new(0, PlaybackEventKind.KeyDown, 65, 30, false, 0, 0, string.Empty, 0);
        PlaybackMacroEvent keyUp = keyDown with { OffsetMilliseconds = 20, Kind = PlaybackEventKind.KeyUp };
        PlaybackMacroDocument macro = Document(keyDown, keyUp);

        Assert.True(AbsoluteOnlyPlaybackGate.TryValidate(macro, out string error), error);
        Assert.Empty(MouseReplayModeRuntime.CreatePolicy(macro).Build(keyDown));
        Assert.Throws<InvalidOperationException>(() => AbsoluteDesktopInputComposer.Compose(keyDown, DualMonitorDesktop));
    }

    [Fact]
    public void LegacyRawMetadataWithValidCoordinatesUsesXYAndDoesNotRewriteFile()
    {
        string path = WriteMacro("legacy-raw-with-xy.macro", """
            {
              "schemaVersion": "1.2",
              "macroName": "legacy-raw-with-xy",
              "duration": 0,
              "captureMetadata": {
                "captureMode": "RawEnhanced",
                "recommendedMouseReplayMode": "RawRelative"
              },
              "events": [
                {
                  "type": "MouseMove",
                  "timeMs": 0,
                  "x": -400,
                  "y": 350,
                  "deltaX": 999,
                  "deltaY": -999,
                  "mouseMovementMode": "RawRelative",
                  "mouseTrajectoryCapabilities": "AbsolutePosition, RelativeDelta"
                }
              ]
            }
            """);
        string before = Hash(path);

        PlaybackMacroDocument macro = PlaybackMacroDocument.Load(path);
        PlaybackMacroEvent move = Assert.Single(macro.Events);
        Assert.True(AbsoluteOnlyPlaybackGate.TryValidate(macro, out string error), error);
        Assert.Equal(MouseReplayMode.AbsoluteDesktop, MouseReplayModeRuntime.CreatePolicy(macro).Mode);
        MousePlaybackCommand command = Assert.Single(MouseReplayModeRuntime.CreatePolicy(macro).Build(move));
        Assert.Equal((-400, 350), (command.X, command.Y));
        _ = AbsoluteDesktopInputComposer.Compose(move, DualMonitorDesktop);

        Assert.Equal(before, Hash(path));
    }

    [Fact]
    public async Task RelativeOnlyLegacyMacroIsRejectedBeforeWindowCountdownFactoryOrInputAndIsUnchanged()
    {
        string path = WriteMacro("legacy-relative-only.macro", """
            {
              "schemaVersion": "1.2",
              "macroName": "legacy-relative-only",
              "duration": 0,
              "captureMetadata": {
                "captureMode": "RawEnhanced",
                "recommendedMouseReplayMode": "RawRelative"
              },
              "events": [
                {
                  "type": "RawMouseMove",
                  "timeMs": 0,
                  "deltaX": 17,
                  "deltaY": -23,
                  "mouseMovementMode": "RawRelative",
                  "mouseTrajectoryCapabilities": "RelativeDelta"
                }
              ]
            }
            """);
        string before = Hash(path);
        PlaybackMacroDocument macro = PlaybackMacroDocument.Load(path);
        PlaybackMacroEvent relativeOnly = Assert.Single(macro.Events);
        Fixture fixture = Fixture.Create();

        InvalidDataException composerError = Assert.Throws<InvalidDataException>(() =>
            AbsoluteDesktopInputComposer.Compose(relativeOnly, DualMonitorDesktop));
        Assert.Equal(AbsoluteOnlyPlaybackGate.MissingAbsoluteCoordinatesMessage, composerError.Message);
        PlaybackRunResult result = await fixture.Run(PlayerCountdownMode.KeepVisible, macro: macro);

        Assert.Equal(PlaybackDisposition.ValidationRejected, result.Disposition);
        Assert.Equal(AbsoluteOnlyPlaybackGate.LegacyRelativeOnlyMessage, result.Message);
        Assert.Equal(0, result.EventsSent);
        Assert.Equal(0, fixture.Window.PrepareCount);
        Assert.Equal(0, fixture.Countdown.RunCount);
        Assert.Equal(0, fixture.Factory.CreateCount);
        Assert.Equal(0, fixture.Factory.Session.EventsSentCount);
        Assert.Null(fixture.Log.StartContext);
        Assert.DoesNotContain("prepare", fixture.Order);
        Assert.DoesNotContain("countdown", fixture.Order);
        Assert.Equal(before, Hash(path));
    }

    [Theory]
    [InlineData(HookSource.Mouse, false)]
    [InlineData(HookSource.RawMouse, true)]
    public void NewStandardAndRawEnhancedMouseOutputContainsAbsoluteDesktopCoordinates(
        HookSource source,
        bool includesRawDelta)
    {
        HookEvent input = new()
        {
            Source = source,
            Message = 0x0200,
            TimestampMs = 11,
            MouseX = -640,
            MouseY = 720,
            IsMouseMove = true,
            DeltaX = includesRawDelta ? 31 : 0,
            DeltaY = includesRawDelta ? -19 : 0,
            IsAbsoluteMouse = !includesRawDelta,
            HasAbsoluteMousePosition = true,
            HasRelativeMouseDelta = includesRawDelta
        };

        Assert.True(
            AbsoluteRecordingMouseNormalizer.TryCreate(MacroEventKind.MouseMove, input, 25, out MacroEventRecord? record, out string error),
            error);
        Assert.NotNull(record);
        Assert.Equal(-640, record!.X);
        Assert.Equal(720, record.Y);
        Assert.Equal(25, record.TimeMs);
        Assert.Equal(MouseMovementMode.DesktopAbsolute, record.MouseMovementMode);
        Assert.True((record.EffectiveMouseTrajectoryCapabilities & MouseTrajectoryCapabilities.AbsolutePosition) != MouseTrajectoryCapabilities.None);

        PlaybackMacroEvent playback = Mouse(PlaybackEventKind.MouseMove, record.X!.Value, record.Y!.Value) with
        {
            DeltaX = record.DeltaX ?? 0,
            DeltaY = record.DeltaY ?? 0,
            HasRelativeDelta = record.DeltaX.HasValue && record.DeltaY.HasValue,
            MouseCapabilities = record.EffectiveMouseTrajectoryCapabilities
        };
        PlaybackMacroDocument macro = Document(playback);
        Assert.True(AbsoluteOnlyPlaybackGate.TryValidate(macro, out error), error);
        Assert.Equal(MouseReplayMode.AbsoluteDesktop, MouseReplayModeRuntime.CreatePolicy(macro).Mode);
        MousePlaybackCommand command = Assert.Single(MouseReplayModeRuntime.CreatePolicy(macro).Build(playback));
        Assert.Equal((-640, 720), (command.X, command.Y));
    }

    [Fact]
    public void RecorderRejectsRawMovementWithoutAbsoluteDesktopCoordinates()
    {
        HookEvent relativeOnly = new()
        {
            Source = HookSource.RawMouse,
            Message = 0x0200,
            IsMouseMove = true,
            DeltaX = 5,
            DeltaY = -7,
            HasRelativeMouseDelta = true,
            HasAbsoluteMousePosition = false
        };

        Assert.False(AbsoluteRecordingMouseNormalizer.TryCreate(
            MacroEventKind.MouseMove,
            relativeOnly,
            0,
            out MacroEventRecord? record,
            out string error));
        Assert.Null(record);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void NewRecordingOutputGateRejectsRelativeModeOrMissingAbsoluteData()
    {
        MacroEventRecord absolute = new()
        {
            Type = MacroEventKind.MouseMove,
            TimeMs = 0,
            X = -10,
            Y = 20,
            MouseMovementMode = MouseMovementMode.DesktopAbsolute,
            MouseTrajectoryCapabilities = MouseTrajectoryCapabilities.AbsolutePosition
        };
        MacroFile valid = new()
        {
            CaptureMetadata = new MacroCaptureMetadata
            {
                RecommendedMouseReplayMode = MouseReplayMode.AbsoluteDesktop
            },
            Events = [absolute]
        };
        Assert.True(AbsoluteRecordingOutputGate.TryValidate(valid, out string error), error);

        MacroFile legacyRecommendation = new()
        {
            CaptureMetadata = new MacroCaptureMetadata
            {
                RecommendedMouseReplayMode = MouseReplayMode.RawRelative
            },
            Events = [absolute]
        };
        Assert.False(AbsoluteRecordingOutputGate.TryValidate(legacyRecommendation, out error));

        MacroFile relativeOnly = new()
        {
            Events =
            [
                new MacroEventRecord
                {
                    Type = MacroEventKind.MouseMove,
                    DeltaX = 1,
                    DeltaY = -1,
                    MouseMovementMode = MouseMovementMode.RawRelative,
                    MouseTrajectoryCapabilities = MouseTrajectoryCapabilities.RelativeDelta
                }
            ]
        };
        Assert.False(AbsoluteRecordingOutputGate.TryValidate(relativeOnly, out error));
        Assert.Equal(AbsoluteRecordingMouseNormalizer.MissingCursorMessage, error);
    }

    [Fact]
    public void PlayerLayoutProbeRequiresFixedAbsoluteModeControl()
    {
        FieldInfo requiredNames = typeof(PlayerLayoutProbe).GetField("RequiredNames", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new Xunit.Sdk.XunitException("PlayerLayoutProbe.RequiredNames was not found.");
        string[] required = Assert.IsType<string[]>(requiredNames.GetValue(null));

        Assert.Contains("MouseReplayMode", required);
    }

    private static PlaybackMacroEvent Mouse(
        PlaybackEventKind kind,
        int x,
        int y,
        string button = "Left",
        int wheelDelta = 0) =>
        new(0, kind, 0, 0, false, x, y, button, wheelDelta,
            MouseCapabilities: MouseTrajectoryCapabilities.AbsolutePosition,
            HasAbsolutePosition: true);

    private static PlaybackMacroDocument Document(params PlaybackMacroEvent[] events) => new(
        "absolute-only-test.macro",
        "1.2",
        "Absolute-only test",
        DateTimeOffset.UnixEpoch,
        events.Length == 0 ? 0 : events.Max(item => item.OffsetMilliseconds),
        false,
        "DesktopSafe",
        string.Empty,
        string.Empty,
        "3840 x 1200",
        events,
        DualMonitorDesktop.Left,
        DualMonitorDesktop.Top,
        DualMonitorDesktop.Width,
        DualMonitorDesktop.Height);

    private static string WriteMacro(string name, string json)
    {
        string root = ProjectLocalTestSandbox.Create();
        string path = Path.Combine(root, name);
        File.WriteAllText(path, json.Replace("\n", "\r\n", StringComparison.Ordinal), new UTF8Encoding(false));
        return path;
    }

    private static string Hash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed class AlwaysSafeFocusPolicy : IPlaybackFocusPolicy
    {
        public int FocusChangeCount => 0;
        public PlaybackSafetyCheck CheckPeriodicSafety() => PlaybackSafetyCheck.Passed;
    }

    private sealed class CollectingNativeSink : ISafePlaybackNativeSink
    {
        public List<SafePlaybackNativeInput> Inputs { get; } = [];

        public uint Send(IReadOnlyList<SafePlaybackNativeInput> inputs)
        {
            Inputs.AddRange(inputs);
            return (uint)inputs.Count;
        }
    }
}

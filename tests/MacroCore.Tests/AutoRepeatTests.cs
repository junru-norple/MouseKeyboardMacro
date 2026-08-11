using System.Reflection;
using MacroCore.Input;
using MacroCore.Models;
using MacroCore.Serialization;
using MacroPlayer;
using MacroRecorder.Services;

namespace MacroCore.Tests;

public sealed class AutoRepeatTests
{
    [Fact]
    public void NormalKeyPair()
    {
        var macro = Macro(Key(MacroEventKind.KeyDown, 10), Key(MacroEventKind.KeyUp, 20));
        Assert.True(MacroSerializer.TryValidate(macro, out var error), error);
    }

    [Fact]
    public void AutoRepeatKey()
    {
        var macro = Macro(
            Key(MacroEventKind.KeyDown, 10),
            Key(MacroEventKind.KeyDown, 20),
            Key(MacroEventKind.KeyDown, 30),
            Key(MacroEventKind.KeyUp, 40));

        Assert.True(MacroSerializer.TryValidate(macro, out var error), error);
        Assert.Equal(4, macro.Events.Count);
    }

    [Fact]
    public void LongAutoRepeatKey()
    {
        var events = new List<MacroEventRecord> { Key(MacroEventKind.KeyDown, 0) };
        for (int i = 1; i <= 100; i++)
        {
            events.Add(Key(MacroEventKind.KeyDown, i));
        }
        events.Add(Key(MacroEventKind.KeyUp, 101));

        var macro = Macro(events.ToArray());
        Assert.True(MacroSerializer.TryValidate(macro, out var error), error);
        Assert.Equal(102, macro.Events.Count);
    }

    [Fact]
    public void CtrlSWithCtrlRepeat()
    {
        var macro = Macro(
            Key(MacroEventKind.KeyDown, 10, 162, 29),
            Key(MacroEventKind.KeyDown, 20, 162, 29),
            Key(MacroEventKind.KeyDown, 30, 162, 29),
            Key(MacroEventKind.KeyDown, 40, 83, 31),
            Key(MacroEventKind.KeyUp, 50, 83, 31),
            Key(MacroEventKind.KeyUp, 60, 162, 29));

        Assert.True(MacroSerializer.TryValidate(macro, out var error), error);
    }

    [Fact]
    public void OrphanKeyUp()
    {
        var macro = Macro(Key(MacroEventKind.KeyUp, 10, 162, 29));
        Assert.False(MacroSerializer.TryValidate(macro, out var error));
        Assert.Contains("沒有對應按下事件的 KeyUp", error ?? string.Empty);
        Assert.Contains("VK=162", error ?? string.Empty);
    }

    [Fact]
    public void TrueDanglingKeyDown()
    {
        var macro = Macro(Key(MacroEventKind.KeyDown, 10, 162, 29));
        Assert.False(MacroSerializer.TryValidate(macro, out var error));
        Assert.Contains("按鍵未釋放", error ?? string.Empty);
        Assert.Contains("VK=162", error ?? string.Empty);
        Assert.Contains("ScanCode=29", error ?? string.Empty);
        Assert.Contains("Extended=False", error ?? string.Empty);
        Assert.Contains("最後事件時間=10", error ?? string.Empty);
    }

    [Fact]
    public void PlaybackPressedSet()
    {
        var sender = new FakePlaybackInputSender();
        using var playback = new PlaybackService(new MacroFile(), sender);

        for (int i = 0; i < 101; i++)
        {
            playback.ProcessEvent(Key(MacroEventKind.KeyDown, i, 99, 81));
        }

        Assert.Equal(101, sender.KeyDownCount);
        Assert.Equal(1, playback.PressedKeyCount);

        playback.ProcessEvent(Key(MacroEventKind.KeyUp, 102, 99, 81));
        Assert.Equal(1, sender.KeyUpCount);
        Assert.Equal(0, playback.PressedKeyCount);
    }

    [Fact]
    public void EmergencyReleaseAfterRepeat()
    {
        var sender = new FakePlaybackInputSender();
        using var playback = new PlaybackService(new MacroFile(), sender);

        for (int i = 0; i < 100; i++)
        {
            playback.ProcessEvent(Key(MacroEventKind.KeyDown, i, 162, 29));
        }

        playback.ReleasePressedInputs();
        playback.ReleasePressedInputs();

        Assert.Equal(100, sender.KeyDownCount);
        Assert.Equal(1, sender.KeyUpCount);
        Assert.Equal(new KeyIdentity(162, 29, false), sender.ReleasedKeys[0]);
        Assert.Equal(0, playback.PressedKeyCount);
    }

    [Fact]
    public void RecorderFinalization()
    {
        MacroFile? completed = null;
        using var recorder = new RecorderService();
        recorder.RecordingReady += macro => completed = macro;

        InvokePrivate(recorder, "StartRecording");
        InvokePrivate(recorder, "HandleKeyboardEvent", new HookEvent
        {
            Source = HookSource.Keyboard,
            Message = 0x0100,
            VirtualKey = 65,
            ScanCode = 30
        });
        InvokePrivate(recorder, "HandleKeyboardEvent", new HookEvent
        {
            Source = HookSource.Keyboard,
            Message = 0x0100,
            VirtualKey = 65,
            ScanCode = 30
        });
        InvokePrivate(recorder, "HandleMouseEvent", new HookEvent
        {
            Source = HookSource.Mouse,
            Message = 0x0201,
            MouseButton = MouseButtonKind.Left,
            MouseX = 120,
            MouseY = 240
        });
        InvokePrivate(recorder, "StopRecording");

        Assert.NotNull(completed);
        var macro = completed!;
        Assert.Equal(2, macro.Events.Count(e => e.Type == MacroEventKind.KeyDown));
        Assert.Equal(1, macro.Events.Count(e => e.Type == MacroEventKind.KeyUp));
        Assert.Equal(1, macro.Events.Count(e => e.Type == MacroEventKind.MouseDown));
        Assert.Equal(1, macro.Events.Count(e => e.Type == MacroEventKind.MouseUp));
        Assert.Equal(2, macro.Events.Single(e => e.Type == MacroEventKind.KeyUp).Flags);
        Assert.Equal(2, macro.Events.Single(e => e.Type == MacroEventKind.MouseUp).Flags);
        Assert.True(MacroSerializer.TryValidate(macro, out var error), error);
        for (int i = 1; i < macro.Events.Count; i++)
        {
            Assert.True(macro.Events[i - 1].TimeMs <= macro.Events[i].TimeMs);
        }
    }

    [Fact]
    public void ExistingValidMacroCompatibility()
    {
        var macro = Macro(
            Key(MacroEventKind.KeyDown, 10, 65, 30),
            Key(MacroEventKind.KeyUp, 20, 65, 30),
            new MacroEventRecord { Type = MacroEventKind.MouseDown, TimeMs = 30, MouseButton = MouseButtonKind.Left, X = 1, Y = 2 },
            new MacroEventRecord { Type = MacroEventKind.MouseUp, TimeMs = 40, MouseButton = MouseButtonKind.Left, X = 1, Y = 2 });

        Assert.True(MacroSerializer.TryValidate(macro, out var error), error);
        Assert.Equal("1.0", macro.SchemaVersion);
    }

    [Fact]
    public void Test2MacroRegression()
    {
        var path = SyntheticMacroFixtureFactory.GetPath("AutoRepeat173.macro");
        var before = File.ReadAllBytes(path);

        Assert.True(MacroSerializer.TryLoad(path, out var loaded, out var error), error);
        Assert.NotNull(loaded);
        Assert.Equal(173, loaded!.Events.Count);
        Assert.Equal(173, loaded.Events.Count(e => e.Type is MacroEventKind.KeyDown or MacroEventKind.KeyUp or MacroEventKind.MouseMove));

        var after = File.ReadAllBytes(path);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(before)), Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(after)));
    }

    [Fact]
    public void InvalidMacroSafety()
    {
        var sender = new FakePlaybackInputSender();
        var cases = new List<string>
        {
            string.Empty,
            "{not-json",
            MacroSerializer.ToJson(new MacroFile { SchemaVersion = "999" }),
            MacroSerializer.ToJson(Macro(Key(MacroEventKind.KeyUp, 1))),
            MacroSerializer.ToJson(Macro(Key(MacroEventKind.KeyDown, 1)))
        };

        foreach (var content in cases)
        {
            var path = Path.Combine(ProjectLocalTestSandbox.Create(), $"invalid_{Guid.NewGuid():N}.macro");
            try
            {
                File.WriteAllText(path, content);
                Assert.False(MacroSerializer.TryLoad(path, out _, out _));
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        Assert.Equal(0, sender.TotalSendCount);
    }

    [Fact]
    public void RecorderControlKeysAreExcluded()
    {
        MacroFile? completed = null;
        using var recorder = new RecorderService();
        recorder.RecordingReady += macro => completed = macro;

        InvokePrivate(recorder, "StartRecording");
        InvokePrivate(recorder, "HandleKeyboardEvent", new HookEvent
        {
            Source = HookSource.Keyboard,
            Message = 0x0100,
            VirtualKey = 0x7A,
            ScanCode = 87
        });
        InvokePrivate(recorder, "StopRecording");

        Assert.NotNull(completed);
        Assert.Equal(0, completed!.Events.Count);
    }

    private static MacroFile Macro(params MacroEventRecord[] events)
    {
        return new MacroFile
        {
            SchemaVersion = "1.0",
            DurationMs = events.Length == 0 ? 0 : events.Max(e => e.TimeMs),
            Events = events.ToList()
        };
    }

    private static MacroEventRecord Key(MacroEventKind type, long timeMs, int virtualKey = 65, int scanCode = 30, bool isExtended = false)
    {
        return new MacroEventRecord
        {
            Type = type,
            TimeMs = timeMs,
            VirtualKey = virtualKey,
            ScanCode = scanCode,
            IsExtended = isExtended
        };
    }

    private static void InvokePrivate(object target, string methodName, params object[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(target, args);
    }

    private sealed class FakePlaybackInputSender : IPlaybackInputSender
    {
        public int KeyDownCount { get; private set; }
        public int KeyUpCount { get; private set; }
        public int MouseSendCount { get; private set; }
        public List<KeyIdentity> ReleasedKeys { get; } = [];
        public int TotalSendCount => KeyDownCount + KeyUpCount + MouseSendCount;

        public void KeyDown(int scanCode, int virtualKey, bool isExtended) => KeyDownCount++;

        public void KeyUp(int scanCode, int virtualKey, bool isExtended)
        {
            KeyUpCount++;
            ReleasedKeys.Add(new KeyIdentity(virtualKey, scanCode, isExtended));
        }

        public void MouseDown(MouseButtonKind button, MacroDisplayLayout layout, int x, int y) => MouseSendCount++;
        public void MouseUp(MouseButtonKind button, MacroDisplayLayout layout, int x, int y) => MouseSendCount++;
        public void MouseMove(int x, int y, MacroDisplayLayout layout) => MouseSendCount++;
        public void MouseWheel(int delta, MacroDisplayLayout layout, int x, int y) => MouseSendCount++;
        public void MouseHorizontalWheel(int delta, MacroDisplayLayout layout, int x, int y) => MouseSendCount++;
        public void ReleaseMouseButton(MouseButtonKind button) => MouseSendCount++;
    }
}

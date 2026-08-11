using MacroCore.Models;

namespace MacroCore.Input;

public enum HookSource
{
    Keyboard,
    Mouse,
    RawKeyboard,
    RawMouse
}

public sealed class HookEvent
{
    public HookSource Source { get; init; }
    public int Message { get; init; }
    public long TimestampMs { get; init; }
    public int VirtualKey { get; init; }
    public int ScanCode { get; init; }
    public bool IsExtended { get; init; }
    public bool IsE0 { get; init; }
    public bool IsE1 { get; init; }
    public bool IsInjected { get; init; }
    public bool IsLowerIntegrityInjected { get; init; }
    public ulong ExtraInfo { get; init; }
    public bool IsOwnSyntheticInput { get; init; }
    public int MouseX { get; init; }
    public int MouseY { get; init; }
    public int MouseData { get; init; }
    public bool IsMouseMove { get; init; }
    public MouseButtonKind? MouseButton { get; init; }
    public int DeltaX { get; init; }
    public int DeltaY { get; init; }
    public bool IsAbsoluteMouse { get; init; }
    public bool HasAbsoluteMousePosition { get; init; } = true;
    public bool HasRelativeMouseDelta { get; init; }

    public bool IsKeyboard => Source is HookSource.Keyboard or HookSource.RawKeyboard;
    public bool IsMouse => Source is HookSource.Mouse or HookSource.RawMouse;
    public bool IsRaw => Source is HookSource.RawKeyboard or HookSource.RawMouse;
    public CaptureSourceKind CaptureSource => Source switch
    {
        HookSource.Keyboard => CaptureSourceKind.LowLevelKeyboard,
        HookSource.Mouse => CaptureSourceKind.LowLevelMouse,
        HookSource.RawKeyboard => CaptureSourceKind.RawKeyboard,
        _ => CaptureSourceKind.RawMouse
    };
}

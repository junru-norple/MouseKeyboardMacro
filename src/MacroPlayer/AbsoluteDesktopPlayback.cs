using MacroCore.Input;
using MacroCore.Models;

namespace MacroPlayer;

public static class AbsoluteOnlyPlaybackGate
{
    public const string LegacyRelativeOnlyMessage =
        "此舊巨集只包含相對座標資料，目前版本無法安全播放。請使用新版錄製器重新錄製。";

    public const string MissingAbsoluteCoordinatesMessage =
        "此巨集包含缺少絕對桌面座標的滑鼠資料，目前版本無法安全播放。請使用新版錄製器重新錄製。";

    public static bool TryValidate(PlaybackMacroDocument macro, out string error)
    {
        ArgumentNullException.ThrowIfNull(macro);
        PlaybackMacroEvent[] mouseEvents = macro.Events.Where(IsMouse).ToArray();
        PlaybackMacroEvent[] missingAbsolute = mouseEvents.Where(item => !item.HasAbsolutePosition).ToArray();
        if (missingAbsolute.Length == 0)
        {
            error = string.Empty;
            return true;
        }

        bool hasAnyAbsolute = mouseEvents.Any(item => item.HasAbsolutePosition);
        bool hasLegacyRelativeData = missingAbsolute.Any(item =>
            item.IsRelative || item.HasRelativeDelta ||
            (item.EffectiveMouseCapabilities & MouseTrajectoryCapabilities.RelativeDelta) != 0);
        error = !hasAnyAbsolute && hasLegacyRelativeData
            ? LegacyRelativeOnlyMessage
            : MissingAbsoluteCoordinatesMessage;
        return false;
    }

    public static void EnsureValid(PlaybackMacroDocument macro)
    {
        if (!TryValidate(macro, out string error))
        {
            throw new InvalidDataException(error);
        }
    }

    public static bool TryValidate(MacroFile macro, out string error)
    {
        ArgumentNullException.ThrowIfNull(macro);
        MacroEventRecord[] mouseEvents = macro.Events.Where(IsMouse).ToArray();
        MacroEventRecord[] missingAbsolute = mouseEvents
            .Where(item => !item.X.HasValue || !item.Y.HasValue)
            .ToArray();
        if (missingAbsolute.Length == 0)
        {
            error = string.Empty;
            return true;
        }

        bool hasAnyAbsolute = mouseEvents.Any(item => item.X.HasValue && item.Y.HasValue);
        bool hasLegacyRelativeData = missingAbsolute.Any(item =>
            item.MouseMovementMode == MouseMovementMode.RawRelative ||
            (item.DeltaX.HasValue && item.DeltaY.HasValue));
        error = !hasAnyAbsolute && hasLegacyRelativeData
            ? LegacyRelativeOnlyMessage
            : MissingAbsoluteCoordinatesMessage;
        return false;
    }

    public static void EnsureValid(MacroFile macro)
    {
        if (!TryValidate(macro, out string error))
        {
            throw new InvalidDataException(error);
        }
    }

    private static bool IsMouse(PlaybackMacroEvent item) => item.Kind is
        PlaybackEventKind.MouseMove or PlaybackEventKind.MouseDown or PlaybackEventKind.MouseUp or
        PlaybackEventKind.MouseWheel or PlaybackEventKind.MouseHorizontalWheel;

    private static bool IsMouse(MacroEventRecord item) => item.Type is
        MacroEventKind.MouseMove or MacroEventKind.MouseDown or MacroEventKind.MouseUp or
        MacroEventKind.MouseWheel or MacroEventKind.MouseHorizontalWheel;
}

public readonly record struct AbsoluteDesktopMousePacket(int X, int Y, uint MouseData, uint Flags);

public enum SafePlaybackNativeInputKind
{
    Mouse,
    Keyboard
}

public readonly record struct SafePlaybackNativeInput(
    SafePlaybackNativeInputKind Kind,
    int X = 0,
    int Y = 0,
    uint MouseData = 0,
    uint Flags = 0,
    ushort VirtualKey = 0,
    ushort ScanCode = 0,
    nint ExtraInfo = default);

/// <summary>Non-live test seam around the production session's final native input boundary.</summary>
public interface ISafePlaybackNativeSink
{
    uint Send(IReadOnlyList<SafePlaybackNativeInput> inputs);
}

/// <summary>
/// Pure packet composition used by the production SendInput path and non-live regression tests.
/// </summary>
public static class AbsoluteDesktopInputComposer
{
    public const uint MoveFlag = AbsoluteDesktopInputContract.MoveFlag;
    public const uint LeftDownFlag = AbsoluteDesktopInputContract.LeftDownFlag;
    public const uint LeftUpFlag = AbsoluteDesktopInputContract.LeftUpFlag;
    public const uint RightDownFlag = AbsoluteDesktopInputContract.RightDownFlag;
    public const uint RightUpFlag = AbsoluteDesktopInputContract.RightUpFlag;
    public const uint MiddleDownFlag = AbsoluteDesktopInputContract.MiddleDownFlag;
    public const uint MiddleUpFlag = AbsoluteDesktopInputContract.MiddleUpFlag;
    public const uint XDownFlag = AbsoluteDesktopInputContract.XDownFlag;
    public const uint XUpFlag = AbsoluteDesktopInputContract.XUpFlag;
    public const uint WheelFlag = AbsoluteDesktopInputContract.WheelFlag;
    public const uint HorizontalWheelFlag = AbsoluteDesktopInputContract.HorizontalWheelFlag;
    public const uint VirtualDeskFlag = AbsoluteDesktopInputContract.VirtualDeskFlag;
    public const uint AbsoluteFlag = AbsoluteDesktopInputContract.AbsoluteFlag;
    public const uint RequiredMovementFlags = AbsoluteDesktopInputContract.RequiredMovementFlags;

    public static AbsoluteDesktopMousePacket Compose(PlaybackMacroEvent item, Rectangle virtualDesktop)
    {
        if (!IsMouse(item.Kind))
        {
            throw new InvalidOperationException("事件不是滑鼠事件。");
        }
        if (!item.HasAbsolutePosition)
        {
            throw new InvalidDataException(AbsoluteOnlyPlaybackGate.MissingAbsoluteCoordinatesMessage);
        }

        int normalizedX = Normalize(item.X, virtualDesktop.Left, virtualDesktop.Width);
        int normalizedY = Normalize(item.Y, virtualDesktop.Top, virtualDesktop.Height);
        (uint actionFlags, uint mouseData) = GetAction(item);
        return new AbsoluteDesktopMousePacket(
            normalizedX,
            normalizedY,
            mouseData,
            RequiredMovementFlags | actionFlags);
    }

    public static int Normalize(int value, int minimum, int size) =>
        AbsoluteDesktopInputContract.NormalizeCoordinate(value, minimum, size);

    private static (uint Flags, uint MouseData) GetAction(PlaybackMacroEvent item) => item.Kind switch
    {
        PlaybackEventKind.MouseMove => (0, 0),
        PlaybackEventKind.MouseDown => Button(item.MouseButton, up: false),
        PlaybackEventKind.MouseUp => Button(item.MouseButton, up: true),
        PlaybackEventKind.MouseWheel => (WheelFlag, unchecked((uint)item.WheelDelta)),
        PlaybackEventKind.MouseHorizontalWheel => (HorizontalWheelFlag, unchecked((uint)item.WheelDelta)),
        _ => throw new InvalidOperationException("Unsupported mouse event.")
    };

    private static (uint Flags, uint MouseData) Button(string button, bool up)
    {
        string normalized = button.ToUpperInvariant();
        return normalized switch
        {
            "RIGHT" => (up ? RightUpFlag : RightDownFlag, 0),
            "MIDDLE" => (up ? MiddleUpFlag : MiddleDownFlag, 0),
            "X2" or "XBUTTON2" => (up ? XUpFlag : XDownFlag, 2),
            "X1" or "XBUTTON1" => (up ? XUpFlag : XDownFlag, 1),
            _ => (up ? LeftUpFlag : LeftDownFlag, 0)
        };
    }

    private static bool IsMouse(PlaybackEventKind kind) => kind is
        PlaybackEventKind.MouseMove or PlaybackEventKind.MouseDown or PlaybackEventKind.MouseUp or
        PlaybackEventKind.MouseWheel or PlaybackEventKind.MouseHorizontalWheel;
}

using MacroCore.Input;
using MacroCore.Models;

namespace MacroRecorder.Services;

/// <summary>
/// Converts either low-level or Raw Input mouse observations into new-format events that always
/// contain desktop x/y. Raw deltas may be retained as capture evidence, but never select playback.
/// </summary>
public static class AbsoluteRecordingMouseNormalizer
{
    private const int MouseWheelMessage = 0x020A;
    private const int MouseHorizontalWheelMessage = 0x020E;

    public const string MissingCursorMessage =
        "無法取得可靠的桌面游標座標，本次錄製已安全停止且不會儲存。請重新錄製。";

    public static bool TryCreate(
        MacroEventKind kind,
        HookEvent input,
        long timeMilliseconds,
        out MacroEventRecord? record,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(input);
        record = null;
        if (!input.IsMouse)
        {
            error = "輸入事件不是滑鼠事件。";
            return false;
        }
        if (!input.HasAbsoluteMousePosition)
        {
            error = MissingCursorMessage;
            return false;
        }

        bool hasRawDelta = input.Source == HookSource.RawMouse &&
                           input.IsMouseMove &&
                           !input.IsAbsoluteMouse &&
                           input.HasRelativeMouseDelta;
        MouseTrajectoryCapabilities capabilities = MouseTrajectoryCapabilities.AbsolutePosition;
        if (hasRawDelta)
        {
            capabilities |= MouseTrajectoryCapabilities.RelativeDelta;
        }

        record = new MacroEventRecord
        {
            Type = kind,
            TimeMs = timeMilliseconds,
            X = input.MouseX,
            Y = input.MouseY,
            MouseButton = input.MouseButton,
            WheelDelta = input.Message is MouseWheelMessage or MouseHorizontalWheelMessage
                ? (short)(input.MouseData >> 16)
                : null,
            IsExtended = input.IsExtended,
            Flags = (input.IsInjected ? 1 : 0) |
                    (input.IsLowerIntegrityInjected ? 4 : 0) |
                    (input.IsE1 ? 8 : 0),
            CaptureSource = input.CaptureSource,
            MouseMovementMode = input.IsMouseMove ? MouseMovementMode.DesktopAbsolute : null,
            DeltaX = hasRawDelta ? input.DeltaX : null,
            DeltaY = hasRawDelta ? input.DeltaY : null,
            MouseTrajectoryCapabilities = capabilities
        };
        error = string.Empty;
        return true;
    }
}

public static class AbsoluteRecordingOutputGate
{
    public static bool TryValidate(MacroFile macro, out string error)
    {
        ArgumentNullException.ThrowIfNull(macro);
        foreach (MacroEventRecord item in macro.Events.Where(IsMouse))
        {
            if (!item.X.HasValue || !item.Y.HasValue ||
                (item.EffectiveMouseTrajectoryCapabilities & MouseTrajectoryCapabilities.AbsolutePosition) == 0)
            {
                error = AbsoluteRecordingMouseNormalizer.MissingCursorMessage;
                return false;
            }
            if (item.Type == MacroEventKind.MouseMove && item.MouseMovementMode == MouseMovementMode.RawRelative)
            {
                error = "新錄製的滑鼠移動不是絕對桌面座標，已安全停止且不會儲存。";
                return false;
            }
        }

        if (macro.CaptureMetadata?.RecommendedMouseReplayMode == MouseReplayMode.RawRelative)
        {
            error = "新錄製的滑鼠重播建議不是絕對桌面座標，已安全停止且不會儲存。";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool IsMouse(MacroEventRecord item) => item.Type is
        MacroEventKind.MouseMove or MacroEventKind.MouseDown or MacroEventKind.MouseUp or
        MacroEventKind.MouseWheel or MacroEventKind.MouseHorizontalWheel;
}

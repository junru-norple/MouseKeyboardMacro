using MacroCore.Models;

namespace MacroPlayer;

public enum MousePlaybackCommandKind
{
    MoveAbsolute,
    ButtonDown,
    ButtonUp,
    VerticalWheel,
    HorizontalWheel
}

public sealed record MousePlaybackCommand(
    MousePlaybackCommandKind Kind,
    int X = 0,
    int Y = 0,
    string MouseButton = "Left",
    int WheelDelta = 0);

public interface IMousePlaybackPolicy
{
    MouseReplayMode Mode { get; }
    IReadOnlyList<MousePlaybackCommand> Build(PlaybackMacroEvent item);
}

public sealed class AbsoluteDesktopMousePolicy : IMousePlaybackPolicy
{
    public MouseReplayMode Mode => MouseReplayMode.AbsoluteDesktop;

    public IReadOnlyList<MousePlaybackCommand> Build(PlaybackMacroEvent item)
    {
        if (!IsMouse(item.Kind))
        {
            return Array.Empty<MousePlaybackCommand>();
        }
        if (!item.HasAbsolutePosition)
        {
            throw new InvalidDataException(AbsoluteOnlyPlaybackGate.MissingAbsoluteCoordinatesMessage);
        }

        MousePlaybackCommand move = new(MousePlaybackCommandKind.MoveAbsolute, X: item.X, Y: item.Y);
        return item.Kind switch
        {
            PlaybackEventKind.MouseMove => [move],
            PlaybackEventKind.MouseDown => [move, new(MousePlaybackCommandKind.ButtonDown, MouseButton: item.MouseButton)],
            PlaybackEventKind.MouseUp => [move, new(MousePlaybackCommandKind.ButtonUp, MouseButton: item.MouseButton)],
            PlaybackEventKind.MouseWheel => [move, new(MousePlaybackCommandKind.VerticalWheel, WheelDelta: item.WheelDelta)],
            PlaybackEventKind.MouseHorizontalWheel => [move, new(MousePlaybackCommandKind.HorizontalWheel, WheelDelta: item.WheelDelta)],
            _ => Array.Empty<MousePlaybackCommand>()
        };
    }

    private static bool IsMouse(PlaybackEventKind kind) => kind is
        PlaybackEventKind.MouseMove or PlaybackEventKind.MouseDown or PlaybackEventKind.MouseUp or
        PlaybackEventKind.MouseWheel or PlaybackEventKind.MouseHorizontalWheel;
}

/// <summary>
/// Absolute-only runtime factory. The legacy enum value remains only for old JSON compatibility.
/// </summary>
public static class MouseReplayModeRuntime
{
    public static MouseReplayMode RequestedMode => MouseReplayMode.AbsoluteDesktop;

    public static MouseReplayMode Recommend(PlaybackMacroDocument macro)
    {
        ArgumentNullException.ThrowIfNull(macro);
        return MouseReplayMode.AbsoluteDesktop;
    }

    public static MouseReplayMode Resolve(PlaybackMacroDocument macro)
    {
        ArgumentNullException.ThrowIfNull(macro);
        return MouseReplayMode.AbsoluteDesktop;
    }

    public static IMousePlaybackPolicy CreatePolicy(PlaybackMacroDocument macro)
    {
        ArgumentNullException.ThrowIfNull(macro);
        return new AbsoluteDesktopMousePolicy();
    }
}

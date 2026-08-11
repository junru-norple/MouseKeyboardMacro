using System.Runtime.InteropServices;

namespace MacroCore.Input;

public sealed class DesktopMouseMoveCoalescer
{
    public const int DefaultWindowMilliseconds = 12;
    public const int DefaultMinimumDistancePixels = 1;

    private readonly int _windowMilliseconds;
    private readonly int _minimumDistanceSquared;
    private HookEvent? _pending;
    private bool _hasEmitted;
    private int _lastX;
    private int _lastY;
    private long _lastEmissionMilliseconds;

    public DesktopMouseMoveCoalescer(
        int windowMilliseconds = DefaultWindowMilliseconds,
        int minimumDistancePixels = DefaultMinimumDistancePixels)
    {
        _windowMilliseconds = Math.Clamp(windowMilliseconds, 8, 16);
        var distance = Math.Clamp(minimumDistancePixels, 1, 2);
        _minimumDistanceSquared = distance * distance;
    }

    public bool HasPending => _pending is not null;

    public void Reset(int? anchorX = null, int? anchorY = null)
    {
        _pending = null;
        _hasEmitted = anchorX.HasValue && anchorY.HasValue;
        _lastX = anchorX ?? 0;
        _lastY = anchorY ?? 0;
        _lastEmissionMilliseconds = 0;
    }

    public HookEvent? Observe(HookEvent input, long elapsedMilliseconds)
    {
        if (!input.IsMouseMove || !input.HasAbsoluteMousePosition)
        {
            return null;
        }

        _pending = input;
        var dx = input.MouseX - _lastX;
        var dy = input.MouseY - _lastY;
        if (_hasEmitted && dx * dx + dy * dy < _minimumDistanceSquared)
        {
            return null;
        }
        if (_hasEmitted && elapsedMilliseconds - _lastEmissionMilliseconds < _windowMilliseconds)
        {
            return null;
        }

        return Flush(elapsedMilliseconds);
    }

    public HookEvent? Flush(long elapsedMilliseconds)
    {
        var result = _pending;
        if (result is null)
        {
            return null;
        }

        _pending = null;
        _hasEmitted = true;
        _lastX = result.MouseX;
        _lastY = result.MouseY;
        _lastEmissionMilliseconds = Math.Max(_lastEmissionMilliseconds, elapsedMilliseconds);
        return result;
    }
}

public static class WindowsCursorPosition
{
    public static bool TryGet(out int x, out int y)
    {
        if (GetCursorPos(out var point))
        {
            x = point.X;
            y = point.Y;
            return true;
        }

        x = 0;
        y = 0;
        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}

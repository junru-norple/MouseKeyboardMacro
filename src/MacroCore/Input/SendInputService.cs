using System.Runtime.InteropServices;
using MacroCore.Models;

namespace MacroCore.Input;

/// <summary>
/// Authoritative absolute-desktop coordinate and Win32 mouse-flag contract shared by playback paths.
/// </summary>
public static class AbsoluteDesktopInputContract
{
    public const uint MoveFlag = 0x0001;
    public const uint LeftDownFlag = 0x0002;
    public const uint LeftUpFlag = 0x0004;
    public const uint RightDownFlag = 0x0008;
    public const uint RightUpFlag = 0x0010;
    public const uint MiddleDownFlag = 0x0020;
    public const uint MiddleUpFlag = 0x0040;
    public const uint XDownFlag = 0x0080;
    public const uint XUpFlag = 0x0100;
    public const uint WheelFlag = 0x0800;
    public const uint HorizontalWheelFlag = 0x1000;
    public const uint VirtualDeskFlag = 0x4000;
    public const uint AbsoluteFlag = 0x8000;
    public const uint RequiredMovementFlags = MoveFlag | AbsoluteFlag | VirtualDeskFlag;

    public static int NormalizeCoordinate(int value, int minimum, int size)
    {
        int safeSize = Math.Max(2, size);
        double offset = (double)value - minimum;
        double normalized = Math.Round(offset * 65535d / (safeSize - 1));
        return (int)Math.Clamp(normalized, 0d, 65535d);
    }
}

public static class SendInputService
{
    private const int INPUT_MOUSE = 0;
    private const int INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE = AbsoluteDesktopInputContract.MoveFlag;
    private const uint MOUSEEVENTF_LEFTDOWN = AbsoluteDesktopInputContract.LeftDownFlag;
    private const uint MOUSEEVENTF_LEFTUP = AbsoluteDesktopInputContract.LeftUpFlag;
    private const uint MOUSEEVENTF_RIGHTDOWN = AbsoluteDesktopInputContract.RightDownFlag;
    private const uint MOUSEEVENTF_RIGHTUP = AbsoluteDesktopInputContract.RightUpFlag;
    private const uint MOUSEEVENTF_MIDDLEDOWN = AbsoluteDesktopInputContract.MiddleDownFlag;
    private const uint MOUSEEVENTF_MIDDLEUP = AbsoluteDesktopInputContract.MiddleUpFlag;
    private const uint MOUSEEVENTF_XDOWN = AbsoluteDesktopInputContract.XDownFlag;
    private const uint MOUSEEVENTF_XUP = AbsoluteDesktopInputContract.XUpFlag;
    private const uint MOUSEEVENTF_WHEEL = AbsoluteDesktopInputContract.WheelFlag;
    private const uint MOUSEEVENTF_HWHEEL = AbsoluteDesktopInputContract.HorizontalWheelFlag;
    private const uint MOUSEEVENTF_ABSOLUTE = AbsoluteDesktopInputContract.AbsoluteFlag;
    private const uint MOUSEEVENTF_VIRTUALDESK = AbsoluteDesktopInputContract.VirtualDeskFlag;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

    public static int NormalizeToAbsoluteX(int x, MacroDisplayLayout layout)
    {
        return NormalizeCoordinate(x, layout.VirtualBounds.X, layout.VirtualBounds.Width);
    }

    public static int NormalizeToAbsoluteY(int y, MacroDisplayLayout layout)
    {
        return NormalizeCoordinate(y, layout.VirtualBounds.Y, layout.VirtualBounds.Height);
    }

    public static void MoveMouseTo(int x, int y, MacroDisplayLayout layout)
    {
        int nx = NormalizeToAbsoluteX(x, layout);
        int ny = NormalizeToAbsoluteY(y, layout);

        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = nx,
                    dy = ny,
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                    time = 0,
                    dwExtraInfo = InputSyntheticMarker.Value
                }
            }
        };
        SendInputChecked(input);
    }

    public static void MouseDown(MouseButtonKind button, MacroDisplayLayout layout, int x, int y)
    {
        MoveMouseTo(x, y, layout);
        uint flag = button switch
        {
            MouseButtonKind.Left => MOUSEEVENTF_LEFTDOWN,
            MouseButtonKind.Right => MOUSEEVENTF_RIGHTDOWN,
            MouseButtonKind.Middle => MOUSEEVENTF_MIDDLEDOWN,
            _ => MOUSEEVENTF_XDOWN
        };
        if (button is MouseButtonKind.X1 or MouseButtonKind.X2)
        {
            var input = CreateMouseInputEvent(
                0,
                0,
                button is MouseButtonKind.X2 ? 2u : 1u,
                flag,
                layout,
                x,
                y,
                        useAbsolute: true);
            SendInputChecked(input);
            return;
        }
        SendMouseFlag(flag);
    }

    public static void MouseUp(MouseButtonKind button, MacroDisplayLayout layout, int x, int y)
    {
        MoveMouseTo(x, y, layout);
        uint flag = button switch
        {
            MouseButtonKind.Left => MOUSEEVENTF_LEFTUP,
            MouseButtonKind.Right => MOUSEEVENTF_RIGHTUP,
            MouseButtonKind.Middle => MOUSEEVENTF_MIDDLEUP,
            _ => MOUSEEVENTF_XUP
        };
        if (button is MouseButtonKind.X1 or MouseButtonKind.X2)
        {
            var input = CreateMouseInputEvent(
                0,
                0,
                button is MouseButtonKind.X2 ? 2u : 1u,
                flag,
                layout,
                x,
                y,
                useAbsolute: true);
            SendInputChecked(input);
            return;
        }

        SendMouseFlag(flag);
    }

    public static void MouseMove(int x, int y, MacroDisplayLayout layout)
    {
        MoveMouseTo(x, y, layout);
    }

    public static void MouseWheel(int delta, MacroDisplayLayout layout, int x, int y)
    {
        MoveMouseTo(x, y, layout);
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = 0,
                    dy = 0,
                    mouseData = delta,
                    dwFlags = MOUSEEVENTF_WHEEL,
                    time = 0,
                    dwExtraInfo = InputSyntheticMarker.Value
                }
            }
        };
        SendInputChecked(input);
    }

    public static void MouseHorizontalWheel(int delta, MacroDisplayLayout layout, int x, int y)
    {
        MoveMouseTo(x, y, layout);
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = 0,
                    dy = 0,
                    mouseData = delta,
                    dwFlags = MOUSEEVENTF_HWHEEL,
                    time = 0,
                    dwExtraInfo = InputSyntheticMarker.Value
                }
            }
        };
        SendInputChecked(input);
    }

    public static void KeyDown(int scanCode, int virtualKey, bool isExtended)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = (ushort)virtualKey,
                    wScan = (ushort)scanCode,
                    dwFlags = KEYEVENTF_SCANCODE | (isExtended ? KEYEVENTF_EXTENDEDKEY : 0),
                    time = 0,
                    dwExtraInfo = InputSyntheticMarker.Value
                }
            }
        };
        SendInputChecked(input);
    }

    public static void KeyUp(int scanCode, int virtualKey, bool isExtended)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = (ushort)virtualKey,
                    wScan = (ushort)scanCode,
                    dwFlags = KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP | (isExtended ? KEYEVENTF_EXTENDEDKEY : 0),
                    time = 0,
                    dwExtraInfo = InputSyntheticMarker.Value
                }
            }
        };
        SendInputChecked(input);
    }

    public static void ReleaseKey(int scanCode, int virtualKey, bool isExtended)
    {
        KeyUp(scanCode, virtualKey, isExtended);
    }

    public static void ReleaseMouseButton(MouseButtonKind button)
    {
        switch (button)
        {
            case MouseButtonKind.Left:
                SendMouseFlag(MOUSEEVENTF_LEFTUP);
                break;
            case MouseButtonKind.Right:
                SendMouseFlag(MOUSEEVENTF_RIGHTUP);
                break;
            case MouseButtonKind.Middle:
                SendMouseFlag(MOUSEEVENTF_MIDDLEUP);
                break;
                case MouseButtonKind.X1:
                {
                    var input = CreateMouseInputEvent(0, 0, 1, MOUSEEVENTF_XUP, null, 0, 0, useAbsolute: false);
                    SendInputChecked(input);
                    break;
                }
            case MouseButtonKind.X2:
                {
                    var input = CreateMouseInputEvent(0, 0, 2, MOUSEEVENTF_XUP, null, 0, 0, useAbsolute: false);
                    SendInputChecked(input);
                    break;
                }
            default:
                SendMouseFlag(MOUSEEVENTF_LEFTUP);
                break;
        }
    }

    private static INPUT CreateMouseInputEvent(
        int dx,
        int dy,
        uint mouseData,
        uint flag,
        MacroDisplayLayout? layout,
        int x,
        int y,
        bool useAbsolute = true)
    {
        var absoluteX = dx;
        var absoluteY = dy;
        if (useAbsolute && layout is not null)
        {
            absoluteX = NormalizeToAbsoluteX(x, layout);
            absoluteY = NormalizeToAbsoluteY(y, layout);
        }

        var flags = flag | (useAbsolute ? (MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK) : 0);

        return new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = absoluteX,
                    dy = absoluteY,
                    mouseData = (int)mouseData,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = InputSyntheticMarker.Value
                }
            }
        };
    }

    public static void SendEvent(MacroEventRecord evt, MacroDisplayLayout layout)
    {
        if (evt.Type == MacroEventKind.KeyDown)
        {
            if (evt.ScanCode.HasValue)
            {
                KeyDown(evt.ScanCode.Value, evt.VirtualKey ?? 0, evt.IsExtended);
            }
            return;
        }

        if (evt.Type == MacroEventKind.KeyUp)
        {
            if (evt.ScanCode.HasValue)
            {
                KeyUp(evt.ScanCode.Value, evt.VirtualKey ?? 0, evt.IsExtended);
            }
            return;
        }

        if (evt.X is null || evt.Y is null)
        {
            return;
        }

        int x = evt.X.Value;
        int y = evt.Y.Value;

        switch (evt.Type)
        {
            case MacroEventKind.MouseMove:
                MouseMove(x, y, layout);
                break;
            case MacroEventKind.MouseDown:
                MouseDown(evt.MouseButton ?? MouseButtonKind.Left, layout, x, y);
                break;
            case MacroEventKind.MouseUp:
                MouseUp(evt.MouseButton ?? MouseButtonKind.Left, layout, x, y);
                break;
            case MacroEventKind.MouseWheel:
                MouseWheel(evt.WheelDelta ?? 0, layout, x, y);
                break;
            case MacroEventKind.MouseHorizontalWheel:
                MouseHorizontalWheel(evt.WheelDelta ?? 0, layout, x, y);
                break;
        }
    }

    public static void SendKeyUp(MacroEventRecord evt, MacroDisplayLayout layout)
    {
        if (evt.ScanCode.HasValue)
        {
            KeyUp(evt.ScanCode.Value, evt.VirtualKey ?? 0, evt.IsExtended);
        }
    }

    private static void SendMouseFlag(uint flag)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = 0,
                    dy = 0,
                    mouseData = 0,
                    dwFlags = flag,
                    time = 0,
                    dwExtraInfo = InputSyntheticMarker.Value
                }
            }
        };
        SendInputChecked(input);
    }

    private static int NormalizeCoordinate(int value, int min, int size)
    {
        if (size <= 1)
        {
            return 0;
        }

        return AbsoluteDesktopInputContract.NormalizeCoordinate(value, min, size);
    }

    private static void SendInputChecked(INPUT input)
    {
        if (NativeSendInput(1, [input], Marshal.SizeOf<INPUT>()) != 1)
        {
            throw new StandardInputRejectedException(Marshal.GetLastWin32Error());
        }
    }

    [DllImport("user32.dll", EntryPoint = "SendInput", SetLastError = true)]
    private static extern uint NativeSendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public int mouseData;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;
        [FieldOffset(0)]
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }
}

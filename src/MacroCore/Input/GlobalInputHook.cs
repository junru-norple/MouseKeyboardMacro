using System.Diagnostics;
using System.Runtime.InteropServices;
using MacroCore.Models;

namespace MacroCore.Input;

public sealed class GlobalInputHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WmMouseMove = 0x0200;
    private const int WmLeftDown = 0x0201;
    private const int WmLeftUp = 0x0202;
    private const int WmRightDown = 0x0204;
    private const int WmRightUp = 0x0205;
    private const int WmMiddleDown = 0x0207;
    private const int WmMiddleUp = 0x0208;
    private const int WmWheel = 0x020A;
    private const int WmXDown = 0x020B;
    private const int WmXUp = 0x020C;
    private const int WmHorizontalWheel = 0x020E;
    private const int XButton2 = 2;

    private readonly object _sync = new();
    private readonly LowLevelProc _keyboardProc;
    private readonly LowLevelProc _mouseProc;
    private readonly IntPtr _moduleHandle;
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;

    public Func<HookEvent, bool>? TryEnqueue { get; set; }
    public HookSuppressionMode SuppressionMode { get; set; }
    public bool IsKeyboardHookActive => _keyboardHook != IntPtr.Zero;
    public bool IsMouseHookActive => _mouseHook != IntPtr.Zero;
    public int KeyboardHookError { get; private set; }
    public int MouseHookError { get; private set; }
    public long SlowCallbackCount => Interlocked.Read(ref _slowCallbackCount);
    public long RejectedEventCount => Interlocked.Read(ref _rejectedEventCount);
    public long CallbackExceptionCount => Interlocked.Read(ref _callbackExceptionCount);

    private long _slowCallbackCount;
    private long _rejectedEventCount;
    private long _callbackExceptionCount;

    public GlobalInputHook()
    {
        _keyboardProc = KeyboardProc;
        _mouseProc = MouseProc;
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        _moduleHandle = GetModuleHandle(module?.ModuleName ?? string.Empty);
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_keyboardHook == IntPtr.Zero)
            {
                _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, _moduleHandle, 0);
                KeyboardHookError = _keyboardHook == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
            }
            if (_mouseHook == IntPtr.Zero)
            {
                _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, _moduleHandle, 0);
                MouseHookError = _mouseHook == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
            }
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (_keyboardHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = IntPtr.Zero;
            }
            if (_mouseHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
            }
        }
    }

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            if (nCode >= 0)
            {
                var message = wParam.ToInt32();
                if (message is WmKeyDown or WmKeyUp or WmSysKeyDown or WmSysKeyUp)
                {
                    var data = Marshal.PtrToStructure<KeyboardHookData>(lParam);
                    var input = new HookEvent
                    {
                        Source = HookSource.Keyboard,
                        Message = message,
                        TimestampMs = Environment.TickCount64,
                        VirtualKey = (int)data.VirtualKey,
                        ScanCode = (int)data.ScanCode,
                        IsExtended = (data.Flags & 0x01) != 0,
                        IsE0 = (data.Flags & 0x01) != 0,
                        IsE1 = data.VirtualKey == 0x13,
                        IsInjected = (data.Flags & 0x10) != 0,
                        IsLowerIntegrityInjected = (data.Flags & 0x02) != 0,
                        ExtraInfo = data.ExtraInfo.ToUInt64(),
                        IsOwnSyntheticInput = InputSyntheticMarker.IsOwn(data.ExtraInfo)
                    };
                    var result = HookCallbackSafety.Dispatch(input, SuppressionMode, TryEnqueue);
                    if (!result.Enqueued)
                    {
                        Interlocked.Increment(ref _rejectedEventCount);
                    }
                    if (result.Suppressed)
                    {
                        return (IntPtr)1;
                    }
                }
            }
        }
        catch
        {
            Interlocked.Increment(ref _callbackExceptionCount);
        }
        finally
        {
            if (Stopwatch.GetElapsedTime(started).TotalMilliseconds > 2)
            {
                Interlocked.Increment(ref _slowCallbackCount);
            }
        }
        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            if (nCode >= 0)
            {
                var message = wParam.ToInt32();
                if (IsSupportedMouseMessage(message))
                {
                    var data = Marshal.PtrToStructure<MouseHookData>(lParam);
                    var input = new HookEvent
                    {
                        Source = HookSource.Mouse,
                        Message = message,
                        TimestampMs = Environment.TickCount64,
                        MouseX = data.Point.X,
                        MouseY = data.Point.Y,
                        MouseData = unchecked((int)data.MouseData),
                        IsInjected = (data.Flags & 0x01) != 0,
                        IsLowerIntegrityInjected = (data.Flags & 0x02) != 0,
                        ExtraInfo = data.ExtraInfo.ToUInt64(),
                        IsOwnSyntheticInput = InputSyntheticMarker.IsOwn(data.ExtraInfo),
                        IsMouseMove = message == WmMouseMove,
                        MouseButton = ResolveButton(message, data.MouseData)
                    };
                    var result = HookCallbackSafety.Dispatch(input, HookSuppressionMode.None, TryEnqueue);
                    if (!result.Enqueued)
                    {
                        Interlocked.Increment(ref _rejectedEventCount);
                    }
                }
            }
        }
        catch
        {
            Interlocked.Increment(ref _callbackExceptionCount);
        }
        finally
        {
            if (Stopwatch.GetElapsedTime(started).TotalMilliseconds > 2)
            {
                Interlocked.Increment(ref _slowCallbackCount);
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private static bool IsSupportedMouseMessage(int message) => message is
        WmMouseMove or WmLeftDown or WmLeftUp or WmRightDown or WmRightUp or
        WmMiddleDown or WmMiddleUp or WmWheel or WmXDown or WmXUp or WmHorizontalWheel;

    private static MouseButtonKind? ResolveButton(int message, uint mouseData) => message switch
    {
        WmLeftDown or WmLeftUp => MouseButtonKind.Left,
        WmRightDown or WmRightUp => MouseButtonKind.Right,
        WmMiddleDown or WmMiddleUp => MouseButtonKind.Middle,
        WmXDown or WmXUp => ((mouseData >> 16) & 0xFFFF) == XButton2 ? MouseButtonKind.X2 : MouseButtonKind.X1,
        _ => null
    };

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    private delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string moduleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardHookData
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookData
    {
        public NativePoint Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }
}

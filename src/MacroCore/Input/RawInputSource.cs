using System.Buffers;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using MacroCore.Models;

namespace MacroCore.Input;

public static class RawInputConstants
{
    public const uint RidevRemove = 0x00000001;
    public const uint RidevInputSink = 0x00000100;
    public const ushort UsagePageGenericDesktop = 0x01;
    public const ushort UsageMouse = 0x02;
    public const ushort UsageKeyboard = 0x06;
    public const ushort RiKeyBreak = 0x0001;
    public const ushort RiKeyE0 = 0x0002;
    public const ushort RiKeyE1 = 0x0004;
    public const ushort MouseMoveAbsolute = 0x0001;
    public const ushort MouseVirtualDesktop = 0x0002;
    public const ushort LeftDown = 0x0001;
    public const ushort LeftUp = 0x0002;
    public const ushort RightDown = 0x0004;
    public const ushort RightUp = 0x0008;
    public const ushort MiddleDown = 0x0010;
    public const ushort MiddleUp = 0x0020;
    public const ushort X1Down = 0x0040;
    public const ushort X1Up = 0x0080;
    public const ushort X2Down = 0x0100;
    public const ushort X2Up = 0x0200;
    public const ushort Wheel = 0x0400;
    public const ushort HorizontalWheel = 0x0800;
}

public readonly record struct RawInputDeviceDescriptor(ushort UsagePage, ushort Usage, uint Flags, IntPtr TargetWindow);
public readonly record struct RawInputRegistrationResult(bool Success, bool KeyboardRegistered, bool MouseRegistered, int ErrorCode)
{
    public int KeyboardErrorCode { get; init; }
    public int MouseErrorCode { get; init; }
}
public readonly record struct RawInputLayoutSizes(int Header, int Keyboard, int Mouse, int Device);

public interface IRawInputRegistrar
{
    bool Register(IReadOnlyList<RawInputDeviceDescriptor> devices, out int errorCode);
}

public sealed class RawInputRegistrationService
{
    private readonly IRawInputRegistrar _registrar;

    public RawInputRegistrationService(IRawInputRegistrar registrar)
    {
        _registrar = registrar;
    }

    public static RawInputDeviceDescriptor[] CreateRegistrationDevices(IntPtr targetWindow) =>
    [
        new(RawInputConstants.UsagePageGenericDesktop, RawInputConstants.UsageKeyboard, RawInputConstants.RidevInputSink, targetWindow),
        new(RawInputConstants.UsagePageGenericDesktop, RawInputConstants.UsageMouse, RawInputConstants.RidevInputSink, targetWindow)
    ];

    public static RawInputDeviceDescriptor[] CreateRemovalDevices() =>
    [
        new(RawInputConstants.UsagePageGenericDesktop, RawInputConstants.UsageKeyboard, RawInputConstants.RidevRemove, IntPtr.Zero),
        new(RawInputConstants.UsagePageGenericDesktop, RawInputConstants.UsageMouse, RawInputConstants.RidevRemove, IntPtr.Zero)
    ];

    public RawInputRegistrationResult Register(IntPtr targetWindow)
    {
        var devices = CreateRegistrationDevices(targetWindow);
        var keyboard = _registrar.Register([devices[0]], out var keyboardError);
        var mouse = _registrar.Register([devices[1]], out var mouseError);
        return new RawInputRegistrationResult(
            keyboard && mouse,
            keyboard,
            mouse,
            keyboard ? mouseError : keyboardError)
        {
            KeyboardErrorCode = keyboardError,
            MouseErrorCode = mouseError
        };
    }

    public void Unregister()
    {
        foreach (var device in CreateRemovalDevices())
        {
            _registrar.Register([device], out _);
        }
    }
}

public readonly record struct RawKeyboardData(ushort MakeCode, ushort Flags, ushort VirtualKey, uint NativeMessage);
public readonly record struct RawMouseData(
    ushort Flags,
    ushort ButtonFlags,
    ushort ButtonData,
    int LastX,
    int LastY,
    int CursorX,
    int CursorY,
    bool CursorPositionAvailable = true,
    bool RelativeDeltaAvailable = true);

public static class RawInputEventTranslator
{
    public static HookEvent? TranslateKeyboard(RawKeyboardData data, long timestampMs)
    {
        if (data.VirtualKey == 0xFF)
        {
            return null;
        }

        var isBreak = (data.Flags & RawInputConstants.RiKeyBreak) != 0;
        return new HookEvent
        {
            Source = HookSource.RawKeyboard,
            Message = isBreak ? 0x0101 : 0x0100,
            TimestampMs = timestampMs,
            VirtualKey = data.VirtualKey,
            ScanCode = data.MakeCode,
            IsExtended = (data.Flags & (RawInputConstants.RiKeyE0 | RawInputConstants.RiKeyE1)) != 0,
            IsE0 = (data.Flags & RawInputConstants.RiKeyE0) != 0,
            IsE1 = (data.Flags & RawInputConstants.RiKeyE1) != 0
        };
    }

    public static IReadOnlyList<HookEvent> TranslateMouse(RawMouseData data, long timestampMs)
    {
        var events = new List<HookEvent>();
        var absolute = (data.Flags & RawInputConstants.MouseMoveAbsolute) != 0;
        if (absolute || data.LastX != 0 || data.LastY != 0)
        {
            events.Add(new HookEvent
            {
                Source = HookSource.RawMouse,
                Message = 0x0200,
                TimestampMs = timestampMs,
                MouseX = data.CursorX,
                MouseY = data.CursorY,
                DeltaX = absolute ? 0 : data.LastX,
                DeltaY = absolute ? 0 : data.LastY,
                IsMouseMove = true,
                IsAbsoluteMouse = absolute,
                HasAbsoluteMousePosition = data.CursorPositionAvailable,
                HasRelativeMouseDelta = !absolute && data.RelativeDeltaAvailable
            });
        }

        AddButton(events, data, timestampMs, RawInputConstants.LeftDown, 0x0201, MouseButtonKind.Left);
        AddButton(events, data, timestampMs, RawInputConstants.LeftUp, 0x0202, MouseButtonKind.Left);
        AddButton(events, data, timestampMs, RawInputConstants.RightDown, 0x0204, MouseButtonKind.Right);
        AddButton(events, data, timestampMs, RawInputConstants.RightUp, 0x0205, MouseButtonKind.Right);
        AddButton(events, data, timestampMs, RawInputConstants.MiddleDown, 0x0207, MouseButtonKind.Middle);
        AddButton(events, data, timestampMs, RawInputConstants.MiddleUp, 0x0208, MouseButtonKind.Middle);
        AddButton(events, data, timestampMs, RawInputConstants.X1Down, 0x020B, MouseButtonKind.X1);
        AddButton(events, data, timestampMs, RawInputConstants.X1Up, 0x020C, MouseButtonKind.X1);
        AddButton(events, data, timestampMs, RawInputConstants.X2Down, 0x020B, MouseButtonKind.X2);
        AddButton(events, data, timestampMs, RawInputConstants.X2Up, 0x020C, MouseButtonKind.X2);

        if ((data.ButtonFlags & RawInputConstants.Wheel) != 0)
        {
            events.Add(CreateWheel(data, timestampMs, 0x020A));
        }
        if ((data.ButtonFlags & RawInputConstants.HorizontalWheel) != 0)
        {
            events.Add(CreateWheel(data, timestampMs, 0x020E));
        }

        return events;
    }

    private static void AddButton(List<HookEvent> events, RawMouseData data, long timestampMs, ushort flag, int message, MouseButtonKind button)
    {
        if ((data.ButtonFlags & flag) == 0)
        {
            return;
        }

        events.Add(new HookEvent
        {
            Source = HookSource.RawMouse,
            Message = message,
            TimestampMs = timestampMs,
            MouseX = data.CursorX,
            MouseY = data.CursorY,
            MouseButton = button,
            HasAbsoluteMousePosition = data.CursorPositionAvailable
        });
    }

    private static HookEvent CreateWheel(RawMouseData data, long timestampMs, int message) => new()
    {
        Source = HookSource.RawMouse,
        Message = message,
        TimestampMs = timestampMs,
        MouseX = data.CursorX,
        MouseY = data.CursorY,
        MouseData = unchecked((int)((uint)data.ButtonData << 16)),
        HasAbsoluteMousePosition = data.CursorPositionAvailable
    };
}

public sealed class RawInputSource : NativeWindow, IDisposable
{
    private const int WmInput = 0x00FF;
    private static readonly IntPtr HwndMessage = new(-3);
    private readonly RawInputRegistrationService _registration;
    private bool _registered;
    private bool _disposed;

    public Func<HookEvent, bool>? TryEnqueue { get; set; }
    public RawInputRegistrationResult RegistrationResult { get; private set; }
    public int LastReadError { get; private set; }
    public long RejectedEventCount => Interlocked.Read(ref _rejectedEventCount);
    public bool IsRegistered => _registered;
    private long _rejectedEventCount;

    public RawInputSource()
        : this(new RawInputRegistrationService(new Win32RawInputRegistrar()))
    {
    }

    public RawInputSource(RawInputRegistrationService registration)
    {
        _registration = registration;
    }

    public RawInputRegistrationResult Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_registered)
        {
            return RegistrationResult;
        }

        CreateHandle(new CreateParams
        {
            Caption = "MouseKeyboardMacro.RawInputSink",
            Parent = HwndMessage
        });
        RegistrationResult = _registration.Register(Handle);
        _registered = RegistrationResult.KeyboardRegistered || RegistrationResult.MouseRegistered;
        if (!RegistrationResult.Success && _registered)
        {
            _registration.Unregister();
            _registered = false;
        }
        if (!_registered && Handle != IntPtr.Zero)
        {
            DestroyHandle();
        }
        return RegistrationResult;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmInput)
        {
            if (RawInputNativeReader.TryRead(m.LParam, out var keyboard, out var mouse, out var error))
            {
                var timestamp = Environment.TickCount64;
                if (keyboard.HasValue)
                {
                    var translated = RawInputEventTranslator.TranslateKeyboard(keyboard.Value, timestamp);
                    if (translated is not null)
                    {
                        EnqueueNonBlocking(translated);
                    }
                }
                if (mouse.HasValue)
                {
                    foreach (var translated in RawInputEventTranslator.TranslateMouse(mouse.Value, timestamp))
                    {
                        EnqueueNonBlocking(translated);
                    }
                }
            }
            else
            {
                LastReadError = error;
            }
        }

        base.WndProc(ref m);
    }

    private void EnqueueNonBlocking(HookEvent input)
    {
        try
        {
            if (TryEnqueue?.Invoke(input) != true)
            {
                Interlocked.Increment(ref _rejectedEventCount);
            }
        }
        catch
        {
            Interlocked.Increment(ref _rejectedEventCount);
        }
    }

    public void Stop()
    {
        if (_registered)
        {
            _registration.Unregister();
            _registered = false;
        }
        if (Handle != IntPtr.Zero)
        {
            DestroyHandle();
        }
        RegistrationResult = default;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

public static class RawInputNativeLayout
{
    public static RawInputLayoutSizes Current => new(
        Marshal.SizeOf<RawInputHeader>(),
        Marshal.SizeOf<RawKeyboardNative>(),
        Marshal.SizeOf<RawMouseNative>(),
        Marshal.SizeOf<RawInputDeviceNative>());
}

internal sealed class Win32RawInputRegistrar : IRawInputRegistrar
{
    public bool Register(IReadOnlyList<RawInputDeviceDescriptor> devices, out int errorCode)
    {
        var native = devices.Select(device => new RawInputDeviceNative
        {
            UsagePage = device.UsagePage,
            Usage = device.Usage,
            Flags = device.Flags,
            TargetWindow = device.TargetWindow
        }).ToArray();
        var success = RegisterRawInputDevices(native, (uint)native.Length, (uint)Marshal.SizeOf<RawInputDeviceNative>());
        errorCode = success ? 0 : Marshal.GetLastWin32Error();
        return success;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices([In] RawInputDeviceNative[] devices, uint deviceCount, uint size);
}

internal static class RawInputNativeReader
{
    private const uint RidInput = 0x10000003;
    private const uint RimTypeMouse = 0;
    private const uint RimTypeKeyboard = 1;

    public static bool TryRead(IntPtr rawHandle, out RawKeyboardData? keyboard, out RawMouseData? mouse, out int error)
    {
        keyboard = null;
        mouse = null;
        error = 0;
        uint size = 0;
        var headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
        if (GetRawInputData(rawHandle, RidInput, IntPtr.Zero, ref size, headerSize) == uint.MaxValue || size < headerSize)
        {
            error = Marshal.GetLastWin32Error();
            return false;
        }

        var buffer = ArrayPool<byte>.Shared.Rent((int)size);
        var pinned = default(GCHandle);
        try
        {
            pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            var pointer = pinned.AddrOfPinnedObject();
            var readSize = size;
            if (GetRawInputData(rawHandle, RidInput, pointer, ref readSize, headerSize) == uint.MaxValue)
            {
                error = Marshal.GetLastWin32Error();
                return false;
            }

            var header = Marshal.PtrToStructure<RawInputHeader>(pointer);
            var payload = IntPtr.Add(pointer, (int)headerSize);
            if (header.Type == RimTypeKeyboard)
            {
                var native = Marshal.PtrToStructure<RawKeyboardNative>(payload);
                keyboard = new RawKeyboardData(native.MakeCode, native.Flags, native.VirtualKey, native.Message);
                return true;
            }

            if (header.Type == RimTypeMouse)
            {
                var native = Marshal.PtrToStructure<RawMouseNative>(payload);
                var cursorX = 0;
                var cursorY = 0;
                var cursorPositionAvailable = false;
                if ((native.Flags & RawInputConstants.MouseMoveAbsolute) != 0)
                {
                    var virtualDesktop = (native.Flags & RawInputConstants.MouseVirtualDesktop) != 0;
                    var left = virtualDesktop ? GetSystemMetrics(76) : 0;
                    var top = virtualDesktop ? GetSystemMetrics(77) : 0;
                    var width = Math.Max(1, GetSystemMetrics(virtualDesktop ? 78 : 0));
                    var height = Math.Max(1, GetSystemMetrics(virtualDesktop ? 79 : 1));
                    cursorX = left + (int)Math.Round(native.LastX * (width - 1) / 65535.0);
                    cursorY = top + (int)Math.Round(native.LastY * (height - 1) / 65535.0);
                    cursorPositionAvailable = true;
                }
                else if (GetCursorPos(out var point))
                {
                    cursorX = point.X;
                    cursorY = point.Y;
                    cursorPositionAvailable = true;
                }

                mouse = new RawMouseData(
                    native.Flags,
                    native.ButtonFlags,
                    native.ButtonData,
                    native.LastX,
                    native.LastY,
                    cursorX,
                    cursorY,
                    cursorPositionAvailable,
                    RelativeDeltaAvailable: (native.Flags & RawInputConstants.MouseMoveAbsolute) == 0);
                return true;
            }

            return true;
        }
        finally
        {
            if (pinned.IsAllocated)
            {
                pinned.Free();
            }
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}

[StructLayout(LayoutKind.Sequential)]
internal struct RawInputDeviceNative
{
    public ushort UsagePage;
    public ushort Usage;
    public uint Flags;
    public IntPtr TargetWindow;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RawInputHeader
{
    public uint Type;
    public uint Size;
    public IntPtr Device;
    public IntPtr WParam;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RawKeyboardNative
{
    public ushort MakeCode;
    public ushort Flags;
    public ushort Reserved;
    public ushort VirtualKey;
    public uint Message;
    public uint ExtraInformation;
}

[StructLayout(LayoutKind.Explicit)]
internal struct RawMouseNative
{
    [FieldOffset(0)] public ushort Flags;
    [FieldOffset(4)] public uint Buttons;
    [FieldOffset(4)] public ushort ButtonFlags;
    [FieldOffset(6)] public ushort ButtonData;
    [FieldOffset(8)] public uint RawButtons;
    [FieldOffset(12)] public int LastX;
    [FieldOffset(16)] public int LastY;
    [FieldOffset(20)] public uint ExtraInformation;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePoint
{
    public int X;
    public int Y;
}

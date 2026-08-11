using System.Runtime.InteropServices;
using System.Text;

namespace MacroCore.Security;

public enum InputDesktopState
{
    DefaultDesktop,
    SecureOrAlternateDesktop,
    Unknown
}

public sealed record InputDesktopProbeResult(
    InputDesktopState State,
    string? DesktopName,
    int OpenInputDesktopError,
    int QuerySizeError,
    int QueryNameError,
    string ProbeMethod,
    DateTimeOffset Timestamp)
{
    public bool IsDefaultDesktop => State == InputDesktopState.DefaultDesktop;
}

public interface IInputDesktopProbe
{
    InputDesktopProbeResult Probe();
}

public interface IInputDesktopNative
{
    IntPtr OpenInputDesktop(out int errorCode);
    bool QueryDesktopName(IntPtr desktop, IntPtr buffer, int bufferBytes, out int requiredBytes, out int errorCode);
    void CloseInputDesktop(IntPtr desktop);
}

public static class InputDesktopNameCodec
{
    public static string DecodeUnicode(ReadOnlySpan<byte> bytes)
    {
        if ((bytes.Length & 1) != 0)
        {
            throw new ArgumentException("A Unicode desktop name must contain an even number of bytes.", nameof(bytes));
        }

        return Normalize(Encoding.Unicode.GetString(bytes));
    }

    public static string Normalize(string value) =>
        value.TrimEnd('\0', ' ', '\t', '\r', '\n').TrimStart();
}

public sealed class WindowsInputDesktopProbe : IInputDesktopProbe
{
    public const int ErrorInsufficientBuffer = 122;
    public const string MethodName = "OpenInputDesktop/GetUserObjectInformationW";

    private readonly IInputDesktopNative _native;
    private readonly int _maximumAttempts;
    private readonly TimeSpan _retryDelay;

    public WindowsInputDesktopProbe()
        : this(new WindowsInputDesktopNative(), 3, TimeSpan.FromMilliseconds(20))
    {
    }

    public WindowsInputDesktopProbe(IInputDesktopNative native, int maximumAttempts = 3, TimeSpan? retryDelay = null)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _maximumAttempts = Math.Clamp(maximumAttempts, 1, 3);
        _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(20);
    }

    public InputDesktopProbeResult Probe()
    {
        InputDesktopProbeResult result = Unknown(openError: 0, sizeError: 0, nameError: 0);
        for (var attempt = 1; attempt <= _maximumAttempts; attempt++)
        {
            result = ProbeOnce();
            if (result.State != InputDesktopState.Unknown || attempt == _maximumAttempts)
            {
                return result;
            }

            if (_retryDelay > TimeSpan.Zero)
            {
                Thread.Sleep(_retryDelay);
            }
        }

        return result;
    }

    private InputDesktopProbeResult ProbeOnce()
    {
        var desktop = _native.OpenInputDesktop(out var openError);
        if (desktop == IntPtr.Zero)
        {
            return Unknown(openError, sizeError: 0, nameError: 0);
        }

        try
        {
            var sizeSucceeded = _native.QueryDesktopName(desktop, IntPtr.Zero, 0, out var requiredBytes, out var sizeError);
            if ((!sizeSucceeded && sizeError != ErrorInsufficientBuffer) || requiredBytes < sizeof(char))
            {
                return Unknown(openError, sizeError, nameError: 0);
            }

            var buffer = Marshal.AllocHGlobal(requiredBytes);
            try
            {
                if (!_native.QueryDesktopName(desktop, buffer, requiredBytes, out _, out var nameError))
                {
                    return Unknown(openError, sizeError, nameError);
                }

                var bytes = new byte[requiredBytes];
                Marshal.Copy(buffer, bytes, 0, bytes.Length);
                var desktopName = InputDesktopNameCodec.DecodeUnicode(bytes);
                if (string.IsNullOrWhiteSpace(desktopName))
                {
                    return Unknown(openError, sizeError, nameError: 0);
                }

                var state = string.Equals(desktopName, "Default", StringComparison.OrdinalIgnoreCase)
                    ? InputDesktopState.DefaultDesktop
                    : InputDesktopState.SecureOrAlternateDesktop;
                return new InputDesktopProbeResult(
                    state,
                    desktopName,
                    openError,
                    sizeError,
                    0,
                    MethodName,
                    DateTimeOffset.UtcNow);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            _native.CloseInputDesktop(desktop);
        }
    }

    private static InputDesktopProbeResult Unknown(int openError, int sizeError, int nameError) =>
        new(
            InputDesktopState.Unknown,
            null,
            openError,
            sizeError,
            nameError,
            MethodName,
            DateTimeOffset.UtcNow);
}

public sealed class WindowsInputDesktopNative : IInputDesktopNative
{
    private const uint DesktopReadObjects = 0x0001;
    private const int UoiName = 2;

    public IntPtr OpenInputDesktop(out int errorCode)
    {
        var desktop = OpenInputDesktopNative(0, false, DesktopReadObjects);
        errorCode = desktop == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
        return desktop;
    }

    public bool QueryDesktopName(IntPtr desktop, IntPtr buffer, int bufferBytes, out int requiredBytes, out int errorCode)
    {
        var success = GetUserObjectInformationW(desktop, UoiName, buffer, bufferBytes, out requiredBytes);
        errorCode = success ? 0 : Marshal.GetLastWin32Error();
        return success;
    }

    public void CloseInputDesktop(IntPtr desktop) => _ = CloseDesktop(desktop);

    [DllImport("user32.dll", EntryPoint = "OpenInputDesktop", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr OpenInputDesktopNative(
        uint flags,
        [MarshalAs(UnmanagedType.Bool)] bool inherit,
        uint desiredAccess);

    [DllImport(
        "user32.dll",
        EntryPoint = "GetUserObjectInformationW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformationW(
        IntPtr handle,
        int index,
        IntPtr information,
        int length,
        out int needed);

    [DllImport("user32.dll", EntryPoint = "CloseDesktop", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);
}

public sealed class RecordingStartPrivilegeEvaluator
{
    private readonly IWindowsPrivilegeService _privilegeService;
    private readonly IInputDesktopProbe _desktopProbe;

    public RecordingStartPrivilegeEvaluator(IWindowsPrivilegeService privilegeService, IInputDesktopProbe desktopProbe)
    {
        _privilegeService = privilegeService ?? throw new ArgumentNullException(nameof(privilegeService));
        _desktopProbe = desktopProbe ?? throw new ArgumentNullException(nameof(desktopProbe));
    }

    public RecordingStartEvaluation Evaluate()
    {
        var desktop = _desktopProbe.Probe();
        var privilege = _privilegeService.CaptureForeground(desktop);
        var decision = PrivilegePolicy.EvaluateRecordingStart(privilege.RecorderIntegrity, privilege.TargetIntegrity, desktop.State);
        return new RecordingStartEvaluation(desktop, privilege, decision, PrivilegePolicy.GetRecordingBlockMessage(decision, desktop));
    }
}

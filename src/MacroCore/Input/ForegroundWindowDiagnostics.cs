using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using MacroCore.Models;

namespace MacroCore.Input;

public sealed record ForegroundWindowDiagnostic(
    IntPtr WindowHandle,
    int ProcessId,
    string ProcessName,
    string WindowTitle,
    ProcessIntegrityKind ProcessIntegrity,
    ProcessIntegrityKind RecorderIntegrity,
    bool CoversMonitorBounds,
    bool LikelyBorderlessFullscreen,
    bool LikelyExclusiveFullscreen)
{
    public bool PermissionMismatch => IntegrityRank(ProcessIntegrity) > IntegrityRank(RecorderIntegrity);

    public static ForegroundWindowDiagnostic Empty(ProcessIntegrityKind recorderIntegrity) =>
        new(IntPtr.Zero, 0, string.Empty, string.Empty, ProcessIntegrityKind.Unknown, recorderIntegrity, false, false, false);

    private static int IntegrityRank(ProcessIntegrityKind value) => value switch
    {
        ProcessIntegrityKind.Low => 1,
        ProcessIntegrityKind.Medium => 2,
        ProcessIntegrityKind.High => 3,
        ProcessIntegrityKind.System => 4,
        _ => 0
    };
}

public static class ForegroundWindowDiagnosticProvider
{
    private const int GwlStyle = -16;
    private const long WsOverlappedWindow = 0x00CF0000L;

    public static ForegroundWindowDiagnostic Capture()
    {
        var recorderIntegrity = GetIntegrity(Process.GetCurrentProcess());
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return ForegroundWindowDiagnostic.Empty(recorderIntegrity);
        }

        GetWindowThreadProcessId(hwnd, out var processId);
        var processName = string.Empty;
        var processIntegrity = ProcessIntegrityKind.Unknown;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            processName = process.ProcessName;
            processIntegrity = GetIntegrity(process);
        }
        catch
        {
            // Protected processes can deny metadata access; retain an explicit Unknown value.
        }

        var titleLength = Math.Min(1024, Math.Max(0, GetWindowTextLength(hwnd)) + 1);
        var title = string.Empty;
        if (titleLength > 1)
        {
            var buffer = new StringBuilder(titleLength);
            GetWindowText(hwnd, buffer, buffer.Capacity);
            title = buffer.ToString();
        }

        var coversMonitor = false;
        if (GetWindowRect(hwnd, out var rect))
        {
            var bounds = Screen.FromHandle(hwnd).Bounds;
            coversMonitor = Math.Abs(rect.Left - bounds.Left) <= 2 &&
                            Math.Abs(rect.Top - bounds.Top) <= 2 &&
                            Math.Abs(rect.Right - bounds.Right) <= 2 &&
                            Math.Abs(rect.Bottom - bounds.Bottom) <= 2;
        }

        var style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
        var borderless = coversMonitor && (style & WsOverlappedWindow) == 0;
        var exclusiveInference = coversMonitor && !borderless;
        return new ForegroundWindowDiagnostic(
            hwnd,
            (int)processId,
            processName,
            title,
            processIntegrity,
            recorderIntegrity,
            coversMonitor,
            borderless,
            exclusiveInference);
    }

    public static ProcessIntegrityKind GetCurrentIntegrity() => GetIntegrity(Process.GetCurrentProcess());

    private static ProcessIntegrityKind GetIntegrity(Process process)
    {
        const uint tokenQuery = 0x0008;
        const int tokenIntegrityLevel = 25;
        if (!OpenProcessToken(process.Handle, tokenQuery, out var token))
        {
            return ProcessIntegrityKind.Unknown;
        }

        try
        {
            GetTokenInformation(token, tokenIntegrityLevel, IntPtr.Zero, 0, out var length);
            if (length <= 0)
            {
                return ProcessIntegrityKind.Unknown;
            }

            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!GetTokenInformation(token, tokenIntegrityLevel, buffer, length, out _))
                {
                    return ProcessIntegrityKind.Unknown;
                }

                var sid = Marshal.ReadIntPtr(buffer);
                var countPointer = GetSidSubAuthorityCount(sid);
                if (countPointer == IntPtr.Zero)
                {
                    return ProcessIntegrityKind.Unknown;
                }

                var count = Marshal.ReadByte(countPointer);
                if (count == 0)
                {
                    return ProcessIntegrityKind.Unknown;
                }

                var ridPointer = GetSidSubAuthority(sid, (uint)(count - 1));
                var rid = ridPointer == IntPtr.Zero ? 0 : Marshal.ReadInt32(ridPointer);
                return rid switch
                {
                    < 0x1000 => ProcessIntegrityKind.Low,
                    < 0x3000 => ProcessIntegrityKind.Medium,
                    < 0x4000 => ProcessIntegrityKind.High,
                    _ => ProcessIntegrityKind.System
                };
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return ProcessIntegrityKind.Unknown;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new IntPtr(GetWindowLong32(hwnd, index));

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int informationClass, IntPtr information, int informationLength, out int returnLength);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint subAuthority);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}

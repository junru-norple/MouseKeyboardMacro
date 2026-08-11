using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MacroCore.Models;
using MacroCore.Runtime;

namespace MacroCore.Security;

public enum WindowsIntegrityLevel
{
    Unknown = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    System = 4
}

public enum MacroToolRole
{
    Recorder,
    Player,
    Emergency
}

public enum RequestedPrivilegeMode
{
    Normal,
    ElevatedRecorder,
    ElevatedPlayer
}

public enum PlaybackPrivilegeRequirement
{
    Unknown,
    Normal,
    Administrator
}

public sealed record MacroToolLaunchOptions(
    MacroToolRole Role,
    RequestedPrivilegeMode RequestedMode,
    string? PreselectedMacroPath)
{
    public bool RequestedElevation => RequestedMode is RequestedPrivilegeMode.ElevatedRecorder or RequestedPrivilegeMode.ElevatedPlayer;

    public static MacroToolLaunchOptions Parse(string[] args, MacroToolRole role)
    {
        var mode = RequestedPrivilegeMode.Normal;
        string? macroPath = null;

        for (var index = 0; index < args.Length; index++)
        {
            if (string.Equals(args[index], "--requested-mode", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                var value = args[++index];
                mode = value.ToLowerInvariant() switch
                {
                    "elevated-recorder" => RequestedPrivilegeMode.ElevatedRecorder,
                    "elevated-player" => RequestedPrivilegeMode.ElevatedPlayer,
                    _ => RequestedPrivilegeMode.Normal
                };
                continue;
            }

            if (string.Equals(args[index], "--preselect", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                macroPath = Path.GetFullPath(args[++index]);
                continue;
            }

            if ((string.Equals(args[index], "--project-root", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(args[index], "--launch-token", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(args[index], "--ready-file", StringComparison.OrdinalIgnoreCase)) && index + 1 < args.Length)
            {
                index++;
                continue;
            }

            if (!args[index].StartsWith("--", StringComparison.Ordinal) &&
                string.Equals(Path.GetExtension(args[index]), ".macro", StringComparison.OrdinalIgnoreCase) &&
                macroPath is null)
            {
                macroPath = Path.GetFullPath(args[index]);
            }
        }

        return new MacroToolLaunchOptions(role, mode, macroPath);
    }
}

public sealed class CompiledMacroLauncherClient
{
    public ElevationLaunchResult LaunchElevatedPlayer(string macroPath, out string? error)
    {
        error = null;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = RuntimeFolders.Launcher,
                WorkingDirectory = Path.GetDirectoryName(RuntimeFolders.Launcher)!,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (string argument in new[]
                     {
                         "--tool", "player",
                         "--mode", "elevated",
                         "--project-root", RuntimeFolders.ProjectRoot,
                         "--preselect", Path.GetFullPath(macroPath)
                     })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using Process? launcher = Process.Start(startInfo);
            if (launcher is null)
            {
                error = "MacroLauncher 無法啟動。";
                return ElevationLaunchResult.Failed;
            }

            if (!launcher.WaitForExit(120_000))
            {
                error = "等待管理員播放器啟動逾時。";
                return ElevationLaunchResult.Failed;
            }

            return launcher.ExitCode switch
            {
                0 => ElevationLaunchResult.Started,
                20 => ElevationLaunchResult.Cancelled,
                _ => Failure($"MacroLauncher 結束碼 {launcher.ExitCode}。", out error)
            };
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return ElevationLaunchResult.Failed;
        }
    }

    private static ElevationLaunchResult Failure(string message, out string? error)
    {
        error = message;
        return ElevationLaunchResult.Failed;
    }
}

public sealed record ForegroundPrivilegeSnapshot(
    bool IsSecureDesktop,
    WindowsIntegrityLevel RecorderIntegrity,
    WindowsIntegrityLevel TargetIntegrity,
    int TargetProcessId,
    string? TargetProcessName,
    string? TargetWindowTitle)
{
    public InputDesktopState DesktopState { get; init; } = IsSecureDesktop
        ? InputDesktopState.SecureOrAlternateDesktop
        : InputDesktopState.DefaultDesktop;

    public InputDesktopProbeResult? DesktopProbe { get; init; }
}

public enum RecordingStartDecision
{
    Allowed,
    SecureOrAlternateDesktop,
    DesktopStateUnknown,
    TargetIntegrityMismatch,
    SystemTargetBlocked
}

public sealed record RecordingStartEvaluation(
    InputDesktopProbeResult DesktopProbe,
    ForegroundPrivilegeSnapshot PrivilegeSnapshot,
    RecordingStartDecision Decision,
    string? UserMessage)
{
    public bool IsAllowed => Decision == RecordingStartDecision.Allowed;
}

public static class PrivilegePolicy
{
    public static bool CanRecord(WindowsIntegrityLevel recorder, WindowsIntegrityLevel target, bool secureDesktop)
        => CanRecord(
            recorder,
            target,
            secureDesktop ? InputDesktopState.SecureOrAlternateDesktop : InputDesktopState.DefaultDesktop);

    public static bool CanRecord(WindowsIntegrityLevel recorder, WindowsIntegrityLevel target, InputDesktopState desktopState)
    {
        if (desktopState != InputDesktopState.DefaultDesktop || target == WindowsIntegrityLevel.System)
        {
            return false;
        }

        if (recorder >= WindowsIntegrityLevel.High)
        {
            return target is WindowsIntegrityLevel.Unknown or WindowsIntegrityLevel.Low or WindowsIntegrityLevel.Medium or WindowsIntegrityLevel.High;
        }

        return target is not WindowsIntegrityLevel.High and not WindowsIntegrityLevel.System;
    }

    public static RecordingStartDecision EvaluateRecordingStart(
        WindowsIntegrityLevel recorder,
        WindowsIntegrityLevel target,
        InputDesktopState desktopState)
    {
        if (desktopState == InputDesktopState.Unknown)
        {
            return RecordingStartDecision.DesktopStateUnknown;
        }
        if (desktopState == InputDesktopState.SecureOrAlternateDesktop)
        {
            return RecordingStartDecision.SecureOrAlternateDesktop;
        }
        if (target == WindowsIntegrityLevel.System)
        {
            return RecordingStartDecision.SystemTargetBlocked;
        }
        return CanRecord(recorder, target, desktopState)
            ? RecordingStartDecision.Allowed
            : RecordingStartDecision.TargetIntegrityMismatch;
    }

    public static string? GetRecordingBlockMessage(RecordingStartDecision decision, InputDesktopProbeResult probe) =>
        decision switch
        {
            RecordingStartDecision.Allowed => null,
            RecordingStartDecision.SecureOrAlternateDesktop =>
                "目前位於安全或非 Default 輸入桌面，無法錄製。請返回一般 Windows 桌面後再試。",
            RecordingStartDecision.DesktopStateUnknown =>
                $"無法確認桌面狀態，為安全起見未開始錄製。請查看 desktop_security_probe.log（OpenInputDesktop={probe.OpenInputDesktopError}，GetUserObjectInformationW size={probe.QuerySizeError}，name={probe.QueryNameError}）。",
            RecordingStartDecision.TargetIntegrityMismatch =>
                "目前目標程式以管理員權限執行。請關閉本次 Recorder，改用『06A_啟動錄製器_管理員模式.cmd』。",
            RecordingStartDecision.SystemTargetBlocked =>
                "系統層級目標不允許錄製。",
            _ => "無法確認錄製權限，未開始錄製。"
        };

    public static PlaybackPrivilegeRequirement GetPlaybackRequirement(MacroFile macro)
    {
        if (string.Equals(macro.SchemaVersion, "1.0", StringComparison.Ordinal))
        {
            return PlaybackPrivilegeRequirement.Unknown;
        }

        return macro.CaptureMetadata?.RequiresElevationForPlayback switch
        {
            true => PlaybackPrivilegeRequirement.Administrator,
            false => PlaybackPrivilegeRequirement.Normal,
            _ => PlaybackPrivilegeRequirement.Unknown
        };
    }

    public static bool CanPlay(WindowsIntegrityLevel player, PlaybackPrivilegeRequirement requirement) =>
        requirement != PlaybackPrivilegeRequirement.Administrator || player >= WindowsIntegrityLevel.High;
}

public interface IWindowsPrivilegeService
{
    WindowsIntegrityLevel GetCurrentIntegrity();
    ForegroundPrivilegeSnapshot CaptureForeground();
    ForegroundPrivilegeSnapshot CaptureForeground(InputDesktopProbeResult desktopProbe) => CaptureForeground();
}

public sealed class WindowsPrivilegeService : IWindowsPrivilegeService
{
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private readonly IInputDesktopProbe _inputDesktopProbe;

    public WindowsPrivilegeService()
        : this(new WindowsInputDesktopProbe())
    {
    }

    public WindowsPrivilegeService(IInputDesktopProbe inputDesktopProbe)
    {
        _inputDesktopProbe = inputDesktopProbe ?? throw new ArgumentNullException(nameof(inputDesktopProbe));
    }

    public WindowsIntegrityLevel GetCurrentIntegrity() => GetProcessIntegrity(Environment.ProcessId);

    public ForegroundPrivilegeSnapshot CaptureForeground() => CaptureForeground(_inputDesktopProbe.Probe());

    public ForegroundPrivilegeSnapshot CaptureForeground(InputDesktopProbeResult desktopProbe)
    {
        var recorder = GetCurrentIntegrity();
        if (desktopProbe.State != InputDesktopState.DefaultDesktop)
        {
            return WithDesktop(
                new ForegroundPrivilegeSnapshot(
                    desktopProbe.State == InputDesktopState.SecureOrAlternateDesktop,
                    recorder,
                    WindowsIntegrityLevel.Unknown,
                    0,
                    null,
                    null),
                desktopProbe);
        }

        var window = GetForegroundWindow();
        if (window == IntPtr.Zero)
        {
            return WithDesktop(
                new ForegroundPrivilegeSnapshot(false, recorder, WindowsIntegrityLevel.Unknown, 0, null, null),
                desktopProbe);
        }

        _ = GetWindowThreadProcessId(window, out var pid);
        var titleBuffer = new char[257];
        var titleLength = GetWindowText(window, titleBuffer, titleBuffer.Length);
        var title = titleLength > 0 ? new string(titleBuffer, 0, Math.Min(titleLength, 256)) : null;
        string? processName = null;
        try
        {
            using var process = Process.GetProcessById(unchecked((int)pid));
            processName = Path.GetFileName(process.ProcessName + ".exe");
        }
        catch
        {
        }

        return WithDesktop(
            new ForegroundPrivilegeSnapshot(false, recorder, GetProcessIntegrity(unchecked((int)pid)), unchecked((int)pid), processName, title),
            desktopProbe);
    }

    private static ForegroundPrivilegeSnapshot WithDesktop(
        ForegroundPrivilegeSnapshot snapshot,
        InputDesktopProbeResult desktopProbe) =>
        snapshot with
        {
            DesktopState = desktopProbe.State,
            DesktopProbe = desktopProbe
        };

    public static WindowsIntegrityLevel GetProcessIntegrity(int processId)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, unchecked((uint)processId));
        if (process == IntPtr.Zero)
        {
            return WindowsIntegrityLevel.Unknown;
        }

        try
        {
            if (!OpenProcessToken(process, TokenQuery, out var token))
            {
                return WindowsIntegrityLevel.Unknown;
            }

            try
            {
                _ = GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out var required);
                if (required <= 0)
                {
                    return WindowsIntegrityLevel.Unknown;
                }

                var buffer = Marshal.AllocHGlobal(required);
                try
                {
                    if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, required, out _))
                    {
                        return WindowsIntegrityLevel.Unknown;
                    }

                    var sid = Marshal.ReadIntPtr(buffer);
                    var count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                    var rid = unchecked((uint)Marshal.ReadInt32(GetSidSubAuthority(sid, count - 1)));
                    return rid switch
                    {
                        >= 0x4000 => WindowsIntegrityLevel.System,
                        >= 0x3000 => WindowsIntegrityLevel.High,
                        >= 0x2000 => WindowsIntegrityLevel.Medium,
                        >= 0x1000 => WindowsIntegrityLevel.Low,
                        _ => WindowsIntegrityLevel.Unknown
                    };
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                _ = CloseHandle(token);
            }
        }
        finally
        {
            _ = CloseHandle(process);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);
    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, int subAuthority);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, [Out] char[] text, int maximumCount);
}

public enum ElevationLaunchResult
{
    Started,
    Cancelled,
    Failed
}

public interface IElevationLauncher
{
    ElevationLaunchResult Launch(string executablePath, IReadOnlyList<string> arguments, out string? error);
}

public sealed class WindowsElevationLauncher : IElevationLauncher
{
    public ElevationLaunchResult Launch(string executablePath, IReadOnlyList<string> arguments, out string? error)
    {
        error = null;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = true,
                Verb = "runas",
                Arguments = string.Join(" ", arguments.Select(QuoteArgument)),
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory
            });
            return ElevationLaunchResult.Started;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return ElevationLaunchResult.Cancelled;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return ElevationLaunchResult.Failed;
        }
    }

    private static string QuoteArgument(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
}

public static class RecordingLibraryPaths
{
    public static string ProjectRoot => AppPaths.Current.ProjectRoot;
    public static string CanonicalRecordingsDirectory => AppPaths.Current.RecordingsDirectory;

    public static IReadOnlyList<string> GetSearchDirectories() => new[] { CanonicalRecordingsDirectory };
}

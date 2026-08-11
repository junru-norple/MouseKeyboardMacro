using System.Runtime.InteropServices;
using System.Text;
using MacroCore.Diagnostics;
using MacroCore.Runtime;
using MacroCore.Security;

namespace MacroRecorder;

public static class DesktopProbeMode
{
    public const string OptionName = "--desktop-probe-only";

    public static bool IsRequested(IReadOnlyList<string> args) =>
        args.Any(argument => string.Equals(argument, OptionName, StringComparison.OrdinalIgnoreCase));

    public static int Run(
        IInputDesktopProbe? desktopProbe = null,
        IWindowsPrivilegeService? privilegeService = null,
        TextWriter? output = null)
    {
        desktopProbe ??= new WindowsInputDesktopProbe();
        privilegeService ??= new WindowsPrivilegeService(desktopProbe);
        output ??= Console.Out;

        var evaluation = new RecordingStartPrivilegeEvaluator(privilegeService, desktopProbe).Evaluate();
        DesktopSecurityProbeLog.Write(
            evaluation.DesktopProbe,
            evaluation.PrivilegeSnapshot,
            "DESKTOP_PROBE_ONLY",
            evaluation.Decision);

        var lines = new[]
        {
            "DESKTOP_PROBE_RESULT_V1",
            $"State={evaluation.DesktopProbe.State}",
            $"DesktopName={evaluation.DesktopProbe.DesktopName ?? string.Empty}",
            $"OpenInputDesktopError={evaluation.DesktopProbe.OpenInputDesktopError}",
            $"QuerySizeError={evaluation.DesktopProbe.QuerySizeError}",
            $"QueryNameError={evaluation.DesktopProbe.QueryNameError}",
            $"RecorderIntegrity={evaluation.PrivilegeSnapshot.RecorderIntegrity}",
            $"TargetIntegrity={evaluation.PrivilegeSnapshot.TargetIntegrity}",
            $"TargetPid={evaluation.PrivilegeSnapshot.TargetProcessId}",
            $"Decision={evaluation.Decision}",
            $"ProbeMethod={evaluation.DesktopProbe.ProbeMethod}",
            $"ProcessId={Environment.ProcessId}"
        };
        foreach (var line in lines)
        {
            output.WriteLine(line);
        }
        output.Flush();

        var resultPath = Path.Combine(AppPaths.Current.LogsDirectory, "desktop_probe_gate_result.txt");
        File.WriteAllLines(resultPath, lines, new UTF8Encoding(false));

        return evaluation.DesktopProbe.State switch
        {
            InputDesktopState.DefaultDesktop => 0,
            InputDesktopState.SecureOrAlternateDesktop => 2,
            _ => 3
        };
    }
}

internal static class ProbeConsole
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    public static void TryAttachParent()
    {
        try
        {
            if (AttachConsole(AttachParentProcess))
            {
                var writer = new StreamWriter(Console.OpenStandardOutput(), Console.OutputEncoding) { AutoFlush = true };
                Console.SetOut(writer);
            }
        }
        catch
        {
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "AttachConsole", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);
}

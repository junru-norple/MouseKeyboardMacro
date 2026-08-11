using MacroCore.Security;

namespace MacroCore.Diagnostics;

public static class DesktopSecurityProbeLog
{
    public const string FileName = "desktop_security_probe.log";

    public static void Write(
        InputDesktopProbeResult probe,
        ForegroundPrivilegeSnapshot privilege,
        string mode,
        RecordingStartDecision decision)
    {
        var line = string.Join("\t", new[]
        {
            $"timestamp={probe.Timestamp:O}",
            $"process_id={Environment.ProcessId}",
            $"mode={Safe(mode)}",
            $"recorder_integrity={privilege.RecorderIntegrity}",
            $"desktop_state={probe.State}",
            $"desktop_name={Safe(probe.DesktopName)}",
            $"open_error={probe.OpenInputDesktopError}",
            $"size_error={probe.QuerySizeError}",
            $"name_error={probe.QueryNameError}",
            $"target_pid={privilege.TargetProcessId}",
            $"target_integrity={privilege.TargetIntegrity}",
            $"decision={decision}",
            $"probe_method={probe.ProbeMethod}"
        });
        RotatingLog.WriteRuntime(FileName, line);
    }

    private static string Safe(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
}

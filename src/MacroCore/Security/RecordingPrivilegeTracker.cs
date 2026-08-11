using MacroCore.Models;

namespace MacroCore.Security;

public static class RecordingPrivilegeTracker
{
    private static readonly object Sync = new();
    private static bool _active;
    private static bool _pending;
    private static WindowsIntegrityLevel _recorder;
    private static WindowsIntegrityLevel _target;
    private static string _captureMode = "Standard";
    private static string? _processName;
    private static string? _windowTitle;

    public static void Begin(IWindowsPrivilegeService service, string captureMode)
        => Begin(service, captureMode, service.CaptureForeground());

    public static void Begin(
        IWindowsPrivilegeService service,
        string captureMode,
        ForegroundPrivilegeSnapshot initialSnapshot)
    {
        lock (Sync)
        {
            _active = true;
            _pending = true;
            _recorder = initialSnapshot.RecorderIntegrity;
            _target = WindowsIntegrityLevel.Unknown;
            _captureMode = string.Equals(captureMode, "RawEnhanced", StringComparison.OrdinalIgnoreCase)
                ? "RawEnhanced"
                : "Standard";
            _processName = null;
            _windowTitle = null;
        }
        ObserveSnapshot(initialSnapshot);
    }

    public static void ObserveForeground(IWindowsPrivilegeService service)
    {
        lock (Sync)
        {
            if (!_active)
            {
                return;
            }
        }

        ObserveSnapshot(service.CaptureForeground());
    }

    private static void ObserveSnapshot(ForegroundPrivilegeSnapshot snapshot)
    {
        if (snapshot.DesktopState != InputDesktopState.DefaultDesktop)
        {
            return;
        }
        lock (Sync)
        {
            if (!_active)
            {
                return;
            }
            if (snapshot.TargetIntegrity > _target)
            {
                _target = snapshot.TargetIntegrity;
            }
            if (!string.IsNullOrWhiteSpace(snapshot.TargetProcessName) && (_processName is null || snapshot.TargetIntegrity >= _target))
            {
                _processName = Path.GetFileName(snapshot.TargetProcessName);
                _windowTitle = Truncate(snapshot.TargetWindowTitle, 120);
            }
        }
    }

    public static void End()
    {
        lock (Sync)
        {
            _active = false;
        }
    }

    public static void ApplyTo(MacroFile macro)
    {
        lock (Sync)
        {
            if (!_pending || (macro.SchemaVersion is not "1.1" and not "1.2"))
            {
                return;
            }
            macro.CaptureMetadata ??= new MacroCaptureMetadata();
            macro.CaptureMetadata.RecordedRecorderIntegrity = ToMetadata(_recorder);
            macro.CaptureMetadata.RecordedTargetIntegrity = ToMetadata(_target);
            macro.CaptureMetadata.RequiresElevationForPlayback = ResolveRequiresElevation(ToMetadata(_recorder), ToMetadata(_target));
            macro.CaptureMetadata.CaptureMode = _captureMode;
            macro.CaptureMetadata.TargetProcessName = Path.GetFileName(_processName) ?? string.Empty;
            macro.CaptureMetadata.TargetWindowTitle = _windowTitle;
            macro.CaptureMetadata.RecordedWithVersion = typeof(RecordingPrivilegeTracker).Assembly.GetName().Version?.ToString() ?? "1.1";
            _pending = false;
        }
    }

    public static bool? ResolveRequiresElevation(string? recorderIntegrity, string? targetIntegrity)
    {
        if (IsHigh(recorderIntegrity) || IsHigh(targetIntegrity))
        {
            return true;
        }
        if (IsKnownStandard(recorderIntegrity) && IsKnownStandard(targetIntegrity))
        {
            return false;
        }
        return null;
    }

    private static bool IsHigh(string? value) =>
        value is not null && (value.Equals("High", StringComparison.OrdinalIgnoreCase) || value.Equals("System", StringComparison.OrdinalIgnoreCase));

    private static bool IsKnownStandard(string? value) =>
        value is not null && (value.Equals("Low", StringComparison.OrdinalIgnoreCase) || value.Equals("Medium", StringComparison.OrdinalIgnoreCase));

    private static string ToMetadata(WindowsIntegrityLevel level) => level switch
    {
        WindowsIntegrityLevel.Medium => "Medium",
        WindowsIntegrityLevel.High or WindowsIntegrityLevel.System => "High",
        _ => "Unknown"
    };

    private static string? Truncate(string? value, int maximum) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= maximum ? value : value[..maximum];
}

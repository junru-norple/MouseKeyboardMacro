using System.Text.Json.Serialization;
using MacroCore.Serialization;

namespace MacroCore.Models;

public enum MacroEventKind
{
    MouseMove,
    MouseDown,
    MouseUp,
    MouseWheel,
    MouseHorizontalWheel,
    KeyDown,
    KeyUp
}

public enum MouseButtonKind
{
    Left,
    Right,
    Middle,
    X1,
    X2
}

public enum CaptureSourceKind
{
    LowLevelKeyboard,
    LowLevelMouse,
    RawKeyboard,
    RawMouse
}

public enum MouseMovementMode
{
    DesktopAbsolute,
    RawRelative
}

[Flags]
public enum MouseTrajectoryCapabilities
{
    None = 0,
    AbsolutePosition = 1,
    RelativeDelta = 2
}

public enum MouseReplayMode
{
    AbsoluteDesktop,
    RawRelative
}

public enum CaptureInputMode
{
    DesktopHook,
    RawInput,
    Hybrid,
    UnsupportedPermissionMismatch
}

public enum ProcessIntegrityKind
{
    Unknown,
    Low,
    Medium,
    High,
    System
}

public readonly record struct KeyIdentity(int VirtualKey, int ScanCode, bool IsExtended);

public sealed class MacroFile
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = MacroSerializer.SchemaVersion;

    [JsonPropertyName("macroName")]
    public string MacroName { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("duration")]
    public long DurationMs { get; set; }

    [JsonPropertyName("recordedDisplayLayout")]
    public MacroDisplayLayout RecordedDisplayLayout { get; set; } = new();

    [JsonPropertyName("captureMetadata")]
    public MacroCaptureMetadata? CaptureMetadata { get; set; }

    [JsonPropertyName("events")]
    public List<MacroEventRecord> Events { get; set; } = [];
}

public sealed class MacroCaptureMetadata
{
    public string? RecordedRecorderIntegrity { get; set; }

    public string? RecordedTargetIntegrity { get; set; }

    public bool? RequiresElevationForPlayback { get; set; }

    public string? CaptureMode { get; set; }

    public string? TargetWindowTitle { get; set; }

    public string? RecordedWithVersion { get; set; }

    [JsonPropertyName("recommendedMouseReplayMode")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MouseReplayMode? RecommendedMouseReplayMode { get; set; }

    [JsonPropertyName("recordedCursorStart")]
    public MacroPoint? RecordedCursorStart { get; set; }

    [JsonPropertyName("inputMode")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CaptureInputMode InputMode { get; set; }

    [JsonPropertyName("targetProcessName")]
    public string TargetProcessName { get; set; } = string.Empty;

    [JsonPropertyName("targetIntegrity")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ProcessIntegrityKind TargetIntegrity { get; set; }

    [JsonPropertyName("recorderIntegrity")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ProcessIntegrityKind RecorderIntegrity { get; set; }

    [JsonPropertyName("coversMonitorBounds")]
    public bool CoversMonitorBounds { get; set; }

    [JsonPropertyName("likelyBorderlessFullscreen")]
    public bool LikelyBorderlessFullscreen { get; set; }

    [JsonPropertyName("likelyExclusiveFullscreen")]
    public bool LikelyExclusiveFullscreen { get; set; }

    [JsonPropertyName("lowLevelKeyboardCount")]
    public long LowLevelKeyboardCount { get; set; }

    [JsonPropertyName("lowLevelMouseCount")]
    public long LowLevelMouseCount { get; set; }

    [JsonPropertyName("rawKeyboardCount")]
    public long RawKeyboardCount { get; set; }

    [JsonPropertyName("rawMouseCount")]
    public long RawMouseCount { get; set; }

    [JsonPropertyName("duplicateCount")]
    public long DuplicateCount { get; set; }
}

public sealed class MacroDisplayLayout
{
    [JsonPropertyName("virtualBounds")]
    public MacroRect VirtualBounds { get; set; } = new();

    [JsonPropertyName("screens")]
    public List<MacroScreenInfo> Screens { get; set; } = [];

    [JsonPropertyName("primaryScreenBounds")]
    public MacroRect PrimaryScreenBounds { get; set; } = new();

    [JsonPropertyName("screenCount")]
    public int ScreenCount { get; set; }

    public bool IsEquivalentTo(MacroDisplayLayout? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ScreenCount != other.ScreenCount)
        {
            return false;
        }

        if (!VirtualBounds.Equals(other.VirtualBounds) || !PrimaryScreenBounds.Equals(other.PrimaryScreenBounds))
        {
            return false;
        }

        if (Screens.Count != other.Screens.Count)
        {
            return false;
        }

        var a = Screens.OrderBy(s => s.DeviceName, StringComparer.OrdinalIgnoreCase).ToList();
        var b = other.Screens.OrderBy(s => s.DeviceName, StringComparer.OrdinalIgnoreCase).ToList();
        for (int i = 0; i < a.Count; i++)
        {
            if (!a[i].Equals(b[i]))
            {
                return false;
            }
        }

        return true;
    }
}

public sealed class MacroScreenInfo
{
    [JsonPropertyName("deviceName")]
    public string DeviceName { get; set; } = string.Empty;

    [JsonPropertyName("bounds")]
    public MacroRect Bounds { get; set; } = new();

    [JsonPropertyName("isPrimary")]
    public bool IsPrimary { get; set; }

    [JsonPropertyName("dpiX")]
    public int DpiX { get; set; } = 96;

    [JsonPropertyName("dpiY")]
    public int DpiY { get; set; } = 96;

    public bool Equals(MacroScreenInfo? other)
    {
        return other is not null &&
               string.Equals(DeviceName, other.DeviceName, StringComparison.OrdinalIgnoreCase) &&
               Bounds.Equals(other.Bounds) &&
               IsPrimary == other.IsPrimary &&
               DpiX == other.DpiX &&
               DpiY == other.DpiY;
    }
}

public sealed class MacroRect
{
    [JsonPropertyName("x")]
    public int X { get; set; }

    [JsonPropertyName("y")]
    public int Y { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    public bool Equals(MacroRect? other)
    {
        return other is not null &&
               X == other.X &&
               Y == other.Y &&
               Width == other.Width &&
               Height == other.Height;
    }
}

public sealed class MacroPoint
{
    [JsonPropertyName("x")]
    public int X { get; set; }

    [JsonPropertyName("y")]
    public int Y { get; set; }
}

public sealed class MacroEventRecord
{
    [JsonPropertyName("type")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MacroEventKind Type { get; set; }

    [JsonPropertyName("timeMs")]
    public long TimeMs { get; set; }

    [JsonPropertyName("x")]
    public int? X { get; set; }

    [JsonPropertyName("y")]
    public int? Y { get; set; }

    [JsonPropertyName("mouseButton")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MouseButtonKind? MouseButton { get; set; }

    [JsonPropertyName("wheelDelta")]
    public int? WheelDelta { get; set; }

    [JsonPropertyName("virtualKey")]
    public int? VirtualKey { get; set; }

    [JsonPropertyName("scanCode")]
    public int? ScanCode { get; set; }

    [JsonPropertyName("isExtended")]
    public bool IsExtended { get; set; }

    [JsonPropertyName("flags")]
    public int Flags { get; set; }

    [JsonPropertyName("captureSource")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CaptureSourceKind? CaptureSource { get; set; }

    [JsonPropertyName("mouseMovementMode")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MouseMovementMode? MouseMovementMode { get; set; }

    [JsonPropertyName("deltaX")]
    public int? DeltaX { get; set; }

    [JsonPropertyName("deltaY")]
    public int? DeltaY { get; set; }

    [JsonPropertyName("mouseTrajectoryCapabilities")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MouseTrajectoryCapabilities? MouseTrajectoryCapabilities { get; set; }

    [JsonPropertyName("isInitialCursorAnchor")]
    public bool? IsInitialCursorAnchor { get; set; }

    [JsonIgnore]
    public MouseTrajectoryCapabilities EffectiveMouseTrajectoryCapabilities
    {
        get
        {
            if (MouseTrajectoryCapabilities.HasValue)
            {
                return MouseTrajectoryCapabilities.Value;
            }

            var capabilities = global::MacroCore.Models.MouseTrajectoryCapabilities.None;
            if (X.HasValue && Y.HasValue)
            {
                capabilities |= global::MacroCore.Models.MouseTrajectoryCapabilities.AbsolutePosition;
            }
            if (DeltaX.HasValue && DeltaY.HasValue)
            {
                capabilities |= global::MacroCore.Models.MouseTrajectoryCapabilities.RelativeDelta;
            }
            return capabilities;
        }
    }
}

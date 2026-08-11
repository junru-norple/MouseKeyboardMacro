using System.Text.Json;
using MacroCore.Models;

namespace MacroPlayer;

public enum PlaybackEventKind
{
    KeyDown,
    KeyUp,
    MouseMove,
    MouseDown,
    MouseUp,
    MouseWheel,
    MouseHorizontalWheel
}

public sealed record PlaybackMacroEvent(
    long OffsetMilliseconds,
    PlaybackEventKind Kind,
    int VirtualKey,
    int ScanCode,
    bool Extended,
    int X,
    int Y,
    string MouseButton,
    int WheelDelta,
    bool IsRelative = false,
    int DeltaX = 0,
    int DeltaY = 0,
    MouseTrajectoryCapabilities MouseCapabilities = MouseTrajectoryCapabilities.None,
    bool HasAbsolutePosition = true,
    bool HasRelativeDelta = false,
    bool IsInitialCursorAnchor = false)
{
    public MouseTrajectoryCapabilities EffectiveMouseCapabilities
    {
        get
        {
            if (MouseCapabilities != MouseTrajectoryCapabilities.None)
            {
                return MouseCapabilities;
            }

            var capabilities = HasAbsolutePosition
                ? MouseTrajectoryCapabilities.AbsolutePosition
                : MouseTrajectoryCapabilities.None;
            if (HasRelativeDelta || IsRelative)
            {
                capabilities |= MouseTrajectoryCapabilities.RelativeDelta;
            }
            return capabilities;
        }
    }
}

public sealed record PlaybackMacroDocument(
    string FilePath,
    string SchemaVersion,
    string Name,
    DateTimeOffset? CreatedAt,
    long DurationMilliseconds,
    bool? RequiresElevation,
    string CaptureMode,
    string TargetProcessName,
    string TargetWindowTitle,
    string ScreenSummary,
    IReadOnlyList<PlaybackMacroEvent> Events,
    int? VirtualScreenLeft = null,
    int? VirtualScreenTop = null,
    int? VirtualScreenWidth = null,
    int? VirtualScreenHeight = null,
    string RecordedRecorderIntegrity = "",
    string LegacyRecorderIntegrity = "",
    string RecordedTargetIntegrity = "",
    string LegacyTargetIntegrity = "",
    string CaptureSessionMode = "")
{
    public bool HasScreenConfiguration => VirtualScreenWidth.HasValue && VirtualScreenHeight.HasValue;

    public bool MatchesCurrentScreen(Rectangle current) => !HasScreenConfiguration ||
        (VirtualScreenLeft ?? 0) == current.Left &&
        (VirtualScreenTop ?? 0) == current.Top &&
        VirtualScreenWidth == current.Width &&
        VirtualScreenHeight == current.Height;

    public static PlaybackMacroDocument Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new InvalidDataException("找不到指定的巨集檔案。");
        }

        FileInfo info = new(path);
        if (info.Length == 0)
        {
            throw new InvalidDataException("巨集檔案是空白檔案。");
        }

        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using JsonDocument json = JsonDocument.Parse(stream, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32
        });

        JsonElement root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("巨集格式錯誤：根節點不是物件。");
        }

        string schema = GetString(root, "schemaVersion") ?? "";
        if (!schema.Equals("1.0", StringComparison.Ordinal) &&
            !schema.Equals("1.1", StringComparison.Ordinal) &&
            !schema.Equals("1.2", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"不支援的巨集 schemaVersion：{(string.IsNullOrEmpty(schema) ? "（缺少）" : schema)}。");
        }

        JsonElement metadata = TryGet(root, "captureMetadata", out JsonElement metadataElement) && metadataElement.ValueKind == JsonValueKind.Object
            ? metadataElement
            : TryGet(root, "metadata", out metadataElement) && metadataElement.ValueKind == JsonValueKind.Object
                ? metadataElement
                : root;

        if (!TryGet(root, "events", out JsonElement eventsElement) || eventsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("巨集格式錯誤：缺少 events 陣列。");
        }

        List<PlaybackMacroEvent> events = new(eventsElement.GetArrayLength());
        long previousOffset = 0;
        foreach (JsonElement item in eventsElement.EnumerateArray())
        {
            string rawKind = GetString(item, "type", "eventType", "kind") ?? "";
            bool relativeKind = rawKind.Equals("RawMouseMove", StringComparison.OrdinalIgnoreCase) ||
                rawKind.Equals("MouseDelta", StringComparison.OrdinalIgnoreCase) ||
                rawKind.Equals("RelativeMouseMove", StringComparison.OrdinalIgnoreCase);
            if (relativeKind)
            {
                rawKind = nameof(PlaybackEventKind.MouseMove);
            }

            if (!Enum.TryParse(rawKind, true, out PlaybackEventKind kind))
            {
                throw new InvalidDataException($"巨集包含未知事件類型：{rawKind}。");
            }

            long offset = GetInt64(item, "offsetMilliseconds", "timestampMilliseconds", "timestampMs", "elapsedMilliseconds", "timeMs", "timestamp") ?? previousOffset;
            if (offset < previousOffset || offset < 0)
            {
                throw new InvalidDataException("巨集事件時間順序無效。");
            }

            previousOffset = offset;
            int? deltaX = GetInt32(item, "deltaX", "relativeX", "dx");
            int? deltaY = GetInt32(item, "deltaY", "relativeY", "dy");
            int? absoluteX = GetInt32(item, "x", "screenX");
            int? absoluteY = GetInt32(item, "y", "screenY");
            bool hasAbsolute = absoluteX.HasValue && absoluteY.HasValue;
            bool hasRelative = deltaX.HasValue && deltaY.HasValue;
            bool isRelative = kind == PlaybackEventKind.MouseMove &&
                (relativeKind || GetBoolean(item, "isRelative", "relative") == true || deltaX.HasValue || deltaY.HasValue);
            MouseTrajectoryCapabilities capabilities = MouseTrajectoryCapabilities.None;
            string? serializedCapabilities = GetString(item, "mouseTrajectoryCapabilities", "trajectoryCapabilities");
            if (!string.IsNullOrWhiteSpace(serializedCapabilities) &&
                Enum.TryParse(serializedCapabilities, true, out MouseTrajectoryCapabilities explicitCapabilities))
            {
                capabilities = explicitCapabilities;
            }
            else
            {
                if (hasAbsolute) capabilities |= MouseTrajectoryCapabilities.AbsolutePosition;
                if (hasRelative) capabilities |= MouseTrajectoryCapabilities.RelativeDelta;
            }
            events.Add(new PlaybackMacroEvent(
                offset,
                kind,
                GetInt32(item, "virtualKey", "virtualKeyCode", "vkCode", "keyCode") ?? 0,
                GetInt32(item, "scanCode", "scan") ?? 0,
                GetBoolean(item, "extended", "isExtendedKey") ?? false,
                absoluteX ?? 0,
                absoluteY ?? 0,
                GetString(item, "mouseButton", "button") ?? "Left",
                GetInt32(item, "wheelDelta", "delta", "mouseData") ?? 0,
                isRelative,
                deltaX ?? 0,
                deltaY ?? 0,
                capabilities,
                hasAbsolute,
                hasRelative,
                GetBoolean(item, "isInitialCursorAnchor", "initialCursorAnchor") == true));
        }

        string name = GetString(root, "macroName", "name") ?? GetString(metadata, "name", "macroName") ?? Path.GetFileNameWithoutExtension(path);
        DateTimeOffset? created = ParseDate(GetString(root, "createdAt", "createdAtUtc", "recordedAt") ??
            GetString(metadata, "createdAt", "createdAtUtc", "recordedAt"));
        bool? requiresElevation = schema == "1.0"
            ? null
            : GetBoolean(metadata, "requiresElevationForPlayback");
        string recordedRecorderIntegrity = GetString(metadata, "recordedRecorderIntegrity") ?? string.Empty;
        string legacyRecorderIntegrity = GetString(metadata, "recorderIntegrity") ?? string.Empty;
        string recordedTargetIntegrity = GetString(metadata, "recordedTargetIntegrity") ?? string.Empty;
        string legacyTargetIntegrity = GetString(metadata, "targetIntegrity") ?? string.Empty;
        string captureSessionMode = GetString(metadata, "recorderMode", "sessionMode", "requestedMode") ?? string.Empty;
        long duration = GetInt64(root, "duration", "durationMilliseconds", "durationMs") ??
            GetInt64(metadata, "duration", "durationMilliseconds", "durationMs") ??
            (events.Count == 0 ? 0 : events[^1].OffsetMilliseconds);
        string captureMode = GetString(metadata, "captureMode") ?? (schema == "1.0" ? "Unknown" : "DesktopSafe");
        string processName = Path.GetFileName(GetString(metadata, "targetProcessName") ?? string.Empty);
        string windowTitle = Truncate(GetString(metadata, "targetWindowTitle") ?? string.Empty, 160);
        (int? left, int? top, int? width, int? height) = ReadScreenConfiguration(root, metadata);
        string screen = width.HasValue && height.HasValue
            ? $"{width} x {height}，原點 ({left ?? 0}, {top ?? 0})"
            : "未提供螢幕配置";

        return new PlaybackMacroDocument(
            Path.GetFullPath(path), schema, name, created, duration, requiresElevation,
            captureMode, processName, windowTitle, screen, events, left, top, width, height,
            recordedRecorderIntegrity, legacyRecorderIntegrity, recordedTargetIntegrity,
            legacyTargetIntegrity, captureSessionMode);
    }

    private static (int? Left, int? Top, int? Width, int? Height) ReadScreenConfiguration(JsonElement root, JsonElement metadata)
    {
        JsonElement source = TryGet(root, "recordedDisplayLayout", out JsonElement displayLayout) &&
            TryGet(displayLayout, "virtualBounds", out JsonElement virtualBounds) ? virtualBounds :
            TryGet(metadata, "screenConfiguration", out JsonElement nested) ? nested :
            TryGet(metadata, "displayConfiguration", out nested) ? nested :
            TryGet(root, "screenConfiguration", out nested) ? nested : root;
        int? width = GetInt32(source, "virtualScreenWidth", "width");
        int? height = GetInt32(source, "virtualScreenHeight", "height");
        int? left = GetInt32(source, "virtualScreenLeft", "left", "x");
        int? top = GetInt32(source, "virtualScreenTop", "top", "y");
        return (left, top, width, height);
    }

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, out DateTimeOffset result) ? result : null;

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (TryGet(element, name, out JsonElement value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static int? GetInt32(JsonElement element, params string[] names)
    {
        long? value = GetInt64(element, names);
        return value is >= int.MinValue and <= int.MaxValue ? (int)value.Value : null;
    }

    private static long? GetInt64(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (TryGet(element, name, out JsonElement value))
            {
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number))
                {
                    return number;
                }

                if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number))
                {
                    return number;
                }
            }
        }

        return null;
    }

    private static bool? GetBoolean(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (TryGet(element, name, out JsonElement value))
            {
                if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    return value.GetBoolean();
                }

                if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out bool parsed))
                {
                    return parsed;
                }
            }
        }

        return null;
    }
}

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MacroCore.Models;

namespace MacroCore.Serialization;

public static class MacroSerializer
{
    public const string SchemaVersion = "1.2";
    public const string PreviousSchemaVersion = "1.1";
    public const string LegacySchemaVersion = "1.0";

    public static bool IsSupportedSchemaVersion(string? schemaVersion) =>
        string.Equals(schemaVersion, LegacySchemaVersion, StringComparison.Ordinal) ||
        string.Equals(schemaVersion, PreviousSchemaVersion, StringComparison.Ordinal) ||
        string.Equals(schemaVersion, SchemaVersion, StringComparison.Ordinal);

    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string ToJson(MacroFile macroFile)
    {
        MacroCore.Security.RecordingPrivilegeTracker.ApplyTo(macroFile);
        return JsonSerializer.Serialize(macroFile, SerializerOptions);
    }

    public static MacroFile FromJson(string json)
    {
        return JsonSerializer.Deserialize<MacroFile>(json, SerializerOptions) ?? throw new InvalidOperationException("macro is null");
    }

    public static void SaveAtomically(MacroFile macroFile, string filePath)
    {
        if (!TryValidate(macroFile, out var validationError))
        {
            throw new InvalidDataException(validationError ?? "巨集檔案驗證失敗。");
        }

        var normalized = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(normalized) ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);

            var directoryInfo = new DirectoryInfo(directory);
            if ((directoryInfo.Attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
            {
                throw new UnauthorizedAccessException($"Cannot write to read-only directory: {directory}");
            }
        }

        var tmp = Path.Combine(directory, $".{Path.GetFileName(normalized)}.{Guid.NewGuid():N}.tmp");
        var json = ToJson(macroFile);

        try
        {
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            File.Move(tmp, normalized, true);
        }
        catch
        {
            if (File.Exists(tmp))
            {
                try { File.Delete(tmp); } catch { }
            }
            throw;
        }
    }

    public static bool TryLoad(string filePath, out MacroFile? macro, out string? error)
    {
        macro = null;
        error = null;

        try
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                error = "Invalid macro file path.";
                return false;
            }

            var json = File.ReadAllText(filePath, new UTF8Encoding(false));
            if (string.IsNullOrWhiteSpace(json))
            {
                error = "Macro file is empty.";
                return false;
            }

            macro = FromJson(json);
            return TryValidate(macro, out error);
        }
        catch (JsonException ex)
        {
            error = $"Invalid macro JSON: {ex.Message}";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool TryValidate(MacroFile? macro, out string? error)
    {
        error = null;
        if (macro is null)
        {
            error = "巨集檔案內容不存在。";
            return false;
        }

        if (!IsSupportedSchemaVersion(macro.SchemaVersion))
        {
            error = $"不支援的 schemaVersion：{macro.SchemaVersion}。支援版本為 {LegacySchemaVersion}、{PreviousSchemaVersion}、{SchemaVersion}。";
            return false;
        }

        if (macro.Events is null)
        {
            error = "巨集檔案缺少 events。";
            return false;
        }

        var ordered = macro.Events.ToList();
        for (int i = 1; i < ordered.Count; i++)
        {
            if (ordered[i - 1].TimeMs > ordered[i].TimeMs)
            {
                error = "巨集檔案事件順序錯誤：TimeMs 必須依非遞減順序排列。";
                return false;
            }
        }

        if (!ValidateEventPairs(ordered, out error))
        {
            return false;
        }

        macro.Events = ordered;
        return true;
    }

    private static bool ValidateEventPairs(List<MacroEventRecord> events, out string? error)
    {
        error = null;
        var pressedKeys = new Dictionary<KeyIdentity, long>();
        var pressedMouseButtons = new Dictionary<MouseButtonKind, long>();

        foreach (var e in events)
        {
            if (e.Type == MacroEventKind.KeyDown)
            {
                if (!TryGetKeyIdentity(e, out var identity, out error))
                {
                    return false;
                }

                // A repeated KeyDown while the physical key is already down is valid
                // Windows auto-repeat. Keep the event, but keep only one pressed state.
                pressedKeys[identity] = e.TimeMs;
            }
            else if (e.Type == MacroEventKind.KeyUp)
            {
                if (!TryGetKeyIdentity(e, out var identity, out error))
                {
                    return false;
                }

                if (!pressedKeys.Remove(identity))
                {
                    error = $"巨集檔案事件順序錯誤：收到沒有對應按下事件的 KeyUp。{FormatIdentity(identity)}，TimeMs={e.TimeMs}。";
                    return false;
                }
            }
            else if (e.Type == MacroEventKind.MouseDown)
            {
                if (!e.MouseButton.HasValue)
                {
                    error = "巨集檔案事件不完整：MouseDown 缺少 mouseButton。";
                    return false;
                }

                pressedMouseButtons[e.MouseButton.Value] = e.TimeMs;
            }
            else if (e.Type == MacroEventKind.MouseUp)
            {
                if (!e.MouseButton.HasValue)
                {
                    error = "巨集檔案事件不完整：MouseUp 缺少 mouseButton。";
                    return false;
                }

                if (!pressedMouseButtons.Remove(e.MouseButton.Value))
                {
                    error = $"巨集檔案事件順序錯誤：收到沒有對應按下事件的 MouseUp。Button={e.MouseButton.Value}，TimeMs={e.TimeMs}。";
                    return false;
                }
            }
        }

        if (pressedKeys.Count > 0)
        {
            var dangling = pressedKeys
                .OrderBy(pair => pair.Value)
                .ThenBy(pair => pair.Key.VirtualKey)
                .ThenBy(pair => pair.Key.ScanCode)
                .First();
            error = $"巨集檔案不完整：按鍵未釋放。{FormatIdentity(dangling.Key)}，最後事件時間={dangling.Value} ms。";
            return false;
        }

        if (pressedMouseButtons.Count > 0)
        {
            var dangling = pressedMouseButtons.OrderBy(pair => pair.Value).First();
            error = $"巨集檔案不完整：滑鼠按鍵未釋放。Button={dangling.Key}，最後事件時間={dangling.Value} ms。";
            return false;
        }

        return true;
    }

    private static bool TryGetKeyIdentity(MacroEventRecord e, out KeyIdentity identity, out string? error)
    {
        identity = default;
        error = null;
        if (!e.VirtualKey.HasValue || !e.ScanCode.HasValue)
        {
            error = $"巨集檔案事件不完整：{e.Type} 必須包含 virtualKey、scanCode 與 isExtended。TimeMs={e.TimeMs}。";
            return false;
        }

        identity = new KeyIdentity(e.VirtualKey.Value, e.ScanCode.Value, e.IsExtended);
        return true;
    }

    private static string FormatIdentity(KeyIdentity identity)
    {
        return $"VK={identity.VirtualKey}，ScanCode={identity.ScanCode}，Extended={identity.IsExtended}";
    }
}

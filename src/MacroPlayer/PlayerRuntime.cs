using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MacroCore.Models;

namespace MacroPlayer;

public sealed record PlayerLaunchOptions(
    string ProjectRoot,
    string? InitialMacroPath,
    string RequestedMode,
    string? ReadyFile,
    bool UiLayoutProbe,
    nint LaunchForegroundWindow = default,
    PlayerCountdownMode? InitialCountdownMode = null,
    bool PrivilegeGateProbe = false,
    bool LegacyTargetModeIgnored = false)
{
    public static PlayerLaunchOptions Parse(string[] args)
    {
        string? projectRoot = null;
        string? macroPath = null;
        string requestedMode = "desktop-player";
        string? readyFile = null;
        bool uiProbe = false;
        bool privilegeGateProbe = false;
        bool legacyTargetModeIgnored = false;
        nint launchForeground = nint.Zero;
        PlayerCountdownMode? countdownMode = null;

        for (int i = 0; i < args.Length; i++)
        {
            string value = args[i];
            if (value.Equals("--project-root", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                projectRoot = args[++i];
            }
            else if (value.Equals("--requested-mode", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                requestedMode = args[++i];
            }
            else if (value.Equals("--ready-file", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                readyFile = args[++i];
            }
            else if (value.Equals("--ui-layout-probe", StringComparison.OrdinalIgnoreCase))
            {
                uiProbe = true;
            }
            else if (value.Equals("--privilege-gate-probe", StringComparison.OrdinalIgnoreCase))
            {
                privilegeGateProbe = true;
            }
            else if (value.Equals("--launch-foreground-hwnd", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length &&
                     long.TryParse(args[++i], out long handleValue))
            {
                launchForeground = new nint(handleValue);
            }
            else if (value.Equals("--countdown-mode", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length &&
                     Enum.TryParse(args[++i], true, out PlayerCountdownMode parsedCountdown))
            {
                countdownMode = parsedCountdown;
            }
            else if (value.Equals("--target-mode", StringComparison.OrdinalIgnoreCase))
            {
                legacyTargetModeIgnored = true;
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    i++;
                }
            }
            else if (value.Equals("--preselect", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                macroPath = args[++i];
            }
            else if (!value.StartsWith("--", StringComparison.Ordinal) && value.EndsWith(".macro", StringComparison.OrdinalIgnoreCase))
            {
                macroPath = value;
            }
        }

        string root = ResolveProjectRoot(projectRoot);
        return new PlayerLaunchOptions(
            root,
            NormalizeOptionalPath(macroPath),
            requestedMode,
            NormalizeOptionalPath(readyFile),
            uiProbe,
            launchForeground,
            countdownMode,
            privilegeGateProbe,
            legacyTargetModeIgnored);
    }

    public PlayerSettings ApplyOverrides(PlayerSettings settings) => settings with
    {
        CountdownMode = InitialCountdownMode ?? settings.CountdownMode
    };

    private static string ResolveProjectRoot(string? explicitRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            return Path.GetFullPath(explicitRoot);
        }

        DirectoryInfo? cursor = new(AppContext.BaseDirectory);
        while (cursor is not null)
        {
            if (Directory.Exists(Path.Combine(cursor.FullName, "Program")) &&
                Directory.Exists(Path.Combine(cursor.FullName, "Recordings")))
            {
                return cursor.FullName;
            }

            cursor = cursor.Parent;
        }

        string current = Path.GetFullPath(Environment.CurrentDirectory);
        return Directory.Exists(Path.Combine(current, "Recordings")) ? current : AppContext.BaseDirectory;
    }

    private static string? NormalizeOptionalPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
}

public static class PlayerRuntimePaths
{
    private static string _projectRoot = AppContext.BaseDirectory;

    public static string ProjectRoot => _projectRoot;
    public static string Recordings => Path.Combine(_projectRoot, "Recordings");
    public static string State => Path.Combine(_projectRoot, "Program", "State");
    public static string Logs => Path.Combine(State, "Logs");
    public static string Settings => Path.Combine(State, "Settings");

    public static void Initialize(string projectRoot)
    {
        _projectRoot = Path.GetFullPath(projectRoot);
        Directory.CreateDirectory(Recordings);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Settings);
    }
}

public enum PlayerCountdownMode
{
    KeepVisible,
    MinimizeBeforeCountdown
}

public sealed record PlayerSettings
{
    public const int CurrentVersion = 4;

    public int SettingsVersion { get; init; } = CurrentVersion;
    public PlayerCountdownMode CountdownMode { get; init; } = PlayerCountdownMode.MinimizeBeforeCountdown;

    public PlayerSettings()
    {
    }

    public PlayerSettings(PlayerCountdownMode countdownMode)
    {
        CountdownMode = countdownMode;
    }

    public static PlayerSettings Default => new();
}

public interface IPlayerSettingsStore
{
    string SettingsPath { get; }
    string LastDiagnostic { get; }
    PlayerSettings Load();
    PlayerSettings Update(Func<PlayerSettings, PlayerSettings> updater);
    void Save(PlayerSettings settings);
}

public sealed class PlayerSettingsStore : IPlayerSettingsStore
{
    private static readonly object ProcessLock = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private readonly string _settingsPath;
    private readonly Action<string>? _diagnosticSink;
    private string _lastDiagnostic = "NONE";

    public PlayerSettingsStore(string? settingsPath = null, Action<string>? diagnosticSink = null)
    {
        _settingsPath = Path.GetFullPath(settingsPath ?? SettingsPath);
        _diagnosticSink = diagnosticSink;
    }

    public static string SettingsPath => Path.Combine(PlayerRuntimePaths.Settings, "player-settings.json");

    string IPlayerSettingsStore.SettingsPath => _settingsPath;
    string IPlayerSettingsStore.LastDiagnostic => _lastDiagnostic;
    PlayerSettings IPlayerSettingsStore.Load() => LoadValue();
    PlayerSettings IPlayerSettingsStore.Update(Func<PlayerSettings, PlayerSettings> updater) => UpdateValue(updater);
    void IPlayerSettingsStore.Save(PlayerSettings settings) => SaveValue(settings);

    public static PlayerSettings Load() => new PlayerSettingsStore().LoadValue();

    public static PlayerSettings Update(Func<PlayerSettings, PlayerSettings> updater) =>
        new PlayerSettingsStore().UpdateValue(updater);

    public static void Save(PlayerSettings settings) => new PlayerSettingsStore().SaveValue(settings);

    public PlayerSettings LoadValue()
    {
        lock (ProcessLock)
        {
            return LoadCore(migrateLegacy: true);
        }
    }

    public PlayerSettings UpdateValue(Func<PlayerSettings, PlayerSettings> updater)
    {
        ArgumentNullException.ThrowIfNull(updater);
        lock (ProcessLock)
        {
            PlayerSettings current = LoadCore(migrateLegacy: true);
            PlayerSettings updated = Normalize(updater(current) ?? throw new InvalidOperationException("Settings updater returned null."));
            WriteAtomic(updated);
            Report("SETTINGS_UPDATE_OK");
            return updated;
        }
    }

    public void SaveValue(PlayerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (ProcessLock)
        {
            WriteAtomic(Normalize(settings));
            Report("SETTINGS_SAVE_OK");
        }
    }

    private PlayerSettings LoadCore(bool migrateLegacy)
    {
        if (!File.Exists(_settingsPath))
        {
            Report("FIRST_LAUNCH_DEFAULT");
            return PlayerSettings.Default;
        }

        try
        {
            string json = File.ReadAllText(_settingsPath, Encoding.UTF8);
            using JsonDocument document = JsonDocument.Parse(json);
            PlayerSettings loaded = JsonSerializer.Deserialize<PlayerSettings>(json, JsonOptions)
                ?? throw new JsonException("Settings document deserialized to null.");
            bool hasVersion = document.RootElement.TryGetProperty(nameof(PlayerSettings.SettingsVersion), out JsonElement versionElement);
            int sourceVersion = hasVersion && versionElement.TryGetInt32(out int parsedVersion) ? parsedVersion : 1;
            bool hasLegacyTargetMode = document.RootElement.TryGetProperty("TargetMode", out _);
            bool hasLegacyMouseReplayMode = document.RootElement.TryGetProperty("MouseReplayMode", out _);

            if (!Enum.IsDefined(loaded.CountdownMode) ||
                sourceVersion < 1 || sourceVersion > PlayerSettings.CurrentVersion)
            {
                PreserveCorrupt("INVALID_SETTING_VALUE");
                return PlayerSettings.Default;
            }

            PlayerSettings normalized = Normalize(loaded);
            if (migrateLegacy &&
                (sourceVersion != PlayerSettings.CurrentVersion || hasLegacyTargetMode || hasLegacyMouseReplayMode))
            {
                WriteAtomic(normalized);
                Report($"SETTINGS_MIGRATED_FROM_V{sourceVersion}");
            }
            else
            {
                Report("SETTINGS_LOAD_OK");
            }

            return normalized;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            PreserveCorrupt($"CORRUPT_SETTINGS_{exception.GetType().Name}");
            return PlayerSettings.Default;
        }
    }

    private static PlayerSettings Normalize(PlayerSettings settings) => settings with
    {
        SettingsVersion = PlayerSettings.CurrentVersion,
        CountdownMode = Enum.IsDefined(settings.CountdownMode)
            ? settings.CountdownMode
            : PlayerCountdownMode.MinimizeBeforeCountdown
    };

    private void WriteAtomic(PlayerSettings settings)
    {
        string? directory = Path.GetDirectoryName(_settingsPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("Settings path has no parent directory.");
        }

        Directory.CreateDirectory(directory);
        string temporary = _settingsPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions), Utf8NoBom);
            if (File.Exists(_settingsPath))
            {
                try
                {
                    File.Replace(temporary, _settingsPath, null, ignoreMetadataErrors: true);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Move(temporary, _settingsPath, overwrite: true);
                }
                catch (IOException)
                {
                    File.Move(temporary, _settingsPath, overwrite: true);
                }
            }
            else
            {
                File.Move(temporary, _settingsPath);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private void PreserveCorrupt(string reason)
    {
        if (File.Exists(_settingsPath))
        {
            string preserved = _settingsPath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            try
            {
                File.Move(_settingsPath, preserved, overwrite: false);
            }
            catch (IOException)
            {
                // Keeping the original file is safer than deleting it if preservation cannot be renamed.
            }
        }

        Report(reason);
    }

    private void Report(string diagnostic)
    {
        _lastDiagnostic = diagnostic;
        if (_diagnosticSink is not null)
        {
            _diagnosticSink(diagnostic);
            return;
        }

        try
        {
            Directory.CreateDirectory(PlayerRuntimePaths.Logs);
            File.AppendAllText(
                Path.Combine(PlayerRuntimePaths.Logs, "player_settings.log"),
                $"{DateTimeOffset.Now:O}\t{diagnostic}{Environment.NewLine}",
                Utf8NoBom);
        }
        catch
        {
            // Settings diagnostics must never prevent the Player from opening safely.
        }
    }
}

public sealed record PlaybackSessionOptionsSnapshot(
    PlayerCountdownMode CountdownMode,
    string UiCountdownText,
    DateTimeOffset CapturedAt);

public sealed record PlaybackSessionModeAudit(
    string DisplayedCountdownText,
    PlayerCountdownMode UiCountdownMode,
    PlayerCountdownMode SavedCountdownModeAtStart,
    PlayerCountdownMode EffectiveCountdownMode)
{
    public bool IsConsistent => UiCountdownMode == SavedCountdownModeAtStart &&
                                SavedCountdownModeAtStart == EffectiveCountdownMode;

    public string ToLogLine() =>
        $"SESSION_OPTIONS displayedCountdownText={Sanitize(DisplayedCountdownText)} " +
        $"uiCountdownMode={UiCountdownMode} savedCountdownModeAtStart={SavedCountdownModeAtStart} " +
        $"effectiveCountdownMode={EffectiveCountdownMode} consistency={(IsConsistent ? "PASS" : "FAIL")}";

    private static string Sanitize(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
}

public static class PlaybackSessionOptionsFactory
{
    public static bool TryCreate(
        PlayerCountdownMode? uiCountdownMode,
        string uiCountdownText,
        PlayerSettings savedSettings,
        DateTimeOffset capturedAt,
        out PlaybackSessionOptionsSnapshot? snapshot,
        out PlaybackSessionModeAudit? audit,
        out string error)
    {
        snapshot = null;
        audit = null;
        if (uiCountdownMode is null || !Enum.IsDefined(uiCountdownMode.Value))
        {
            error = "目前顯示的倒數模式無效，已安全阻止播放；請重新選擇倒數模式。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(uiCountdownText))
        {
            error = "播放選項不完整，已安全阻止播放；請重新選擇後再試。";
            return false;
        }

        snapshot = new PlaybackSessionOptionsSnapshot(
            uiCountdownMode.Value,
            uiCountdownText.Trim(),
            capturedAt);
        audit = new PlaybackSessionModeAudit(
            snapshot.UiCountdownText,
            snapshot.CountdownMode,
            savedSettings.CountdownMode,
            snapshot.CountdownMode);
        if (!audit.IsConsistent)
        {
            error = "畫面顯示的倒數模式與已儲存設定不一致，已安全阻止播放；請重新選擇一次。";
            snapshot = null;
            return false;
        }

        error = string.Empty;
        return true;
    }
}

public static class PlayerLayoutTextMetrics
{
    public static int RequiredTextHeight(string text, Font font, int availableWidth, float scale = 1f)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        int scaledWidth = Math.Max(1, (int)Math.Floor(availableWidth / Math.Max(0.5f, scale)));
        TextFormatFlags flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl;
        return TextRenderer.MeasureText(text, font, new Size(scaledWidth, int.MaxValue), flags).Height;
    }
}

public static class WindowsPlayerPrivilege
{
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;

    public static bool IsElevated => GetCurrentIntegrityRid() >= 0x3000;
    public static string Label => IsElevated ? "High" : "Medium";

    public static int GetProcessIntegrityRid(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            if (!OpenProcessToken(process.Handle, TokenQuery, out nint token))
            {
                return -1;
            }

            try
            {
                _ = GetTokenInformation(token, TokenIntegrityLevel, nint.Zero, 0, out int needed);
                if (needed <= 0)
                {
                    return -1;
                }

                nint buffer = Marshal.AllocHGlobal(needed);
                try
                {
                    if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, needed, out _))
                    {
                        return -1;
                    }

                    nint sid = Marshal.ReadIntPtr(buffer);
                    nint count = GetSidSubAuthorityCount(sid);
                    byte subAuthorityCount = Marshal.ReadByte(count);
                    nint rid = GetSidSubAuthority(sid, (uint)(subAuthorityCount - 1));
                    return Marshal.ReadInt32(rid);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CloseHandle(token);
            }
        }
        catch
        {
            return -1;
        }
    }

    private static int GetCurrentIntegrityRid() => GetProcessIntegrityRid(Environment.ProcessId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(nint tokenHandle, int informationClass, nint information, int informationLength, out int returnLength);

    [DllImport("advapi32.dll")]
    private static extern nint GetSidSubAuthorityCount(nint sid);

    [DllImport("advapi32.dll")]
    private static extern nint GetSidSubAuthority(nint sid, uint subAuthority);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);
}

public static class PlayerElevationPolicy
{
    public static bool IsUserCancellation(Exception exception) =>
        exception is System.ComponentModel.Win32Exception win32 && win32.NativeErrorCode == 1223;
}

public static class PlayerElevationRelaunchArguments
{
    public static IReadOnlyList<string> Build(
        string projectRoot,
        string? selectedMacro,
        PlayerCountdownMode countdownMode)
    {
        List<string> arguments =
        [
            "--project-root", Path.GetFullPath(projectRoot),
            "--requested-mode", "elevated-player",
            "--countdown-mode", countdownMode.ToString()
        ];
        if (!string.IsNullOrWhiteSpace(selectedMacro))
        {
            arguments.Add("--preselect");
            arguments.Add(Path.GetFullPath(selectedMacro));
        }
        return arguments;
    }
}

using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;

namespace MacroLauncher;

public enum LauncherTool
{
    Recorder,
    Player,
    Emergency
}

public enum LauncherMode
{
    Medium,
    Elevated
}

public sealed record LauncherRequest(
    LauncherTool Tool,
    LauncherMode Mode,
    string ProjectRoot,
    string? PreselectPath,
    bool ElevatedStage,
    bool ValidateOnly,
    bool SafeValidation = false,
    bool ReplacementCleanupOnly = false,
    string? CleanupResultPath = null,
    string? CleanupToken = null);

public sealed record LauncherPaths(
    string ProjectRoot,
    string MarkerPath,
    string AppRoot,
    string StateRoot,
    string LogsRoot,
    string LaunchRoot,
    string CurrentSessionPath,
    string LauncherPath,
    string RecorderPath,
    string PlayerPath,
    string WatchdogPath,
    string LauncherLogPath)
{
    public string ActiveToolLockPath => Path.Combine(StateRoot, "active_tool.lock");
    public string ActiveToolMetadataPath => Path.Combine(StateRoot, "active_tool.json");
    public string LaunchCoordinatorLockPath => Path.Combine(LaunchRoot, "launch_coordinator.lock");
    public string ReplacementLogPath => Path.Combine(LogsRoot, "replacement.log");

    public static bool TryCreate(string root, out LauncherPaths? paths, out string error)
    {
        paths = null;
        error = string.Empty;

        try
        {
            string fullRoot = Path.GetFullPath(root.Trim().Trim('"'));
            string programRoot = Path.Combine(fullRoot, "Program");
            string marker = Path.Combine(programRoot, "project-root.marker");
            if (!File.Exists(marker))
            {
                error = "Project marker is missing: " + marker;
                return false;
            }

            string app = Path.Combine(programRoot, "App");
            string state = Path.Combine(programRoot, "State");
            paths = new LauncherPaths(
                fullRoot,
                marker,
                app,
                state,
                Path.Combine(state, "Logs"),
                Path.Combine(state, "Launch"),
                Path.Combine(state, "current_session.json"),
                Path.Combine(app, "Launcher", "MacroLauncher.exe"),
                Path.Combine(app, "Recorder", "MacroRecorder.exe"),
                Path.Combine(app, "Player", "MacroPlayer.exe"),
                Path.Combine(app, "Watchdog", "MacroSafetyWatchdog.exe"),
                Path.Combine(state, "Logs", "launcher.log"));
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}

public static class LauncherArgumentParser
{
    public static bool TryParse(string[] args, out LauncherRequest? request, out string error)
    {
        request = null;
        error = string.Empty;
        string? root = Value(args, "--project-root");
        string? toolText = Value(args, "--tool");
        string? modeText = Value(args, "--mode");

        if (string.IsNullOrWhiteSpace(root))
        {
            error = "Missing --project-root.";
            return false;
        }

        if (!Enum.TryParse(toolText, true, out LauncherTool tool))
        {
            error = "Invalid --tool. Expected recorder, player, or emergency.";
            return false;
        }

        LauncherMode mode = LauncherMode.Medium;
        if (tool != LauncherTool.Emergency && !Enum.TryParse(modeText, true, out mode))
        {
            error = "Invalid --mode. Expected medium or elevated.";
            return false;
        }

        request = new LauncherRequest(
            tool,
            mode,
            root,
            Value(args, "--preselect"),
            Has(args, "--elevated-stage"),
            Has(args, "--validate-only"),
            Has(args, "--safe-validation"),
            Has(args, "--replacement-cleanup-only"),
            Value(args, "--cleanup-result"),
            Value(args, "--cleanup-token"));
        return true;
    }

    public static string? Value(IReadOnlyList<string> args, string name)
    {
        for (int i = 0; i + 1 < args.Count; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    public static bool Has(IEnumerable<string> args, string name) =>
        args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));
}

public static class LauncherPolicy
{
    public const int ReadyTimeoutSeconds = 10;

    public static bool IsSafeValidation(LauncherRequest request, string? environmentValue) =>
        request.SafeValidation || string.Equals(environmentValue, "1", StringComparison.Ordinal);

    public static bool ShouldRegisterInstallRoot(LauncherRequest request, bool safeValidation) =>
        !safeValidation && !request.ValidateOnly;

    public static bool RequiresElevation(LauncherRequest request, bool currentlyElevated) =>
        request.Mode == LauncherMode.Elevated && !currentlyElevated;

    public static string RequestedMode(LauncherRequest request) => request.Tool switch
    {
        LauncherTool.Recorder when request.Mode == LauncherMode.Elevated => "elevated-recorder",
        LauncherTool.Player when request.Mode == LauncherMode.Elevated => "elevated-player",
        LauncherTool.Recorder => "desktop-safe-recorder",
        LauncherTool.Player => "desktop-safe-player",
        _ => "emergency"
    };

    public static bool IsReadyRecordValid(string json, string token, int pid, out string error)
    {
        error = string.Empty;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            string? actualToken = Property(root, "launchToken")?.GetString();
            string? status = Property(root, "status")?.GetString();
            int actualPid = Property(root, "processId")?.GetInt32() ?? 0;
            if (!string.Equals(actualToken, token, StringComparison.Ordinal) || actualPid != pid)
            {
                error = "READY token or PID does not match the launched process.";
                return false;
            }

            if (!string.Equals(status, "READY", StringComparison.OrdinalIgnoreCase))
            {
                error = Property(root, "detail")?.GetString() ?? "Child reported initialization failure.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = "Invalid READY record: " + ex.Message;
            return false;
        }
    }

    public static bool TryValidateSession(string sessionJson, Process process, out string token, out string error)
    {
        token = string.Empty;
        error = string.Empty;
        try
        {
            using JsonDocument document = JsonDocument.Parse(sessionJson);
            JsonElement root = SelectSession(document.RootElement, process.Id);
            int pid = IntProperty(root, "processId", "pid");
            token = StringProperty(root, "sessionToken", "token") ?? string.Empty;
            DateTimeOffset expectedStart = DateProperty(root, "processStartTimeUtc", "startTimeUtc", "startTime");
            DateTimeOffset actualStart = process.StartTime.ToUniversalTime();

            if (pid <= 0 || pid != process.Id)
            {
                error = "Session PID does not match.";
                return false;
            }

            if (!IsValidSessionToken(token))
            {
                error = "Session token is invalid.";
                return false;
            }

            if (expectedStart == default || Math.Abs((actualStart - expectedStart).TotalSeconds) > 2)
            {
                error = "Session process start time does not match; PID reuse is possible.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = "Invalid current_session.json: " + ex.Message;
            return false;
        }
    }

    public static string? SessionIntegrity(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement? sessions = Property(document.RootElement, "sessions");
        if (sessions is { ValueKind: JsonValueKind.Array })
        {
            string? first = null;
            foreach (JsonElement session in sessions.Value.EnumerateArray())
            {
                string? integrity = StringProperty(session, "integrityLevel", "integrity");
                first ??= integrity;
                if (string.Equals(integrity, "High", StringComparison.OrdinalIgnoreCase))
                {
                    return "High";
                }
            }

            return first;
        }

        return StringProperty(document.RootElement, "integrityLevel", "integrity");
    }

    public static bool TryGetSessionProcessId(string json, out int pid, out string error)
    {
        pid = 0;
        error = string.Empty;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement session = document.RootElement;
            JsonElement? sessions = Property(document.RootElement, "sessions");
            if (sessions is { ValueKind: JsonValueKind.Array })
            {
                JsonElement? only = null;
                int count = 0;
                foreach (JsonElement candidate in sessions.Value.EnumerateArray())
                {
                    only = candidate;
                    count++;
                }

                if (count != 1 || only is null)
                {
                    error = count == 0
                        ? "No active session was found."
                        : "Multiple active sessions require the multi-session emergency coordinator.";
                    return false;
                }

                session = only.Value;
            }
            pid = IntProperty(session, "processId", "pid");
            if (pid <= 0)
            {
                error = "Session PID is missing.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = "Invalid current_session.json: " + ex.Message;
            return false;
        }
    }

    private static JsonElement SelectSession(JsonElement root, int? processId)
    {
        JsonElement? fallback = null;
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!property.Name.Equals("sessions", StringComparison.OrdinalIgnoreCase) ||
                property.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement session in property.Value.EnumerateArray())
            {
                fallback = session;
                if (processId is not null && IntProperty(session, "processId", "pid") == processId.Value)
                {
                    return session;
                }
            }

            if (processId is null && fallback is not null)
            {
                return fallback.Value;
            }

            if (processId is not null)
            {
                throw new InvalidDataException("Requested session PID was not found.");
            }
        }

        return root;
    }

    private static bool IsValidSessionToken(string token)
    {
        if (token.Length is < 16 or > 128)
        {
            return false;
        }

        foreach (char value in token)
        {
            if (!char.IsLetterOrDigit(value) && value is not '-' and not '_')
            {
                return false;
            }
        }

        return true;
    }

    private static JsonElement? Property(JsonElement root, string name)
    {
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static int IntProperty(JsonElement root, params string[] names)
    {
        foreach (string name in names)
        {
            JsonElement? value = Property(root, name);
            if (value is { ValueKind: JsonValueKind.Number } && value.Value.TryGetInt32(out int number))
            {
                return number;
            }
        }

        return 0;
    }

    private static string? StringProperty(JsonElement root, params string[] names)
    {
        foreach (string name in names)
        {
            JsonElement? value = Property(root, name);
            if (value is { ValueKind: JsonValueKind.String })
            {
                return value.Value.GetString();
            }
        }

        return null;
    }

    private static DateTimeOffset DateProperty(JsonElement root, params string[] names)
    {
        string? text = StringProperty(root, names);
        return DateTimeOffset.TryParse(text, out DateTimeOffset parsed) ? parsed : default;
    }
}

public static class ElevationProbe
{
    public static bool IsElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

public enum ChildStartupState
{
    Waiting,
    Ready,
    ChildExited,
    TimedOut
}

public static class ChildStartupPolicy
{
    public static ChildStartupState Evaluate(bool ready, bool childExited, bool deadlineReached)
    {
        if (ready)
        {
            return ChildStartupState.Ready;
        }

        if (childExited)
        {
            return ChildStartupState.ChildExited;
        }

        return deadlineReached ? ChildStartupState.TimedOut : ChildStartupState.Waiting;
    }

    public static bool IsSuccess(ChildStartupState state) => state == ChildStartupState.Ready;
}

public static class LauncherExitCodePolicy
{
    public const int Success = 0;
    public const int UacCancelled = 20;

    public static bool IsCancelled(int exitCode) => exitCode == UacCancelled;
}

public static class FinalRootLayoutPolicy
{
    public static readonly string[] VisibleNames =
    {
        "06_啟動錄製器_一般模式.cmd",
        "06A_啟動錄製器_管理員模式.cmd",
        "07_選擇並重播巨集_一般模式.cmd",
        "07A_選擇並重播巨集_管理員模式.cmd",
        "99_緊急終止巨集工具.cmd",
        "Recordings",
        "Program",
        "Development",
        ".git",
        ".gitignore",
        ".gitattributes",
        "GitHub_上傳版本"
    };

    public static bool IsAllowedVisibleName(string name) =>
        VisibleNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    public static bool IsForbiddenTemporaryName(string name) =>
        name.StartsWith("tmp_", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".dotnet", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".dotnet-cli-home", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("tmp_dotnet_cli", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("diagnostic_publish", StringComparison.OrdinalIgnoreCase);
}

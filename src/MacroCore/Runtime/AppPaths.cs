using System.Text;

namespace MacroCore.Runtime;

public sealed record RuntimePathSet(string ProjectRoot)
{
    public string ProgramDirectory => Path.Combine(ProjectRoot, "Program");
    public string MarkerPath => Path.Combine(ProgramDirectory, "project-root.marker");
    public string AppDirectory => Path.Combine(ProgramDirectory, "App");
    public string StateDirectory => Path.Combine(ProgramDirectory, "State");
    public string LogsDirectory => Path.Combine(StateDirectory, "Logs");
    public string SettingsDirectory => Path.Combine(StateDirectory, "Settings");
    public string LaunchStateDirectory => Path.Combine(StateDirectory, "Launch");
    public string CurrentSessionPath => Path.Combine(StateDirectory, "current_session.json");
    public string ActiveToolLockPath => Path.Combine(StateDirectory, "active_tool.lock");
    public string ActiveToolMetadataPath => Path.Combine(StateDirectory, "active_tool.json");
    public string RecordingsDirectory => Path.Combine(ProjectRoot, "Recordings");
    public string DocsDirectory => Path.Combine(ProgramDirectory, "Docs");
    public string ManualPath => Path.Combine(DocsDirectory, "README_操作手冊.txt");
    public string LauncherPath => Path.Combine(AppDirectory, "Launcher", "MacroLauncher.exe");
    public string RecorderPath => Path.Combine(AppDirectory, "Recorder", "MacroRecorder.exe");
    public string PlayerPath => Path.Combine(AppDirectory, "Player", "MacroPlayer.exe");
    public string WatchdogPath => Path.Combine(AppDirectory, "Watchdog", "MacroSafetyWatchdog.exe");
    public string LauncherLogPath => Path.Combine(LogsDirectory, "launcher.log");
}

public static class RuntimePathResolver
{
    public const string MarkerRelativePath = "Program\\project-root.marker";

    public static bool TryResolve(string[] args, string applicationBase, out RuntimePathSet paths, out string error)
    {
        var explicitRoot = GetOption(args, "--project-root");
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            return TryCreate(explicitRoot, requireMarker: true, out paths, out error);
        }

        var current = new DirectoryInfo(Path.GetFullPath(applicationBase));
        for (var depth = 0; current is not null && depth < 12; depth++, current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, MarkerRelativePath)))
            {
                return TryCreate(current.FullName, requireMarker: true, out paths, out error);
            }
        }

        paths = null!;
        error = "Missing --project-root and no Program\\project-root.marker was found.";
        return false;
    }

    public static bool TryCreate(string root, bool requireMarker, out RuntimePathSet paths, out string error)
    {
        paths = null!;
        error = string.Empty;
        try
        {
            var normalized = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(normalized))
            {
                error = "Project root does not exist: " + normalized;
                return false;
            }

            var candidate = new RuntimePathSet(normalized);
            if (requireMarker && !File.Exists(candidate.MarkerPath))
            {
                error = "Project root marker is missing: " + candidate.MarkerPath;
                return false;
            }

            paths = candidate;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    public static string? GetOption(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index + 1 < args.Count; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }
        return null;
    }
}

public static class AppPaths
{
    private static readonly object Sync = new();
    private static RuntimePathSet? _current;

    public static RuntimePathSet Current
    {
        get
        {
            lock (Sync)
            {
                if (_current is not null)
                {
                    return _current;
                }
                if (!RuntimePathResolver.TryResolve(Array.Empty<string>(), AppContext.BaseDirectory, out var paths, out var error))
                {
                    throw new InvalidOperationException(error);
                }
                return Initialize(paths);
            }
        }
    }

    public static bool TryInitialize(string[] args, out string error)
    {
        if (!RuntimePathResolver.TryResolve(args, AppContext.BaseDirectory, out var paths, out error))
        {
            return false;
        }
        Initialize(paths);
        return true;
    }

    public static RuntimePathSet Initialize(string projectRoot)
    {
        if (!RuntimePathResolver.TryCreate(projectRoot, requireMarker: true, out var paths, out var error))
        {
            throw new InvalidOperationException(error);
        }
        return Initialize(paths);
    }

    private static RuntimePathSet Initialize(RuntimePathSet paths)
    {
        lock (Sync)
        {
            if (_current is not null && !string.Equals(_current.ProjectRoot, paths.ProjectRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("AppPaths was already initialized with a different project root.");
            }
            Directory.CreateDirectory(paths.RecordingsDirectory);
            Directory.CreateDirectory(paths.LogsDirectory);
            Directory.CreateDirectory(paths.SettingsDirectory);
            Directory.CreateDirectory(paths.LaunchStateDirectory);
            _current = paths;
            return paths;
        }
    }
}

public static class RootMarker
{
    public static void Ensure(string projectRoot)
    {
        var root = Path.GetFullPath(projectRoot);
        var programDirectory = Path.Combine(root, "Program");
        Directory.CreateDirectory(programDirectory);
        var marker = Path.Combine(programDirectory, "project-root.marker");
        if (!File.Exists(marker))
        {
            File.WriteAllText(marker, "MOUSE_KEYBOARD_MACRO_ROOT_V2\r\n", Encoding.ASCII);
        }
    }
}

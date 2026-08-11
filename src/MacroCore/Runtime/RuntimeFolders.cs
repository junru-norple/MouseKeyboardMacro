namespace MacroCore.Runtime;

public static class RuntimeFolders
{
    public static string ProjectRoot => AppPaths.Current.ProjectRoot;

    public static string ProgramRoot => Path.Combine(ProjectRoot, "Program");

    public static string AppRoot => Path.Combine(ProgramRoot, "App");

    public static string StateRoot => Path.Combine(ProgramRoot, "State");

    public static string Logs => Path.Combine(StateRoot, "Logs");

    public static string LaunchState => Path.Combine(StateRoot, "Launch");

    public static string Settings => Path.Combine(StateRoot, "Settings");

    public static string CurrentSession => Path.Combine(StateRoot, "current_session.json");

    public static string ActiveToolLock => Path.Combine(StateRoot, "active_tool.lock");

    public static string ActiveToolMetadata => Path.Combine(StateRoot, "active_tool.json");

    public static string Recordings => Path.Combine(ProjectRoot, "Recordings");

    public static string Manual => Path.Combine(ProgramRoot, "Docs", "README_操作手冊.txt");

    public static string Launcher => Path.Combine(AppRoot, "Launcher", "MacroLauncher.exe");

    public static string Watchdog => Path.Combine(AppRoot, "Watchdog", "MacroSafetyWatchdog.exe");

    public static void EnsureRuntimeDirectories()
    {
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(LaunchState);
        Directory.CreateDirectory(Settings);
        Directory.CreateDirectory(Recordings);
    }
}

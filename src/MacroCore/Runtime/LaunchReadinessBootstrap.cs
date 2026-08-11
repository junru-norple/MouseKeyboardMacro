using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace MacroCore.Runtime;

#pragma warning disable CA2255 // Intentional process bootstrap shared by all three GUI executables.
internal static class LaunchReadinessBootstrap
{
    private static int _applicationReady;

    internal static void SignalApplicationReady() => Interlocked.Exchange(ref _applicationReady, 1);

    [ModuleInitializer]
    internal static void Initialize()
    {
        try
        {
            string[] args = Environment.GetCommandLineArgs().Skip(1).ToArray();
            string? explicitRoot = Value(args, "--project-root");
            string? environmentRoot = Environment.GetEnvironmentVariable("MKM_PROJECT_ROOT");
            string? root = explicitRoot ?? environmentRoot ?? FindMarkerRoot(AppContext.BaseDirectory);
            if (!string.IsNullOrWhiteSpace(root))
            {
                AppPaths.Initialize(root);
                RuntimeFolders.EnsureRuntimeDirectories();
            }

            string? token = Value(args, "--launch-token");
            string? readyFile = Value(args, "--ready-file");
            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(readyFile) || string.IsNullOrWhiteSpace(root))
            {
                return;
            }

            string normalizedReady = Path.GetFullPath(readyFile);
            string allowed = Path.GetFullPath(RuntimeFolders.LaunchState).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!normalizedReady.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var thread = new Thread(() => WaitAndReport(token, normalizedReady))
            {
                IsBackground = true,
                Name = "MacroLaunchReadyReporter"
            };
            thread.Start();
        }
        catch
        {
            // Launcher timeout remains the authoritative failure path.
        }
    }

    private static void WaitAndReport(string token, string readyFile)
    {
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(9);
            using Process process = Process.GetCurrentProcess();
            bool windowReady = false;
            bool watchdogSessionReady = false;
            bool applicationReady = false;
            while (DateTime.UtcNow < deadline)
            {
                process.Refresh();
                windowReady = process.MainWindowHandle != IntPtr.Zero;
                watchdogSessionReady = SessionMatchesProcess(RuntimeFolders.CurrentSession, process.Id);
                applicationReady = Volatile.Read(ref _applicationReady) != 0;
                if (windowReady && watchdogSessionReady && applicationReady)
                {
                    WriteReady(readyFile, token, process.Id, "READY", "GUI shown and safety session registered.");
                    return;
                }

                Thread.Sleep(100);
            }

            WriteReady(readyFile, token, process.Id, "FAILED",
                $"Initialization timeout: windowReady={windowReady}, watchdogSessionReady={watchdogSessionReady}, applicationReady={applicationReady}.");
        }
        catch (Exception ex)
        {
            try
            {
                WriteReady(readyFile, token, Environment.ProcessId, "FAILED", ex.GetType().Name + ": " + ex.Message);
            }
            catch
            {
                // Launcher timeout remains the authoritative failure path.
            }
        }
    }

    private static bool SessionMatchesProcess(string path, int pid)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            if (TryElementPid(document.RootElement, out int rootPid))
            {
                return rootPid == pid;
            }

            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (property.Name.Equals("sessions", StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement session in property.Value.EnumerateArray())
                    {
                        if (TryElementPid(session, out int sessionPid) && sessionPid == pid)
                        {
                            return true;
                        }
                    }
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool TryElementPid(JsonElement element, out int pid)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if ((property.Name.Equals("processId", StringComparison.OrdinalIgnoreCase) ||
                 property.Name.Equals("pid", StringComparison.OrdinalIgnoreCase)) &&
                property.Value.TryGetInt32(out int sessionPid))
            {
                pid = sessionPid;
                return true;
            }
        }

        pid = 0;
        return false;
    }

    private static void WriteReady(string path, string token, int pid, string status, string detail)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var payload = new
        {
            launchToken = token,
            processId = pid,
            role = Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "MacroTool",
            status,
            detail,
            timestampUtc = DateTimeOffset.UtcNow
        };
        File.WriteAllText(temporary, JsonSerializer.Serialize(payload), new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    private static string? FindMarkerRoot(string start)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(start));
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Program", "project-root.marker")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static string? Value(IReadOnlyList<string> args, string name)
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
}
#pragma warning restore CA2255

public static class LaunchReadiness
{
    public static void SignalApplicationReady() => LaunchReadinessBootstrap.SignalApplicationReady();
}

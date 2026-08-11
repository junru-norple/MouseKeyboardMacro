using System.Diagnostics;
using System.IO.Pipes;
using MacroCore.Diagnostics;
using MacroCore.Runtime;

namespace MacroSafetyWatchdog;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (!AppPaths.TryInitialize(args, out _))
        {
            return 2;
        }
        RuntimeFolders.EnsureRuntimeDirectories();
        if (args.Any(value => value.Equals("--validate-only", StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(Environment.GetEnvironmentVariable("MKM_SAFE_VALIDATION_MODE"), "1", StringComparison.Ordinal))
        {
            return Directory.Exists(RuntimeFolders.StateRoot) ? 0 : 65;
        }
        var options = Parse(args);
        if (!TryRequired(options, "--process-pid", out var pidText) || !int.TryParse(pidText, out var pid) ||
            !TryRequired(options, "--process-start-utc", out var startText) || !DateTime.TryParse(startText, null, System.Globalization.DateTimeStyles.RoundtripKind, out var startUtc) ||
            !TryRequired(options, "--process-name", out var processName) ||
            !TryRequired(options, "--session-token", out var token) ||
            !TryRequired(options, "--role", out var role) ||
            !TryRequired(options, "--pipe", out var pipeName) ||
            !TryRequired(options, "--session-file", out var sessionFile))
        {
            Log("startup=invalid_arguments");
            return 2;
        }

        Log($"startup=parsed pid={pid} role={role} process={processName}");

        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.In, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(3000);
        }
        catch
        {
            Log("pipe=connect_failed exit=3");
            return 3;
        }

        Log("pipe=connected");

        var buffer = new byte[1];
        while (true)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                var read = await pipe.ReadAsync(buffer, timeout.Token);
                if (read == 0)
                {
                    await TerminateExactAsync(pid, startUtc.ToUniversalTime(), processName, role, token, sessionFile);
                    return 10;
                }
                if (buffer[0] == 0xFF)
                {
                    Log("heartbeat=normal_shutdown exit=0");
                    return 0;
                }
            }
            catch (OperationCanceledException)
            {
                await TerminateExactAsync(pid, startUtc.ToUniversalTime(), processName, role, token, sessionFile);
                Log("heartbeat=timeout exit=11");
                return 11;
            }
            catch
            {
                await TerminateExactAsync(pid, startUtc.ToUniversalTime(), processName, role, token, sessionFile);
                Log("heartbeat=read_failed exit=12");
                return 12;
            }
        }
    }

    private static Task TerminateExactAsync(int pid, DateTime startUtc, string processName, string role, string token, string sessionFile)
    {
        var record = CurrentSessionStore.Read(sessionFile).Sessions.SingleOrDefault(item =>
            string.Equals(item.Role, role, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.SessionToken, token, StringComparison.Ordinal) &&
            item.Pid == pid && Math.Abs((item.StartTimeUtc - startUtc).TotalSeconds) < 1);
        if (record is null || !CurrentSessionStore.IsExactProcess(pid, startUtc, processName))
        {
            return Task.CompletedTask;
        }

        try
        {
            using var target = Process.GetProcessById(pid);
            target.Kill(true);
            target.WaitForExit(3000);
        }
        catch
        {
        }
        return Task.CompletedTask;
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index + 1 < args.Length; index += 2)
        {
            result[args[index]] = args[index + 1];
        }
        return result;
    }

    private static bool TryRequired(IReadOnlyDictionary<string, string> options, string key, out string value) =>
        options.TryGetValue(key, out value!) && !string.IsNullOrWhiteSpace(value);

    private static void Log(string message)
    {
        try
        {
            MacroCore.Diagnostics.RotatingLog.Write(
                Path.Combine(RuntimeFolders.Logs, "watchdog_startup.log"),
                $"{DateTimeOffset.Now:O} {message}");
        }
        catch
        {
        }
    }
}

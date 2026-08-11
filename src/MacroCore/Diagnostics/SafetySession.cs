using System.Diagnostics;
using System.Text.Json;
using MacroCore.Runtime;

namespace MacroCore.Diagnostics;

public sealed class SafetySessionMetadata
{
    public string Token { get; set; } = string.Empty;
    public int RecorderPid { get; set; }
    public long RecorderStartTimeUtcTicks { get; set; }
    public int PlayerPid { get; set; }
    public long PlayerStartTimeUtcTicks { get; set; }
    public int WatchdogPid { get; set; }
    public long WatchdogStartTimeUtcTicks { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public readonly record struct ProcessIdentity(int Pid, long StartTimeUtcTicks);

public static class SafetyProcessGuard
{
    public static bool Matches(ProcessIdentity expected, ProcessIdentity actual) =>
        expected.Pid > 0 && expected.Pid == actual.Pid && expected.StartTimeUtcTicks == actual.StartTimeUtcTicks;

    public static bool ShouldTerminateRecorder(
        SafetySessionMetadata metadata,
        string expectedToken,
        ProcessIdentity actualRecorder) =>
        !string.IsNullOrWhiteSpace(expectedToken) &&
        string.Equals(metadata.Token, expectedToken, StringComparison.Ordinal) &&
        Matches(new ProcessIdentity(metadata.RecorderPid, metadata.RecorderStartTimeUtcTicks), actualRecorder);
}

public static class HeartbeatProtocol
{
    public static bool IsHeartbeat(string? line, string token) =>
        line is not null && line.Equals("HEARTBEAT " + token, StringComparison.Ordinal);

    public static bool IsNormalExit(string? line, string token) =>
        line is not null && line.Equals("EXIT " + token, StringComparison.Ordinal);
}

public static class SafetySessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string ResolveLogsDirectory()
    {
        Directory.CreateDirectory(RuntimeFolders.Logs);
        return RuntimeFolders.Logs;
    }

    public static string CurrentSessionPath => RuntimeFolders.CurrentSession;

    public static SafetySessionMetadata Read(string? path = null)
    {
        path ??= CurrentSessionPath;
        try
        {
            if (!File.Exists(path))
            {
                return new SafetySessionMetadata();
            }
            return JsonSerializer.Deserialize<SafetySessionMetadata>(File.ReadAllText(path), JsonOptions) ?? new SafetySessionMetadata();
        }
        catch
        {
            return new SafetySessionMetadata();
        }
    }

    public static void Write(SafetySessionMetadata metadata, string? path = null)
    {
        path ??= CurrentSessionPath;
        metadata.UpdatedAt = DateTimeOffset.UtcNow;
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ResolveLogsDirectory());
        var temporary = path + ".tmp." + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, JsonSerializer.Serialize(metadata, JsonOptions));
        File.Move(temporary, path, true);
    }

    public static ProcessIdentity CurrentProcessIdentity()
    {
        using var process = Process.GetCurrentProcess();
        return new ProcessIdentity(process.Id, process.StartTime.ToUniversalTime().Ticks);
    }

    public static ProcessIdentity? TryGetIdentity(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return new ProcessIdentity(pid, process.StartTime.ToUniversalTime().Ticks);
        }
        catch
        {
            return null;
        }
    }
}

public static class ReleaseSmokePolicy
{
    public static bool CanLaunchWithoutInput(string applicationName, IReadOnlyList<string> arguments) =>
        applicationName is "MacroRecorder" or "MacroPlayer" or "MacroSafetyWatchdog" &&
        arguments.All(argument => !argument.Equals("--play", StringComparison.OrdinalIgnoreCase));
}

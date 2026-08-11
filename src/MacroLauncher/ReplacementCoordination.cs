using System.Diagnostics;
using System.Text.Json;
using MacroCore.Diagnostics;
using MacroCore.Runtime;

namespace MacroLauncher;

public enum ReplacementShutdownReason
{
    NewRecorder,
    NewPlayer,
    ElevatedCleanupOnly
}

public enum ReplacementSessionOutcome
{
    CooperativelyClosed,
    ForceClosed,
    StaleRemoved,
    Failed
}

public sealed record ReplacementSessionResult(
    WatchdogSessionRecord Session,
    ReplacementSessionOutcome Outcome,
    bool DiscardedActiveRecording,
    string Detail);

public sealed record ReplacementShutdownSummary(IReadOnlyList<ReplacementSessionResult> Results)
{
    public int RecorderCount => Results.Count(item => item.Outcome != ReplacementSessionOutcome.StaleRemoved && item.Session.Role.Equals("Recorder", StringComparison.OrdinalIgnoreCase));
    public int PlayerCount => Results.Count(item => item.Outcome != ReplacementSessionOutcome.StaleRemoved && item.Session.Role.Equals("Player", StringComparison.OrdinalIgnoreCase));
    public int CooperativeClosed => Results.Count(item => item.Outcome == ReplacementSessionOutcome.CooperativelyClosed);
    public int ForcedClosed => Results.Count(item => item.Outcome == ReplacementSessionOutcome.ForceClosed);
    public int StaleRemoved => Results.Count(item => item.Outcome == ReplacementSessionOutcome.StaleRemoved);
    public int Failed => Results.Count(item => item.Outcome == ReplacementSessionOutcome.Failed);
    public int DiscardedActiveRecordings => Results.Count(item => item.DiscardedActiveRecording);
    public bool HasFailedHighSession => Results.Any(item =>
        item.Outcome == ReplacementSessionOutcome.Failed &&
        item.Session.IntegrityLevel.Equals("High", StringComparison.OrdinalIgnoreCase));
}

public interface IReplacementRuntime
{
    bool IsExactLiveSession(WatchdogSessionRecord session);
    Task<bool> RequestCooperativeShutdownAsync(WatchdogSessionRecord session, TimeSpan timeout);
    bool WaitForExit(WatchdogSessionRecord session, TimeSpan timeout);
    bool ForceStopExact(WatchdogSessionRecord session);
    bool CleanupWatchdogExact(WatchdogSessionRecord session);
}

public sealed class MacroToolReplacementCoordinator
{
    public async Task<ReplacementShutdownSummary> ShutdownAllAsync(
        IEnumerable<WatchdogSessionRecord> sessions,
        IReplacementRuntime runtime,
        ReplacementShutdownReason reason,
        bool allowExactForceFallback)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(runtime);
        _ = reason;
        List<ReplacementSessionResult> results = [];
        foreach (WatchdogSessionRecord session in sessions
                     .GroupBy(item => (item.Role.ToUpperInvariant(), item.Pid, item.SessionToken))
                     .Select(group => group.Last()))
        {
            bool discarded = session.Role.Equals("Recorder", StringComparison.OrdinalIgnoreCase) &&
                             session.ActivityState.Equals("Recording", StringComparison.OrdinalIgnoreCase);
            if (!CurrentSessionStore.IsSessionIdentityWellFormed(session) || !runtime.IsExactLiveSession(session))
            {
                results.Add(new ReplacementSessionResult(session, ReplacementSessionOutcome.StaleRemoved, false,
                    "identity mismatch or process exited"));
                continue;
            }

            bool acknowledged = await runtime.RequestCooperativeShutdownAsync(session, TimeSpan.FromMilliseconds(1750)).ConfigureAwait(false);
            if (acknowledged && runtime.WaitForExit(session, TimeSpan.FromMilliseconds(3000)))
            {
                bool watchdogClean = runtime.CleanupWatchdogExact(session);
                results.Add(new ReplacementSessionResult(session, ReplacementSessionOutcome.CooperativelyClosed, discarded,
                    watchdogClean ? "replacement ACK and exact exit" : "replacement ACK; watchdog cleanup pending"));
                continue;
            }

            if (allowExactForceFallback && runtime.ForceStopExact(session))
            {
                _ = runtime.CleanupWatchdogExact(session);
                results.Add(new ReplacementSessionResult(session, ReplacementSessionOutcome.ForceClosed, discarded,
                    acknowledged ? "ACK received; exact fallback required" : "cooperative timeout; exact fallback"));
                continue;
            }

            results.Add(new ReplacementSessionResult(session, ReplacementSessionOutcome.Failed, false,
                acknowledged ? "ACK received but exact exit did not complete" : "cooperative timeout or access denied"));
        }
        return new ReplacementShutdownSummary(results);
    }
}

public sealed class WindowsReplacementRuntime : IReplacementRuntime
{
    private readonly WindowsEmergencyStopRuntime _exact = new();
    public bool IsExactLiveSession(WatchdogSessionRecord session) => _exact.IsExactLiveSession(session);
    public Task<bool> RequestCooperativeShutdownAsync(WatchdogSessionRecord session, TimeSpan timeout) =>
        ReplacementShutdownClient.RequestAsync(session.EmergencyEndpoint, session.SessionToken, timeout);
    public bool WaitForExit(WatchdogSessionRecord session, TimeSpan timeout) => _exact.WaitForExit(session, timeout);
    public bool ForceStopExact(WatchdogSessionRecord session) => _exact.ForceStopExact(session);
    public bool CleanupWatchdogExact(WatchdogSessionRecord session) => _exact.CleanupWatchdogExact(session);
}

public interface ILaunchCoordinatorLock : IDisposable
{
    string Token { get; }
    string LockPath { get; }
}

public sealed class ProjectLaunchCoordinatorLock : ILaunchCoordinatorLock
{
    private readonly FileStream _stream;
    private int _disposed;

    private ProjectLaunchCoordinatorLock(string path, FileStream stream)
    {
        LockPath = path;
        _stream = stream;
        Token = Convert.ToHexString(Guid.NewGuid().ToByteArray());
    }

    public string Token { get; }
    public string LockPath { get; }

    public static ProjectLaunchCoordinatorLock Acquire(string path, TimeSpan timeout)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        Stopwatch timer = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                FileStream stream = new(fullPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
                return new ProjectLaunchCoordinatorLock(fullPath, stream);
            }
            catch (IOException) when (timer.Elapsed < timeout)
            {
                Thread.Sleep(100);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _stream.Dispose();
        }
    }
}

public sealed class ReplacementCleanupResult
{
    public string Token { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int CooperativeClosed { get; set; }
    public int ForcedClosed { get; set; }
    public int StaleRemoved { get; set; }
    public int Failed { get; set; }
}

public static class ReplacementResultStore
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static void Write(string path, ReplacementCleanupResult result)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp." + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, JsonSerializer.Serialize(result, Options));
        File.Move(temporary, path, true);
    }

    public static bool TryRead(string path, string token, out ReplacementCleanupResult? result)
    {
        result = null;
        try
        {
            result = JsonSerializer.Deserialize<ReplacementCleanupResult>(File.ReadAllText(path), Options);
            return result is not null && string.Equals(result.Token, token, StringComparison.Ordinal) &&
                   string.Equals(result.Status, "PASS", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }
}

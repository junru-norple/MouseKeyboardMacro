using System.Diagnostics;
using MacroCore.Diagnostics;

namespace MacroLauncher;

public enum EmergencySessionOutcome
{
    CooperativelyStopped,
    ForceStopped,
    StaleRemoved,
    Failed
}

public sealed record EmergencySessionResult(WatchdogSessionRecord Session, EmergencySessionOutcome Outcome, string Detail);

public sealed record EmergencyStopSummary(IReadOnlyList<EmergencySessionResult> Results)
{
    public int Found => Results.Count(item => item.Outcome != EmergencySessionOutcome.StaleRemoved);
    public int CooperativelyStopped => Results.Count(item => item.Outcome == EmergencySessionOutcome.CooperativelyStopped);
    public int ForceStopped => Results.Count(item => item.Outcome == EmergencySessionOutcome.ForceStopped);
    public int StaleRemoved => Results.Count(item => item.Outcome == EmergencySessionOutcome.StaleRemoved);
    public int Failed => Results.Count(item => item.Outcome == EmergencySessionOutcome.Failed);
}

public interface IEmergencyStopRuntime
{
    bool IsExactLiveSession(WatchdogSessionRecord session);
    Task<bool> RequestCooperativeStopAsync(WatchdogSessionRecord session, TimeSpan timeout);
    bool WaitForExit(WatchdogSessionRecord session, TimeSpan timeout);
    bool ForceStopExact(WatchdogSessionRecord session);
    bool CleanupWatchdogExact(WatchdogSessionRecord session);
}

public sealed class EmergencyStopCoordinator
{
    public async Task<EmergencyStopSummary> StopAllAsync(
        IEnumerable<WatchdogSessionRecord> sessions,
        IEmergencyStopRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(runtime);
        List<EmergencySessionResult> results = [];
        foreach (WatchdogSessionRecord session in sessions
                     .GroupBy(item => (item.Role.ToUpperInvariant(), item.Pid, item.SessionToken), EqualityComparer<(string, int, string)>.Default)
                     .Select(group => group.Last()))
        {
            if (!CurrentSessionStore.IsSessionIdentityWellFormed(session) || !runtime.IsExactLiveSession(session))
            {
                results.Add(new EmergencySessionResult(session, EmergencySessionOutcome.StaleRemoved, "identity mismatch or process exited"));
                continue;
            }

            bool acknowledged = await runtime.RequestCooperativeStopAsync(session, TimeSpan.FromMilliseconds(1250)).ConfigureAwait(false);
            if (acknowledged && runtime.WaitForExit(session, TimeSpan.FromMilliseconds(1500)))
            {
                _ = runtime.CleanupWatchdogExact(session);
                results.Add(new EmergencySessionResult(session, EmergencySessionOutcome.CooperativelyStopped, "ACK received"));
                continue;
            }

            if (runtime.ForceStopExact(session))
            {
                _ = runtime.CleanupWatchdogExact(session);
                results.Add(new EmergencySessionResult(session, EmergencySessionOutcome.ForceStopped, acknowledged ? "ACK received; exact fallback required" : "no ACK; exact fallback"));
            }
            else
            {
                results.Add(new EmergencySessionResult(session, EmergencySessionOutcome.Failed, "exact identity changed or access denied"));
            }
        }

        return new EmergencyStopSummary(results);
    }
}

public sealed class WindowsEmergencyStopRuntime : IEmergencyStopRuntime
{
    public bool IsExactLiveSession(WatchdogSessionRecord session) =>
        CurrentSessionStore.IsExactProcess(session.Pid, session.StartTimeUtc, session.ProcessName);

    public Task<bool> RequestCooperativeStopAsync(WatchdogSessionRecord session, TimeSpan timeout) =>
        EmergencyStopClient.RequestAsync(session.EmergencyEndpoint, session.SessionToken, timeout);

    public bool WaitForExit(WatchdogSessionRecord session, TimeSpan timeout)
    {
        try
        {
            using Process process = Process.GetProcessById(session.Pid);
            return process.WaitForExit((int)timeout.TotalMilliseconds);
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool ForceStopExact(WatchdogSessionRecord session)
    {
        if (!IsExactLiveSession(session))
        {
            return false;
        }
        try
        {
            using Process process = Process.GetProcessById(session.Pid);
            process.Kill(entireProcessTree: true);
            return process.WaitForExit(5000);
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool CleanupWatchdogExact(WatchdogSessionRecord session)
    {
        if (session.WatchdogPid <= 0)
        {
            return true;
        }
        try
        {
            using Process watchdog = Process.GetProcessById(session.WatchdogPid);
            string expected = string.IsNullOrWhiteSpace(session.WatchdogProcessName) ? "MacroSafetyWatchdog" : session.WatchdogProcessName;
            if (!string.Equals(watchdog.ProcessName, expected, StringComparison.OrdinalIgnoreCase) ||
                Math.Abs((watchdog.StartTime.ToUniversalTime() - session.WatchdogStartTimeUtc).TotalSeconds) >= 1)
            {
                return false;
            }
            if (watchdog.WaitForExit(1000))
            {
                return true;
            }
            watchdog.Kill(entireProcessTree: true);
            return watchdog.WaitForExit(3000);
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }
}

public static class EmergencyElevationDecision
{
    public static bool RequiresSingleElevation(IEnumerable<WatchdogSessionRecord> sessions, bool currentlyElevated) =>
        !currentlyElevated && sessions.Any(item => item.IntegrityLevel.Equals("High", StringComparison.OrdinalIgnoreCase));
}

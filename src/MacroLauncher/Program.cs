using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;
using MacroCore.Diagnostics;
using MacroCore.Runtime;

namespace MacroLauncher;

internal static class Program
{
    private const int Success = 0;
    private const int InvalidArguments = 10;
    private const int MissingDependency = 11;
    private const int ChildFailed = 12;
    private const int ReadyTimeout = 13;
    private const int UacCancelled = 20;
    private const int EmergencyValidationFailed = 30;

    [STAThread]
    private static int Main(string[] args)
    {
        if (!LauncherArgumentParser.TryParse(args, out LauncherRequest? request, out string parseError) || request is null)
        {
            Console.Error.WriteLine(parseError);
            return InvalidArguments;
        }

        if (!LauncherPaths.TryCreate(request.ProjectRoot, out LauncherPaths? paths, out string pathError) || paths is null)
        {
            Console.Error.WriteLine(pathError);
            return MissingDependency;
        }

        var log = new RotatingTextLog(paths.LauncherLogPath);
        try
        {
            bool safeValidation = LauncherPolicy.IsSafeValidation(request, Environment.GetEnvironmentVariable("MKM_SAFE_VALIDATION_MODE"));
            log.Write($"request tool={request.Tool} mode={request.Mode} root={paths.ProjectRoot} elevatedStage={request.ElevatedStage} validateOnly={request.ValidateOnly} safeValidation={safeValidation}");
            Directory.CreateDirectory(paths.LaunchRoot);

            if (safeValidation)
            {
                return RunSafeValidation(request, paths, log);
            }

            if (LauncherPolicy.ShouldRegisterInstallRoot(request, safeValidation) &&
                !InstallRootLocator.TryRegister(paths.ProjectRoot, new RegistryInstallRootStore(), out var locatorError))
            {
                return Fail(log, MissingDependency, locatorError);
            }

            if (request.Tool == LauncherTool.Emergency)
            {
                return RunEmergency(request, paths, log);
            }

            if (!ValidateApplicationFiles(request, paths, out string validationError))
            {
                return Fail(log, MissingDependency, validationError);
            }

            bool elevated = ElevationProbe.IsElevated();
            if (request.ValidateOnly)
            {
                log.Write($"validation passed elevated={elevated}");
                return Success;
            }

            if (request.ReplacementCleanupOnly)
            {
                return RunReplacementCleanupOnly(request, paths, log, elevated);
            }

            ILaunchCoordinatorLock? launchCoordinator = null;
            try
            {
                if (!request.ElevatedStage)
                {
                    launchCoordinator = ProjectLaunchCoordinatorLock.Acquire(
                        paths.LaunchCoordinatorLockPath, TimeSpan.FromSeconds(30));
                }

                if (LauncherPolicy.RequiresElevation(request, elevated))
                {
                    return RelaunchElevated(request, paths, log);
                }

                return ReplaceExistingToolsAndLaunch(request, paths, log, elevated);
            }
            finally
            {
                launchCoordinator?.Dispose();
            }
        }
        catch (Exception ex)
        {
            return Fail(log, ChildFailed, ex.ToString());
        }
    }

    private static int RunSafeValidation(LauncherRequest request, LauncherPaths paths, RotatingTextLog log)
    {
        if (request.Tool == LauncherTool.Emergency)
        {
            log.Write("safe validation: emergency path parsed; no session read, elevation, or termination executed");
            return Success;
        }

        if (!ValidateApplicationFiles(request, paths, out string validationError, checkRuntime: false))
        {
            return Fail(log, MissingDependency, validationError);
        }

        string childPath = request.Tool == LauncherTool.Recorder ? paths.RecorderPath : paths.PlayerPath;
        int childResult = RunSafeChild(childPath, ["--project-root", paths.ProjectRoot, "--safe-smoke"], log);
        if (childResult != Success)
        {
            return childResult;
        }

        int watchdogResult = RunSafeChild(paths.WatchdogPath, ["--project-root", paths.ProjectRoot, "--validate-only"], log);
        if (watchdogResult == Success)
        {
            log.Write($"safe validation passed tool={request.Tool}; registry=NO uac=NO input=NO hooks=NO watchdogKill=NO");
        }
        return watchdogResult;
    }

    private static int RunSafeChild(string executable, IReadOnlyList<string> arguments, RotatingTextLog log)
    {
        var info = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        info.Environment["MKM_SAFE_VALIDATION_MODE"] = "1";
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using Process? child = Process.Start(info);
        if (child is null)
        {
            return Fail(log, ChildFailed, "Safe-smoke child did not start: " + executable);
        }
        DateTime startUtc = child.StartTime.ToUniversalTime();
        if (!child.WaitForExit(10000))
        {
            if (!child.HasExited && child.StartTime.ToUniversalTime() == startUtc)
            {
                child.Kill(entireProcessTree: true);
                child.WaitForExit(5000);
            }
            return Fail(log, ReadyTimeout, "Safe-smoke child timed out: " + executable);
        }
        return child.ExitCode == 0 ? Success : Fail(log, ChildFailed, $"Safe-smoke child failed: {executable}; exit={child.ExitCode}");
    }

    private static bool ValidateApplicationFiles(LauncherRequest request, LauncherPaths paths, out string error, bool checkRuntime = true)
    {
        error = string.Empty;
        string child = request.Tool == LauncherTool.Recorder ? paths.RecorderPath : paths.PlayerPath;
        foreach (string file in new[] { child, paths.WatchdogPath })
        {
            if (!File.Exists(file))
            {
                error = "Required application file is missing: " + file;
                return false;
            }
        }

        if (checkRuntime && !File.Exists(Path.Combine(paths.StateRoot, "..", "self-contained.marker")) && !HasWindowsDesktopRuntime())
        {
            error = "Microsoft .NET 8 Desktop Runtime (x64) was not detected.";
            return false;
        }

        return true;
    }

    private static bool HasWindowsDesktopRuntime()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "--list-runtimes",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (process is null)
            {
                return false;
            }

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return process.ExitCode == 0 && output.Contains("Microsoft.WindowsDesktop.App 8.", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static int RelaunchElevated(LauncherRequest request, LauncherPaths paths, RotatingTextLog log)
    {
        var arguments = new List<string>
        {
            "--tool", request.Tool.ToString().ToLowerInvariant(),
            "--mode", request.Mode.ToString().ToLowerInvariant(),
            "--project-root", paths.ProjectRoot,
            "--elevated-stage"
        };
        if (!string.IsNullOrWhiteSpace(request.PreselectPath))
        {
            arguments.Add("--preselect");
            arguments.Add(Path.GetFullPath(request.PreselectPath));
        }
        if (request.ReplacementCleanupOnly)
        {
            arguments.Add("--replacement-cleanup-only");
            if (!string.IsNullOrWhiteSpace(request.CleanupResultPath))
            {
                arguments.Add("--cleanup-result");
                arguments.Add(request.CleanupResultPath);
            }
            if (!string.IsNullOrWhiteSpace(request.CleanupToken))
            {
                arguments.Add("--cleanup-token");
                arguments.Add(request.CleanupToken);
            }
        }

        try
        {
            using Process? elevated = Process.Start(new ProcessStartInfo
            {
                FileName = paths.LauncherPath,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(paths.LauncherPath)!,
                ArgumentList = { }
            }.WithArguments(arguments));
            if (elevated is null)
            {
                return Fail(log, ChildFailed, "Elevated launcher did not start.");
            }

            elevated.WaitForExit();
            log.Write($"elevated launcher exit={elevated.ExitCode}");
            return elevated.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Console.WriteLine("Administrator mode was cancelled.");
            log.Write("UAC cancelled by user");
            return UacCancelled;
        }
    }

    private static int ReplaceExistingToolsAndLaunch(
        LauncherRequest request,
        LauncherPaths paths,
        RotatingTextLog log,
        bool elevated)
    {
        ReplacementShutdownReason reason = request.Tool == LauncherTool.Recorder
            ? ReplacementShutdownReason.NewRecorder
            : ReplacementShutdownReason.NewPlayer;
        log.Write($"REPLACEMENT_BEGIN newRole={request.Tool} newMode={request.Mode}");
        Console.WriteLine("正在安全關閉既有巨集工具……");

        for (int pass = 0; pass < 2; pass++)
        {
            WatchdogSessionRecord[] sessions = CurrentSessionStore.Read(paths.CurrentSessionPath).Sessions.ToArray();
            ReplacementShutdownSummary summary = new MacroToolReplacementCoordinator()
                .ShutdownAllAsync(sessions, new WindowsReplacementRuntime(), reason, allowExactForceFallback: true)
                .GetAwaiter().GetResult();
            ApplyReplacementSummary(summary, paths, log);

            if (summary.Failed > 0)
            {
                if (!elevated && summary.HasFailedHighSession)
                {
                    int cleanup = RunElevatedReplacementCleanup(request, paths, log);
                    if (cleanup != Success)
                    {
                        Console.Error.WriteLine("無法安全關閉既有管理員工具；新工具未啟動。");
                        return cleanup;
                    }
                }
                else
                {
                    return Fail(log, ChildFailed, "無法安全關閉既有巨集工具；新工具未啟動。");
                }
            }

            if (WaitForExclusiveLeaseRelease(paths.StateRoot, TimeSpan.FromSeconds(5)))
            {
                CurrentSessionDocument remaining = CurrentSessionStore.Read(paths.CurrentSessionPath);
                WatchdogSessionRecord[] liveRemaining = remaining.Sessions
                    .Where(CurrentSessionStore.IsSessionIdentityWellFormed)
                    .Where(item => CurrentSessionStore.IsExactProcess(item.Pid, item.StartTimeUtc, item.ProcessName))
                    .ToArray();
                if (liveRemaining.Length == 0)
                {
                    Console.WriteLine($"已關閉：Recorder {summary.RecorderCount}、Player {summary.PlayerCount}；正在啟動新的 {request.Tool}。");
                    if (summary.DiscardedActiveRecordings > 0)
                    {
                        Console.WriteLine("既有錄製工作已取消，未儲存不完整巨集。");
                    }
                    log.Write($"REPLACEMENT_COMPLETE cooperative={summary.CooperativeClosed} forced={summary.ForcedClosed} stale={summary.StaleRemoved} discardedRecording={summary.DiscardedActiveRecordings}");
                    return LaunchAndWaitReady(request, paths, log);
                }
            }

            Thread.Sleep(250);
        }

        return Fail(log, ChildFailed,
            "既有工具尚未釋放 active_tool lease；為避免同時執行，新工具未啟動。");
    }

    private static void ApplyReplacementSummary(
        ReplacementShutdownSummary summary,
        LauncherPaths paths,
        RotatingTextLog log)
    {
        foreach (ReplacementSessionResult result in summary.Results)
        {
            string marker = result.Outcome == ReplacementSessionOutcome.ForceClosed
                ? "REPLACEMENT_FORCED_STOP"
                : "REPLACEMENT_SESSION_STOPPED";
            log.Write($"{marker} role={result.Session.Role} pid={result.Session.Pid} outcome={result.Outcome} discardedRecording={result.DiscardedActiveRecording} detail={result.Detail}");
            if (result.Outcome != ReplacementSessionOutcome.Failed)
            {
                CurrentSessionStore.RemoveExact(result.Session, paths.CurrentSessionPath);
            }
        }
    }

    private static bool WaitForExclusiveLeaseRelease(string stateRoot, TimeSpan timeout)
    {
        Stopwatch timer = Stopwatch.StartNew();
        do
        {
            if (MacroToolExclusiveLease.IsAvailable(stateRoot))
            {
                return true;
            }
            Thread.Sleep(100);
        } while (timer.Elapsed < timeout);
        return false;
    }

    private static int RunElevatedReplacementCleanup(LauncherRequest request, LauncherPaths paths, RotatingTextLog log)
    {
        string token = Convert.ToHexString(Guid.NewGuid().ToByteArray());
        string resultPath = Path.Combine(paths.LaunchRoot, "replacement_cleanup_" + token + ".json");
        List<string> arguments =
        [
            "--tool", request.Tool.ToString().ToLowerInvariant(),
            "--mode", "elevated",
            "--project-root", paths.ProjectRoot,
            "--elevated-stage",
            "--replacement-cleanup-only",
            "--cleanup-result", resultPath,
            "--cleanup-token", token
        ];
        try
        {
            using Process? helper = Process.Start(new ProcessStartInfo
            {
                FileName = paths.LauncherPath,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(paths.LauncherPath)!
            }.WithArguments(arguments));
            if (helper is null)
            {
                return Fail(log, ChildFailed, "Elevated replacement cleanup helper did not start.");
            }
            helper.WaitForExit();
            ReplacementCleanupResult? cleanup = null;
            bool valid = helper.ExitCode == Success && ReplacementResultStore.TryRead(resultPath, token, out cleanup);
            TryDelete(resultPath);
            if (!valid || cleanup is null)
            {
                return Fail(log, ChildFailed, $"Elevated replacement cleanup failed; exit={helper.ExitCode}.");
            }
            log.Write($"replacement elevated cleanup pass cooperative={cleanup.CooperativeClosed} forced={cleanup.ForcedClosed} stale={cleanup.StaleRemoved}");
            return Success;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            TryDelete(resultPath);
            log.Write("replacement cleanup UAC cancelled; new tool not launched");
            return UacCancelled;
        }
    }

    private static int RunReplacementCleanupOnly(
        LauncherRequest request,
        LauncherPaths paths,
        RotatingTextLog log,
        bool elevated)
    {
        if (!elevated || string.IsNullOrWhiteSpace(request.CleanupResultPath) || string.IsNullOrWhiteSpace(request.CleanupToken))
        {
            return Fail(log, InvalidArguments, "Replacement cleanup-only requires elevation, result path, and token.");
        }
        string resultPath = Path.GetFullPath(request.CleanupResultPath);
        string allowed = Path.GetFullPath(paths.LaunchRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resultPath.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
        {
            return Fail(log, InvalidArguments, "Replacement cleanup result must remain in Program\\State\\Launch.");
        }

        WatchdogSessionRecord[] sessions = CurrentSessionStore.Read(paths.CurrentSessionPath).Sessions.ToArray();
        ReplacementShutdownSummary summary = new MacroToolReplacementCoordinator()
            .ShutdownAllAsync(sessions, new WindowsReplacementRuntime(), ReplacementShutdownReason.ElevatedCleanupOnly, true)
            .GetAwaiter().GetResult();
        ApplyReplacementSummary(summary, paths, log);
        bool released = summary.Failed == 0 && WaitForExclusiveLeaseRelease(paths.StateRoot, TimeSpan.FromSeconds(5));
        ReplacementResultStore.Write(resultPath, new ReplacementCleanupResult
        {
            Token = request.CleanupToken,
            Status = released ? "PASS" : "FAIL",
            CooperativeClosed = summary.CooperativeClosed,
            ForcedClosed = summary.ForcedClosed,
            StaleRemoved = summary.StaleRemoved,
            Failed = summary.Failed + (released ? 0 : 1)
        });
        return released ? Success : EmergencyValidationFailed;
    }

    private static int LaunchAndWaitReady(LauncherRequest request, LauncherPaths paths, RotatingTextLog log)
    {
        string childPath = request.Tool == LauncherTool.Recorder ? paths.RecorderPath : paths.PlayerPath;
        string token = Guid.NewGuid().ToString("D");
        string readyFile = Path.Combine(paths.LaunchRoot, token + ".ready.json");
        if (File.Exists(readyFile))
        {
            File.Delete(readyFile);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = childPath,
            WorkingDirectory = Path.GetDirectoryName(childPath)!,
            UseShellExecute = false
        };
        Add(startInfo, "--project-root", paths.ProjectRoot);
        Add(startInfo, "--requested-mode", LauncherPolicy.RequestedMode(request));
        Add(startInfo, "--launch-token", token);
        Add(startInfo, "--ready-file", readyFile);
        if (request.Tool == LauncherTool.Player)
        {
            nint launchForeground = GetForegroundWindow();
            if (launchForeground != nint.Zero)
            {
                Add(startInfo, "--launch-foreground-hwnd", launchForeground.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        if (!string.IsNullOrWhiteSpace(request.PreselectPath))
        {
            Add(startInfo, "--preselect", Path.GetFullPath(request.PreselectPath));
        }

        using Process? child = Process.Start(startInfo);
        if (child is null)
        {
            return Fail(log, ChildFailed, "Child process did not start.");
        }

        var readyTimer = Stopwatch.StartNew();
        log.Write($"child started pid={child.Id} role={request.Tool} mode={request.Mode} exe={childPath} token={token}");
        var deadline = DateTime.UtcNow.AddSeconds(LauncherPolicy.ReadyTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (child.HasExited)
            {
                return Fail(log, ChildFailed, $"Child exited before READY. pid={child.Id} elapsedMs={readyTimer.ElapsedMilliseconds} exit={child.ExitCode}");
            }

            if (File.Exists(readyFile))
            {
                string json = File.ReadAllText(readyFile);
                if (LauncherPolicy.IsReadyRecordValid(json, token, child.Id, out string readyError))
                {
                    TryDelete(readyFile);
                    log.Write($"READY pid={child.Id} elapsedMs={readyTimer.ElapsedMilliseconds} exit=running");
                    return Success;
                }

                TerminateFailedLaunch(child, paths.CurrentSessionPath, log);
                return Fail(log, ChildFailed, readyError);
            }

            Thread.Sleep(100);
        }

        TerminateFailedLaunch(child, paths.CurrentSessionPath, log);
        return Fail(log, ReadyTimeout, $"Child READY timeout after {LauncherPolicy.ReadyTimeoutSeconds} seconds. pid={child.Id} elapsedMs={readyTimer.ElapsedMilliseconds}");
    }

    private static int RunEmergency(LauncherRequest request, LauncherPaths paths, RotatingTextLog log)
    {
        if (!File.Exists(paths.CurrentSessionPath))
        {
            Console.WriteLine("No active macro session was found.");
            log.Write("emergency: no current session");
            return Success;
        }

        CurrentSessionDocument document = CurrentSessionStore.Read(paths.CurrentSessionPath);
        WatchdogSessionRecord[] sessions = document.Sessions.ToArray();
        if (sessions.Length == 0)
        {
            TryDelete(paths.CurrentSessionPath);
            Console.WriteLine("No active macro session was found.");
            return Success;
        }

        if (EmergencyElevationDecision.RequiresSingleElevation(sessions, ElevationProbe.IsElevated()))
        {
            if (request.ValidateOnly)
            {
                log.Write("emergency validation: High session would request UAC");
                return Success;
            }

            var elevated = request with { Mode = LauncherMode.Elevated };
            return RelaunchElevated(elevated, paths, log);
        }

        if (request.ValidateOnly)
        {
            log.Write($"emergency validation passed sessions={sessions.Length}");
            return Success;
        }

        EmergencyStopSummary summary = new EmergencyStopCoordinator()
            .StopAllAsync(sessions, new WindowsEmergencyStopRuntime()).GetAwaiter().GetResult();
        foreach (EmergencySessionResult result in summary.Results)
        {
            Console.WriteLine($"{result.Session.Role} PID {result.Session.Pid}: {result.Outcome} - {result.Detail}");
            log.Write($"emergency role={result.Session.Role} pid={result.Session.Pid} outcome={result.Outcome} detail={result.Detail}");
            if (result.Outcome != EmergencySessionOutcome.Failed)
            {
                CurrentSessionStore.RemoveExact(result.Session, paths.CurrentSessionPath);
            }
        }
        Console.WriteLine($"found={summary.Found} cooperatively_stopped={summary.CooperativelyStopped} forced_stopped={summary.ForceStopped} stale_removed={summary.StaleRemoved} failed={summary.Failed}");
        Thread.Sleep(2000);
        return summary.Failed == 0 ? Success : EmergencyValidationFailed;
    }

    private static int Fail(RotatingTextLog log, int code, string message)
    {
        Console.Error.WriteLine(message);
        log.Write($"failure code={code} {message.Replace(Environment.NewLine, " ")}");
        return code;
    }

    private static void TerminateFailedLaunch(Process child, string sessionPath, RotatingTextLog log)
    {
        int pid = child.Id;
        DateTime startUtc;
        try
        {
            startUtc = child.StartTime.ToUniversalTime();
        }
        catch
        {
            startUtc = default;
        }

        try
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                child.WaitForExit(5000);
            }
            log.Write($"failed launch terminated exact pid={pid} startUtc={startUtc:O}");
        }
        catch (Exception ex)
        {
            log.Write($"failed launch termination error pid={pid} {ex.GetType().Name}: {ex.Message}");
        }

        RemoveSessionByPidAndStart(sessionPath, pid, startUtc, log);
    }

    private static void RemoveSessionByPidAndStart(string path, int pid, DateTime startUtc, RotatingTextLog log)
    {
        try
        {
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject document || document["sessions"] is not JsonArray sessions)
            {
                return;
            }

            for (int index = sessions.Count - 1; index >= 0; index--)
            {
                if (sessions[index] is not JsonObject session || session["pid"]?.GetValue<int>() != pid)
                {
                    continue;
                }

                string? sessionStartText = session["startTimeUtc"]?.GetValue<string>();
                if (startUtc != default && DateTime.TryParse(sessionStartText, out DateTime sessionStart) &&
                    Math.Abs((sessionStart.ToUniversalTime() - startUtc).TotalSeconds) > 2)
                {
                    continue;
                }

                sessions.RemoveAt(index);
            }

            if (sessions.Count == 0)
            {
                File.Delete(path);
                return;
            }

            string temporary = path + ".launcher-cleanup.tmp";
            File.WriteAllText(temporary, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex)
        {
            log.Write($"session cleanup error pid={pid} {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Add(ProcessStartInfo info, string name, string value)
    {
        info.ArgumentList.Add(name);
        info.ArgumentList.Add(value);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // A stale READY/session file is safer than masking the primary result.
        }
    }

    private static ProcessStartInfo WithArguments(this ProcessStartInfo info, IEnumerable<string> arguments)
    {
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return info;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
}

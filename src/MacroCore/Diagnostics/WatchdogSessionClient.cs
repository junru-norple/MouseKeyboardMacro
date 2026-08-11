using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using MacroCore.Security;
using MacroCore.Runtime;

namespace MacroCore.Diagnostics;

public sealed class WatchdogSessionRecord
{
    public string Role { get; set; } = string.Empty;
    public int Pid { get; set; }
    public DateTime StartTimeUtc { get; set; }
    public string SessionToken { get; set; } = string.Empty;
    public string IntegrityLevel { get; set; } = "Unknown";
    public string Mode { get; set; } = string.Empty;
    public string ProcessName { get; set; } = string.Empty;
    public string EmergencyEndpoint { get; set; } = string.Empty;
    public string ActivityState { get; set; } = "Idle";
    public int WatchdogPid { get; set; }
    public DateTime WatchdogStartTimeUtc { get; set; }
    public string WatchdogProcessName { get; set; } = "MacroSafetyWatchdog";
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class CurrentSessionDocument
{
    public string SchemaVersion { get; set; } = "2.0";
    public List<WatchdogSessionRecord> Sessions { get; set; } = [];
}

public static class CurrentSessionStore
{
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string ResolvePath() => RuntimeFolders.CurrentSession;

    public static CurrentSessionDocument Read(string? path = null)
    {
        lock (Sync)
        {
            var resolved = path ?? ResolvePath();
            try
            {
                if (!File.Exists(resolved))
                {
                    return new CurrentSessionDocument();
                }
                return JsonSerializer.Deserialize<CurrentSessionDocument>(File.ReadAllText(resolved), Options) ?? new CurrentSessionDocument();
            }
            catch
            {
                return new CurrentSessionDocument();
            }
        }
    }

    public static void Upsert(WatchdogSessionRecord record, string? path = null)
    {
        lock (Sync)
        {
            var resolved = path ?? ResolvePath();
            Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);
            var document = Read(resolved);
            document.Sessions.RemoveAll(item =>
                (string.Equals(item.Role, record.Role, StringComparison.OrdinalIgnoreCase) && item.Pid == record.Pid) ||
                !IsAlive(item));
            record.UpdatedAtUtc = DateTime.UtcNow;
            document.Sessions.Add(record);
            WriteAtomic(resolved, document);
        }
    }

    public static void Remove(string role, string token, string? path = null)
    {
        lock (Sync)
        {
            var resolved = path ?? ResolvePath();
            var document = Read(resolved);
            document.Sessions.RemoveAll(item =>
                string.Equals(item.Role, role, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.SessionToken, token, StringComparison.Ordinal));
            if (document.Sessions.Count == 0)
            {
                try { File.Delete(resolved); } catch { }
                return;
            }
            WriteAtomic(resolved, document);
        }
    }

    public static void RemoveExact(WatchdogSessionRecord record, string? path = null) =>
        Remove(record.Role, record.SessionToken, path);

    public static bool IsSessionIdentityWellFormed(WatchdogSessionRecord record)
    {
        if (record.Pid <= 0 || record.StartTimeUtc == default || string.IsNullOrWhiteSpace(record.ProcessName) ||
            string.IsNullOrWhiteSpace(record.SessionToken))
        {
            return false;
        }

        bool tokenValid = record.SessionToken.Length == 32 && record.SessionToken.All(Uri.IsHexDigit);
        bool endpointValid = string.IsNullOrWhiteSpace(record.EmergencyEndpoint) ||
            record.EmergencyEndpoint.Equals($"MacroEmergency_{record.Pid}_{record.SessionToken}", StringComparison.Ordinal);
        return tokenValid && endpointValid;
    }

    public static bool IsExactProcess(int pid, DateTime expectedStartUtc, string expectedProcessName)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return string.Equals(process.ProcessName, expectedProcessName, StringComparison.OrdinalIgnoreCase) &&
                   Math.Abs((process.StartTime.ToUniversalTime() - expectedStartUtc).TotalSeconds) < 1;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsAlive(WatchdogSessionRecord record)
    {
        try
        {
            using var process = Process.GetProcessById(record.Pid);
            return Math.Abs((process.StartTime.ToUniversalTime() - record.StartTimeUtc).TotalSeconds) < 1;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteAtomic(string path, CurrentSessionDocument document)
    {
        var temporary = path + ".tmp." + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, JsonSerializer.Serialize(document, Options));
        File.Move(temporary, path, true);
    }
}

public sealed class WatchdogSessionClient : IDisposable
{
    private readonly string _role;
    private readonly string _mode;
    private readonly string _token = Convert.ToHexString(Guid.NewGuid().ToByteArray());
    private readonly CancellationTokenSource _cancellation = new();
    private NamedPipeServerStream? _pipe;
    private Process? _watchdog;
    private Task? _heartbeat;
    private EmergencyStopEndpoint? _emergencyEndpoint;
    private WatchdogSessionRecord? _record;
    private readonly object _recordLock = new();
    private int _disposed;

    public WatchdogSessionClient(string role, string mode)
    {
        _role = role;
        _mode = mode;
    }

    public string Status { get; private set; } = "not-started";
    public bool IsHealthy { get; private set; }
    public event Action? EmergencyRequested;
    public event Action? ReplacementShutdownRequested;

    public void Start()
    {
        if (_pipe is not null)
        {
            return;
        }

        var target = Process.GetCurrentProcess();
        var watchdogPath = ResolveWatchdogPath();
        if (!File.Exists(watchdogPath))
        {
            throw new FileNotFoundException("找不到 MacroSafetyWatchdog.exe。", watchdogPath);
        }

        var pipeName = $"MacroSafety_{target.Id}_{Guid.NewGuid():N}";
        string emergencyPipeName = $"MacroEmergency_{target.Id}_{_token}";
        _emergencyEndpoint = new EmergencyStopEndpoint(emergencyPipeName, _token);
        _emergencyEndpoint.EmergencyRequested += RaiseEmergency;
        _emergencyEndpoint.ReplacementShutdownRequested += RaiseReplacementShutdown;
        _emergencyEndpoint.Start();
        _pipe = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var startUtc = target.StartTime.ToUniversalTime();
        var sessionPath = CurrentSessionStore.ResolvePath();
        var arguments = new[]
        {
            "--project-root", RuntimeFolders.ProjectRoot,
            "--process-pid", target.Id.ToString(),
            "--process-start-utc", startUtc.ToString("O"),
            "--process-name", target.ProcessName,
            "--session-token", _token,
            "--role", _role,
            "--pipe", pipeName,
            "--session-file", sessionPath
        };
        var connection = _pipe.WaitForConnectionAsync(_cancellation.Token);
        _watchdog = Process.Start(new ProcessStartInfo
        {
            FileName = watchdogPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(watchdogPath)!,
            Arguments = string.Join(" ", arguments.Select(Quote))
        }) ?? throw new InvalidOperationException("Watchdog 無法啟動。");

        try
        {
            if (!connection.Wait(TimeSpan.FromSeconds(8)))
            {
                var child = _watchdog.HasExited ? $"exit={_watchdog.ExitCode}" : "running";
                WriteDiagnostic($"connect=timeout role={_role} watchdog_pid={_watchdog.Id} child={child} path={watchdogPath}");
                throw new TimeoutException($"Watchdog heartbeat 連線逾時（{child}）。");
            }
        }
        catch (AggregateException exception)
        {
            var cause = exception.Flatten().InnerExceptions.FirstOrDefault() ?? exception;
            var child = _watchdog.HasExited ? $"exit={_watchdog.ExitCode}" : "running";
            WriteDiagnostic($"connect=fault role={_role} watchdog_pid={_watchdog.Id} child={child} error={cause.GetType().Name}:{cause.Message}");
            throw new InvalidOperationException($"Watchdog heartbeat 連線失敗（{child}）：{cause.Message}", cause);
        }

        var integrity = new WindowsPrivilegeService().GetCurrentIntegrity();
        _record = new WatchdogSessionRecord
        {
            Role = _role,
            Pid = target.Id,
            StartTimeUtc = startUtc,
            SessionToken = _token,
            IntegrityLevel = integrity >= WindowsIntegrityLevel.High ? "High" : "Medium",
            Mode = _mode,
            ProcessName = target.ProcessName,
            EmergencyEndpoint = emergencyPipeName,
            ActivityState = "Idle",
            WatchdogPid = _watchdog.Id,
            WatchdogStartTimeUtc = _watchdog.StartTime.ToUniversalTime(),
            WatchdogProcessName = _watchdog.ProcessName
        };
        CurrentSessionStore.Upsert(_record);

        IsHealthy = true;
        Status = $"connected:{_watchdog.Id}";
        _heartbeat = Task.Run(HeartbeatLoop);
        WriteDiagnostic($"connect=pass role={_role} watchdog_pid={_watchdog.Id} integrity={integrity}");
    }

    public void SetActivity(string activity)
    {
        if (string.IsNullOrWhiteSpace(activity))
        {
            return;
        }
        lock (_recordLock)
        {
            if (_record is null)
            {
                return;
            }
            _record.ActivityState = activity;
            CurrentSessionStore.Upsert(_record);
        }
    }

    private void RaiseEmergency() => EmergencyRequested?.Invoke();
    private void RaiseReplacementShutdown() => ReplacementShutdownRequested?.Invoke();

    private async Task HeartbeatLoop()
    {
        try
        {
            while (!_cancellation.IsCancellationRequested && _pipe is { IsConnected: true })
            {
                await _pipe.WriteAsync(new byte[] { 0x7E }, _cancellation.Token);
                await _pipe.FlushAsync(_cancellation.Token);
                await Task.Delay(500, _cancellation.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            IsHealthy = false;
            Status = "heartbeat-failed:" + exception.GetType().Name;
        }
    }

    private static string ResolveWatchdogPath()
    {
        return RuntimeFolders.Watchdog;
    }

    private static void WriteDiagnostic(string message)
    {
        try
        {
            Directory.CreateDirectory(RuntimeFolders.Logs);
            RotatingLog.Write(Path.Combine(RuntimeFolders.Logs, "watchdog_client.log"), $"{DateTimeOffset.Now:O} {message}");
        }
        catch
        {
        }
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        try
        {
            if (_pipe is { IsConnected: true })
            {
                _pipe.WriteByte(0xFF);
                _pipe.Flush();
            }
        }
        catch
        {
        }
        _cancellation.Cancel();
        try { _heartbeat?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        try { _watchdog?.WaitForExit(1500); } catch { }
        if (_emergencyEndpoint is not null)
        {
            _emergencyEndpoint.EmergencyRequested -= RaiseEmergency;
            _emergencyEndpoint.ReplacementShutdownRequested -= RaiseReplacementShutdown;
            _emergencyEndpoint.Dispose();
        }
        _pipe?.Dispose();
        _watchdog?.Dispose();
        _cancellation.Dispose();
        CurrentSessionStore.Remove(_role, _token);
        IsHealthy = false;
        Status = "stopped";
        GC.SuppressFinalize(this);
    }
}

using System.Diagnostics;
using System.Text.Json;
using MacroCore.Diagnostics;

namespace MacroCore.Runtime;

public sealed class ActiveToolLeaseRecord
{
    public string Role { get; set; } = string.Empty;
    public int Pid { get; set; }
    public DateTime StartTimeUtc { get; set; }
    public string Token { get; set; } = string.Empty;
    public string Integrity { get; set; } = "Unknown";
    public string ProjectRoot { get; set; } = string.Empty;
    public string ProcessName { get; set; } = string.Empty;
    public DateTime AcquiredAtUtc { get; set; }
}

public sealed class ActiveToolLeaseException : InvalidOperationException
{
    public ActiveToolLeaseException(string message, ActiveToolLeaseRecord? activeRecord = null, Exception? inner = null)
        : base(message, inner) => ActiveRecord = activeRecord;

    public ActiveToolLeaseRecord? ActiveRecord { get; }
}

public sealed class MacroToolExclusiveLease : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly FileStream _stream;
    private readonly string _metadataPath;
    private readonly string _logPath;
    private int _disposed;

    private MacroToolExclusiveLease(FileStream stream, string metadataPath, string logPath, ActiveToolLeaseRecord record)
    {
        _stream = stream;
        _metadataPath = metadataPath;
        _logPath = logPath;
        Record = record;
    }

    public ActiveToolLeaseRecord Record { get; }

    public static string LockPath(string stateDirectory) => Path.Combine(Path.GetFullPath(stateDirectory), "active_tool.lock");
    public static string MetadataPath(string stateDirectory) => Path.Combine(Path.GetFullPath(stateDirectory), "active_tool.json");

    public static MacroToolExclusiveLease Acquire(
        string stateDirectory,
        string role,
        string integrity,
        string projectRoot,
        string? processName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        string root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string state = Path.GetFullPath(stateDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string allowedPrefix = root + Path.DirectorySeparatorChar;
        if (!state.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ActiveToolLeaseException("Active tool lease 必須位於目前專案根目錄內。");
        }

        Directory.CreateDirectory(state);
        string lockPath = LockPath(state);
        string metadataPath = MetadataPath(state);
        FileStream stream;
        try
        {
            stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
        }
        catch (IOException exception)
        {
            throw new ActiveToolLeaseException(
                "已有另一個錄製器或播放器正在執行。請使用專案根目錄入口重新開啟。",
                ReadRecord(metadataPath),
                exception);
        }

        try
        {
            using Process process = Process.GetCurrentProcess();
            ActiveToolLeaseRecord record = new()
            {
                Role = role,
                Pid = process.Id,
                StartTimeUtc = process.StartTime.ToUniversalTime(),
                Token = Convert.ToHexString(Guid.NewGuid().ToByteArray()),
                Integrity = integrity,
                ProjectRoot = root,
                ProcessName = processName ?? process.ProcessName,
                AcquiredAtUtc = DateTime.UtcNow
            };
            WriteRecord(metadataPath, record);
            string logPath = Path.Combine(state, "Logs", "tool_lease.log");
            WriteLog(logPath, $"EXCLUSIVE_LEASE_ACQUIRED role={role} pid={record.Pid} integrity={integrity} token={record.Token}");
            return new MacroToolExclusiveLease(stream, metadataPath, logPath, record);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public static bool TryAcquire(
        string stateDirectory,
        string role,
        string integrity,
        string projectRoot,
        out MacroToolExclusiveLease? lease,
        out ActiveToolLeaseException? error)
    {
        try
        {
            lease = Acquire(stateDirectory, role, integrity, projectRoot);
            error = null;
            return true;
        }
        catch (ActiveToolLeaseException exception)
        {
            lease = null;
            error = exception;
            return false;
        }
    }

    public static bool IsAvailable(string stateDirectory)
    {
        try
        {
            Directory.CreateDirectory(stateDirectory);
            using FileStream stream = new(LockPath(stateDirectory), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static ActiveToolLeaseRecord? ReadRecord(string metadataPath)
    {
        try
        {
            return File.Exists(metadataPath)
                ? JsonSerializer.Deserialize<ActiveToolLeaseRecord>(File.ReadAllText(metadataPath), JsonOptions)
                : null;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            ActiveToolLeaseRecord? current = ReadRecord(_metadataPath);
            if (current is not null && string.Equals(current.Token, Record.Token, StringComparison.Ordinal))
            {
                File.Delete(_metadataPath);
            }
        }
        catch
        {
        }
        finally
        {
            _stream.Dispose();
            WriteLog(_logPath, $"EXCLUSIVE_LEASE_RELEASED role={Record.Role} pid={Record.Pid} token={Record.Token}");
        }
        GC.SuppressFinalize(this);
    }

    private static void WriteRecord(string path, ActiveToolLeaseRecord record)
    {
        string temporary = path + ".tmp." + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, JsonSerializer.Serialize(record, JsonOptions));
        File.Move(temporary, path, true);
    }

    private static void WriteLog(string path, string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            RotatingLog.Write(path, $"{DateTimeOffset.Now:O} {message}");
        }
        catch
        {
        }
    }
}

using System.Text.Json;

namespace MacroCore.Runtime;

public sealed class LaunchReadyRecord
{
    public string Token { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public int Pid { get; set; }
    public string Status { get; set; } = "READY";
    public string Detail { get; set; } = string.Empty;
    public DateTimeOffset ReadyAt { get; set; }
}

public sealed class LaunchReadyReporter
{
    private readonly string _token;
    private readonly string _readyFile;
    private int _reported;

    private LaunchReadyReporter(string token, string readyFile)
    {
        _token = token;
        _readyFile = readyFile;
    }

    public static LaunchReadyReporter? FromArguments(string[] args)
    {
        var token = RuntimePathResolver.GetOption(args, "--launch-token");
        var readyFile = RuntimePathResolver.GetOption(args, "--ready-file");
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(readyFile))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(readyFile);
        var allowed = AppPaths.Current.LaunchStateDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Ready file is outside the launcher state directory.");
        }
        return new LaunchReadyReporter(token, fullPath);
    }

    public void ReportReady(string role, string detail)
    {
        if (Interlocked.Exchange(ref _reported, 1) != 0)
        {
            return;
        }
        var record = new LaunchReadyRecord
        {
            Token = _token,
            Role = role,
            Pid = Environment.ProcessId,
            Status = "READY",
            Detail = detail,
            ReadyAt = DateTimeOffset.UtcNow
        };
        Directory.CreateDirectory(Path.GetDirectoryName(_readyFile)!);
        var temporary = _readyFile + ".tmp." + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, JsonSerializer.Serialize(record));
        File.Move(temporary, _readyFile, true);
    }
}

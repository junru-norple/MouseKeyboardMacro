using System.IO.Pipes;
using System.Text;

namespace MacroCore.Diagnostics;

public enum ToolControlCommand
{
    EmergencyStop,
    ReplacementShutdown
}

public sealed class EmergencyStopEndpoint : IDisposable
{
    private readonly string _pipeName;
    private readonly string _token;
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _listener;
    private int _disposed;

    public EmergencyStopEndpoint(string pipeName, string token)
    {
        _pipeName = pipeName;
        _token = token;
    }

    public string PipeName => _pipeName;
    public event Action? EmergencyRequested;
    public event Action? ReplacementShutdownRequested;

    public void Start() => _listener ??= Task.Run(ListenAsync);

    private async Task ListenAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            try
            {
                using NamedPipeServerStream server = new(
                    _pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(_cancellation.Token).ConfigureAwait(false);
                using StreamReader reader = new(server, Encoding.UTF8, false, 256, leaveOpen: true);
                using StreamWriter writer = new(server, new UTF8Encoding(false), 256, leaveOpen: true) { AutoFlush = true };
                string? request = await reader.ReadLineAsync(_cancellation.Token).ConfigureAwait(false);
                ToolControlCommand? command = request switch
                {
                    string value when string.Equals(value, "STOP " + _token, StringComparison.Ordinal) => ToolControlCommand.EmergencyStop,
                    string value when string.Equals(value, "REPLACE " + _token, StringComparison.Ordinal) => ToolControlCommand.ReplacementShutdown,
                    _ => null
                };
                if (command is null)
                {
                    await writer.WriteLineAsync("REJECT").ConfigureAwait(false);
                    continue;
                }

                string acknowledgement = command == ToolControlCommand.EmergencyStop
                    ? "ACK " + _token
                    : "ACK REPLACE " + _token;
                await writer.WriteLineAsync(acknowledgement).ConfigureAwait(false);
                if (command == ToolControlCommand.EmergencyStop)
                {
                    EmergencyRequested?.Invoke();
                }
                else
                {
                    ReplacementShutdownRequested?.Invoke();
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException) when (!_cancellation.IsCancellationRequested)
            {
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _cancellation.Cancel();
        try { _listener?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _cancellation.Dispose();
        GC.SuppressFinalize(this);
    }
}

public static class EmergencyStopClient
{
    public static async Task<bool> RequestAsync(
        string endpoint,
        string token,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        return await ToolControlClient.RequestAsync(
            endpoint,
            token,
            ToolControlCommand.EmergencyStop,
            timeout,
            cancellationToken).ConfigureAwait(false);
    }
}

public static class ReplacementShutdownClient
{
    public static Task<bool> RequestAsync(
        string endpoint,
        string token,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        ToolControlClient.RequestAsync(endpoint, token, ToolControlCommand.ReplacementShutdown, timeout, cancellationToken);
}

public static class ToolControlClient
{
    public static async Task<bool> RequestAsync(
        string endpoint,
        string token,
        ToolControlCommand command,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            using NamedPipeClientStream client = new(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync((int)Math.Max(1, timeout.TotalMilliseconds), deadline.Token).ConfigureAwait(false);
            using StreamReader reader = new(client, Encoding.UTF8, false, 256, leaveOpen: true);
            using StreamWriter writer = new(client, new UTF8Encoding(false), 256, leaveOpen: true) { AutoFlush = true };
            string verb = command == ToolControlCommand.EmergencyStop ? "STOP" : "REPLACE";
            await writer.WriteLineAsync(verb + " " + token).ConfigureAwait(false);
            string? response = await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false);
            string expected = command == ToolControlCommand.EmergencyStop ? "ACK " + token : "ACK REPLACE " + token;
            return string.Equals(response, expected, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException)
        {
            return false;
        }
    }
}

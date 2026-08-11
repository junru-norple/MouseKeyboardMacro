using System.Diagnostics;
using MacroCore.Diagnostics;
using MacroCore.Runtime;

namespace EmergencySessionTestHost;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (!AppPaths.TryInitialize(args, out string error))
        {
            Console.Error.WriteLine(error);
            return 2;
        }

        string role = Value(args, "--role") ?? "Player";
        string integrity = Value(args, "--integrity") ?? "Medium";
        string behavior = Value(args, "--behavior") ?? "responsive";
        using Process process = Process.GetCurrentProcess();
        string token = Convert.ToHexString(Guid.NewGuid().ToByteArray());
        string endpointName = $"MacroEmergency_{process.Id}_{token}";
        using CancellationTokenSource exit = new(TimeSpan.FromMinutes(5));
        EmergencyStopEndpoint? endpoint = null;
        if (!behavior.Equals("unresponsive", StringComparison.OrdinalIgnoreCase))
        {
            endpoint = new EmergencyStopEndpoint(endpointName, token);
            endpoint.EmergencyRequested += exit.Cancel;
            endpoint.Start();
        }

        WatchdogSessionRecord record = new()
        {
            Role = role,
            Pid = process.Id,
            StartTimeUtc = process.StartTime.ToUniversalTime(),
            ProcessName = process.ProcessName,
            SessionToken = token,
            IntegrityLevel = integrity,
            Mode = "DevelopmentTestHost",
            EmergencyEndpoint = endpointName,
            ActivityState = "Playing"
        };
        CurrentSessionStore.Upsert(record);
        Console.WriteLine($"READY pid={record.Pid} token={record.SessionToken} endpoint={endpointName}");
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, exit.Token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            endpoint?.Dispose();
            CurrentSessionStore.RemoveExact(record);
        }
        return 0;
    }

    private static string? Value(IReadOnlyList<string> args, string name)
    {
        for (int index = 0; index + 1 < args.Count; index++)
        {
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }
        return null;
    }
}

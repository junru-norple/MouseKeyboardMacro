using MacroCore.Diagnostics;
using MacroCore.Security;

namespace MacroRecorder.Services;

public sealed class SafetyWatchdogClient : IDisposable
{
    private readonly WatchdogSessionClient _client = new(
        "Recorder",
        new WindowsPrivilegeService().GetCurrentIntegrity() >= WindowsIntegrityLevel.High
            ? "ElevatedRecorder/Standard"
            : "NormalRecorder/Standard");
    public string Status => _client.Status;
    public bool IsHealthy => _client.IsHealthy;
    public event Action? EmergencyRequested
    {
        add => _client.EmergencyRequested += value;
        remove => _client.EmergencyRequested -= value;
    }
    public event Action? ReplacementShutdownRequested
    {
        add => _client.ReplacementShutdownRequested += value;
        remove => _client.ReplacementShutdownRequested -= value;
    }
    public void Start() => _client.Start();
    public void SetActivity(string activity) => _client.SetActivity(activity);
    public void Dispose() => _client.Dispose();
}

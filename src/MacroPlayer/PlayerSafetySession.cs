using MacroCore.Diagnostics;
using MacroCore.Security;

namespace MacroPlayer;

public sealed class PlayerSafetySession : IDisposable
{
    private readonly WatchdogSessionClient _client;

    private PlayerSafetySession(WatchdogSessionClient client)
    {
        _client = client;
        _client.EmergencyRequested += RaiseEmergency;
        _client.ReplacementShutdownRequested += RaiseReplacementShutdown;
    }

    public event Action? EmergencyRequested;
    public event Action? ReplacementShutdownRequested;

    public static PlayerSafetySession Register()
    {
        var integrity = new WindowsPrivilegeService().GetCurrentIntegrity();
        var client = new WatchdogSessionClient("Player", integrity >= WindowsIntegrityLevel.High ? "ElevatedPlayer" : "NormalPlayer");
        client.Start();
        return new PlayerSafetySession(client);
    }

    public void SetActivity(string activity) => _client.SetActivity(activity);
    private void RaiseEmergency() => EmergencyRequested?.Invoke();
    private void RaiseReplacementShutdown() => ReplacementShutdownRequested?.Invoke();

    public void Dispose()
    {
        _client.EmergencyRequested -= RaiseEmergency;
        _client.ReplacementShutdownRequested -= RaiseReplacementShutdown;
        _client.Dispose();
    }
}

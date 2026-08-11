using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;

namespace MacroPlayer;

public enum PlayerPrivilegeProbeResult
{
    Allowed,
    BlockedAdministratorRequired,
    Unknown
}

public static class PlayerPrivilegeGateProbe
{
    public const int AllowedExitCode = 0;
    public const int BlockedExitCode = 20;
    public const int UnknownExitCode = 21;

    public static int Run(PlayerLaunchOptions options)
    {
        PlayerPrivilegeProbeResult result = Evaluate(options.InitialMacroPath, WindowsPlayerPrivilege.IsElevated);
        string text = result switch
        {
            PlayerPrivilegeProbeResult.Allowed => "ALLOWED",
            PlayerPrivilegeProbeResult.BlockedAdministratorRequired => "BLOCKED_ADMIN_REQUIRED",
            _ => "UNKNOWN"
        };
        WriteStandardOutput(text);
        return result switch
        {
            PlayerPrivilegeProbeResult.Allowed => AllowedExitCode,
            PlayerPrivilegeProbeResult.BlockedAdministratorRequired => BlockedExitCode,
            _ => UnknownExitCode
        };
    }

    public static PlayerPrivilegeProbeResult Evaluate(string? macroPath, bool playerElevated)
    {
        if (string.IsNullOrWhiteSpace(macroPath) || !File.Exists(macroPath))
        {
            return PlayerPrivilegeProbeResult.Unknown;
        }

        try
        {
            PlaybackMacroDocument macro = PlaybackMacroDocument.Load(macroPath);
            EffectivePlaybackPrivilegeResolution privilege = EffectivePlaybackPrivilegeResolver.Resolve(macro);
            return privilege.Requirement switch
            {
                EffectivePlaybackPrivilegeRequirement.Administrator when !playerElevated => PlayerPrivilegeProbeResult.BlockedAdministratorRequired,
                EffectivePlaybackPrivilegeRequirement.Unknown => PlayerPrivilegeProbeResult.Unknown,
                _ => PlayerPrivilegeProbeResult.Allowed
            };
        }
        catch
        {
            return PlayerPrivilegeProbeResult.Unknown;
        }
    }

    private static void WriteStandardOutput(string value)
    {
        nint handle = GetStdHandle(-11);
        if (handle == nint.Zero || handle == new nint(-1))
        {
            return;
        }

        using SafeFileHandle safeHandle = new(handle, ownsHandle: false);
        using FileStream stream = new(safeHandle, FileAccess.Write);
        using StreamWriter writer = new(stream, new UTF8Encoding(false)) { AutoFlush = true };
        writer.WriteLine(value);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int standardHandle);
}

public static class PlayerMigrationDiagnostics
{
    public static void LegacyTargetModeIgnored()
    {
        try
        {
            Directory.CreateDirectory(PlayerRuntimePaths.Logs);
            File.AppendAllText(
                Path.Combine(PlayerRuntimePaths.Logs, "player_migration.log"),
                $"{DateTimeOffset.Now:O}\tLEGACY_TARGET_MODE_IGNORED; playback is desktop-only.{Environment.NewLine}",
                new UTF8Encoding(false));
        }
        catch
        {
        }
    }
}

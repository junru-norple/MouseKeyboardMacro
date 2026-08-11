using MacroCore.Runtime;
using MacroCore.Security;

namespace MacroPlayer;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            PlayerLaunchOptions options = PlayerLaunchOptions.Parse(args);
            if (options.PrivilegeGateProbe)
            {
                return PlayerPrivilegeGateProbe.Run(options);
            }

            AppPaths.Initialize(options.ProjectRoot);
            RuntimeFolders.EnsureRuntimeDirectories();
            PlayerRuntimePaths.Initialize(options.ProjectRoot);
            if (options.LegacyTargetModeIgnored)
            {
                PlayerMigrationDiagnostics.LegacyTargetModeIgnored();
            }
            bool safeSmokeOnly = args.Any(value => value.Equals("--safe-smoke", StringComparison.OrdinalIgnoreCase)) ||
                string.Equals(Environment.GetEnvironmentVariable("MKM_SAFE_VALIDATION_MODE"), "1", StringComparison.Ordinal);
            if (safeSmokeOnly)
            {
                return Directory.Exists(PlayerRuntimePaths.State) && Directory.Exists(PlayerRuntimePaths.Recordings) ? 0 : 65;
            }
            ApplicationConfiguration.Initialize();

            if (options.UiLayoutProbe)
            {
                return PlayerLayoutProbe.Run(options);
            }

            string integrity = new WindowsPrivilegeService().GetCurrentIntegrity() >= WindowsIntegrityLevel.High ? "High" : "Medium";
            using MacroToolExclusiveLease exclusiveLease = MacroToolExclusiveLease.Acquire(
                RuntimeFolders.StateRoot, "Player", integrity, RuntimeFolders.ProjectRoot);
            using PlayerSafetySession safetySession = PlayerSafetySession.Register();
            using PlaybackLibraryForm form = new(options, safetySession: safetySession);
            form.Shown += (_, _) => LaunchReadiness.SignalApplicationReady();
            Application.Run(form);
            return 0;
        }
        catch (ActiveToolLeaseException ex)
        {
            MessageBox.Show(ex.Message, "巨集重播", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 66;
        }
        catch (Exception ex)
        {
            try
            {
                Directory.CreateDirectory(PlayerRuntimePaths.Logs);
                File.AppendAllText(Path.Combine(PlayerRuntimePaths.Logs, "player_startup_error.log"),
                    $"{DateTimeOffset.Now:O} {ex}{Environment.NewLine}");
            }
            catch
            {
            }

            MessageBox.Show("播放器啟動失敗：" + ex.Message, "巨集重播", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}

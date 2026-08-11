using MacroCore.Runtime;
using MacroCore.Security;

namespace MacroRecorder;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var desktopProbeOnly = DesktopProbeMode.IsRequested(args);
        var layoutProbeOnly = RecorderLayoutProbe.IsRequested(args);
        var safeSmokeOnly = args.Any(value => value.Equals("--safe-smoke", StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(Environment.GetEnvironmentVariable("MKM_SAFE_VALIDATION_MODE"), "1", StringComparison.Ordinal);
        if (desktopProbeOnly || layoutProbeOnly)
        {
            ProbeConsole.TryAttachParent();
        }

        if (!AppPaths.TryInitialize(args, out var pathError))
        {
            if (desktopProbeOnly || layoutProbeOnly || safeSmokeOnly)
            {
                Console.Error.WriteLine(pathError);
            }
            else
            {
                MessageBox.Show(pathError, "MacroRecorder 啟動失敗", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return 64;
        }

        RuntimeFolders.EnsureRuntimeDirectories();
        if (safeSmokeOnly)
        {
            return Directory.Exists(RuntimeFolders.StateRoot) && Directory.Exists(RuntimeFolders.Recordings) ? 0 : 65;
        }
        if (desktopProbeOnly)
        {
            return DesktopProbeMode.Run();
        }

        ApplicationConfiguration.Initialize();
        if (layoutProbeOnly)
        {
            return RecorderLayoutProbe.Run(args);
        }
        var options = MacroToolLaunchOptions.Parse(args, MacroToolRole.Recorder);
        var privilegeService = new WindowsPrivilegeService();
        string integrity = privilegeService.GetCurrentIntegrity() >= WindowsIntegrityLevel.High ? "High" : "Medium";
        MacroToolExclusiveLease exclusiveLease;
        try
        {
            exclusiveLease = MacroToolExclusiveLease.Acquire(
                RuntimeFolders.StateRoot, "Recorder", integrity, RuntimeFolders.ProjectRoot);
        }
        catch (ActiveToolLeaseException exception)
        {
            MessageBox.Show(exception.Message, "MacroRecorder 啟動失敗", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 66;
        }
        using (exclusiveLease)
        {
        var displayModel = RecorderPrivilegeUi.Create(options, privilegeService);
        Application.ThreadException += (_, eventArgs) => WriteFatal("UI", eventArgs.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            WriteFatal("APPDOMAIN", eventArgs.ExceptionObject as Exception ?? new Exception("Unknown unhandled exception"));

        Application.Run(new MainForm(displayModel, privilegeService: privilegeService));
        }
        return 0;
    }

    private static void WriteFatal(string source, Exception exception)
    {
        try
        {
            var log = Path.Combine(RuntimeFolders.Logs, "recorder_errors.log");
            MacroCore.Diagnostics.RotatingLog.Write(log, $"{DateTimeOffset.Now:O}\t{source}\t{exception.GetType().Name}\t{exception.Message}");
        }
        catch
        {
        }
    }
}

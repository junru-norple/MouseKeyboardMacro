using System.Text;
using MacroCore.Runtime;

namespace MacroRecorder.Services;

internal static class RecorderDiagnosticsLog
{
    private static readonly object Sync = new();
    private static readonly string LogRoot = ResolveLogRoot();

    public static void HookHealth(string message) => Append("MacroRecorder_hook_health.log", message);
    public static void RawInput(string message) => Append("MacroRecorder_raw_input.log", message);
    public static void GameCompatibility(string message) => Append("game_input_compatibility.log", message);

    private static void Append(string fileName, string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(LogRoot);
                MacroCore.Diagnostics.RotatingLog.Write(
                    Path.Combine(LogRoot, fileName),
                    $"{DateTimeOffset.Now:O} {message}");
            }
        }
        catch
        {
            // Diagnostics must never terminate capture.
        }
    }

    private static string ResolveLogRoot()
    {
        return RuntimeFolders.Logs;
    }
}

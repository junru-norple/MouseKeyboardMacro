using System.Text;
using MacroCore.Runtime;

namespace MacroCore.Diagnostics;

public static class RotatingLog
{
    private static readonly object Sync = new();
    public const long DefaultMaximumBytes = 1024 * 1024;
    public const int DefaultMaximumFiles = 5;

    public static void WriteRuntime(string fileName, string message) =>
        Write(Path.Combine(AppPaths.Current.LogsDirectory, fileName), message);

    public static void Write(string path, string message, long maximumBytes = DefaultMaximumBytes, int maximumFiles = DefaultMaximumFiles)
    {
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                RotateIfNeeded(path, Encoding.UTF8.GetByteCount(message) + 2, maximumBytes, maximumFiles);
                File.AppendAllText(path, message + Environment.NewLine, new UTF8Encoding(false));
            }
            catch
            {
            }
        }
    }

    public static void RotateIfNeeded(string path, long incomingBytes, long maximumBytes, int maximumFiles)
    {
        if (!File.Exists(path) || new FileInfo(path).Length + incomingBytes <= maximumBytes)
        {
            return;
        }
        for (var index = Math.Max(1, maximumFiles - 1); index >= 1; index--)
        {
            var source = path + "." + index;
            var destination = path + "." + (index + 1);
            if (index + 1 >= maximumFiles && File.Exists(destination))
            {
                File.Delete(destination);
            }
            if (File.Exists(source))
            {
                File.Move(source, destination, true);
            }
        }
        File.Move(path, path + ".1", true);
    }
}

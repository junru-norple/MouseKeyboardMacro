using System.Text;

namespace MacroLauncher;

public sealed class RotatingTextLog
{
    private const long Limit = 1024 * 1024;
    private const int Retained = 5;
    private readonly string _path;
    private readonly object _sync = new();

    public RotatingTextLog(string path)
    {
        _path = path;
    }

    public void Write(string message)
    {
        lock (_sync)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            RotateIfNeeded();
            File.AppendAllText(
                _path,
                $"{DateTimeOffset.Now:O} pid={Environment.ProcessId} {message}{Environment.NewLine}",
                new UTF8Encoding(false));
        }
    }

    private void RotateIfNeeded()
    {
        if (!File.Exists(_path) || new FileInfo(_path).Length < Limit)
        {
            return;
        }

        string oldest = _path + "." + Retained;
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (int index = Retained - 1; index >= 1; index--)
        {
            string source = _path + "." + index;
            if (File.Exists(source))
            {
                File.Move(source, _path + "." + (index + 1));
            }
        }

        File.Move(_path, _path + ".1");
    }
}

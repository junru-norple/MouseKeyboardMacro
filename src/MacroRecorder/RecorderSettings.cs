using System.Text.Json;
using System.Text.Json.Serialization;
using MacroCore.Runtime;

namespace MacroRecorder;

public enum RecorderInputModeSetting
{
    Standard,
    RawEnhanced
}

public enum RecordingWindowBehavior
{
    KeepWindow,
    MinimizeToTaskbar
}

public sealed class RecorderSettings
{
    public RecorderInputModeSetting InputMode { get; set; } = RecorderInputModeSetting.Standard;
    public RecordingWindowBehavior WindowBehavior { get; set; } = RecordingWindowBehavior.KeepWindow;
    public bool ShowLiveMonitor { get; set; } = true;
}

public sealed class RecorderSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;

    public RecorderSettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(RuntimeFolders.Settings, "recorder-settings.json");
    }

    public string PathOnDisk => _path;

    public RecorderSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new RecorderSettings();
            }

            return JsonSerializer.Deserialize<RecorderSettings>(File.ReadAllText(_path), JsonOptions)
                   ?? new RecorderSettings();
        }
        catch
        {
            return new RecorderSettings();
        }
    }

    public void Save(RecorderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var directory = Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("Settings path has no directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = _path + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

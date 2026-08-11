using System.Globalization;
using MacroCore.Timing;

namespace MacroPlayer;

public sealed partial class PlaybackLibraryForm
{
    private readonly Dictionary<string, PlaybackTimingMetrics> _lastTimingByFile =
        new(StringComparer.OrdinalIgnoreCase);

    private static long GetEventTimelineMilliseconds(PlaybackMacroDocument macro) =>
        macro.Events.Count == 0 ? 0 : macro.Events[^1].OffsetMilliseconds;

    private string GetLastTimingSummary(string filePath)
    {
        if (!_lastTimingByFile.TryGetValue(filePath, out PlaybackTimingMetrics? timing))
        {
            return "尚無本次啟動後的完成紀錄";
        }

        return FormatTiming(timing);
    }

    private void UpdateLastTimingDetails(PlaybackRunResult result)
    {
        if (_selectedMacro is null || result.TimingMetrics is null)
        {
            return;
        }

        _lastTimingByFile[_selectedMacro.FilePath] = result.TimingMetrics;
        _details.AppendText(Environment.NewLine + "上次實際播放：" + FormatTiming(result.TimingMetrics));
        if (IsTimingWarning(result))
        {
            _details.AppendText(Environment.NewLine + "時鐘警告：速度比超過 1.10，或漂移超過 1 秒/事件時間軸 3%。");
        }
    }

    private static bool IsTimingWarning(PlaybackRunResult result)
    {
        if (result.TimingMetrics is not { } timing)
        {
            return false;
        }

        double threshold = Math.Max(1000, timing.EventTimelineDurationMilliseconds * 0.03);
        return timing.SpeedRatio > 1.10 || Math.Abs(timing.FinalDriftMilliseconds) > threshold;
    }

    private static string FormatCompletionTiming(PlaybackRunResult result) =>
        result.TimingMetrics is { } timing
            ? FormatTiming(timing)
            : "本次沒有可用的 timing metrics。";

    private static string FormatTiming(PlaybackTimingMetrics timing)
    {
        string ratio = timing.SpeedRatio.ToString("0.000", CultureInfo.InvariantCulture);
        return
            $"事件時間軸 {TimeSpan.FromMilliseconds(timing.EventTimelineDurationMilliseconds):hh\\:mm\\:ss\\.fff}，" +
            $"實際播放 {TimeSpan.FromMilliseconds(timing.WallPlaybackDurationMilliseconds):hh\\:mm\\:ss\\.fff}，" +
            $"速度比 {ratio}，漂移 {timing.FinalDriftMilliseconds:0} ms";
    }
}

using MacroCore.Input;
using MacroCore.Models;

namespace MacroCore.Diagnostics;

public sealed record SparseRecordingResult(bool IsSparseCapture, string Code, string FormatStatus, string Message);

public static class SparseRecordingClassifier
{
    public const long MinimumMeaningfulDurationMs = 10_000;
    public const int MinimumMeaningfulEventCount = 4;

    public static SparseRecordingResult Classify(MacroFile macro)
    {
        var sparse = macro.DurationMs >= MinimumMeaningfulDurationMs && macro.Events.Count < MinimumMeaningfulEventCount;
        return sparse
            ? new SparseRecordingResult(true, "SPARSE_CAPTURE", "NOT_PLAYER_FORMAT_ERROR", "檔案格式有效，但錄製內容不足，無法重現原操作。")
            : new SparseRecordingResult(false, "NORMAL_CAPTURE", "NOT_PLAYER_FORMAT_ERROR", string.Empty);
    }
}

public enum PlaybackCompatibilityStatus
{
    Unknown,
    Compatible,
    UnsupportedSendInput,
    Error
}

public static class PlaybackCompatibilityClassifier
{
    public static PlaybackCompatibilityStatus Classify(Exception exception) =>
        exception is StandardInputRejectedException
            ? PlaybackCompatibilityStatus.UnsupportedSendInput
            : PlaybackCompatibilityStatus.Error;
}

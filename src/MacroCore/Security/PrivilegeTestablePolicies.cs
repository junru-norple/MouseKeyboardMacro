namespace MacroCore.Security;

public static class PlayerLibraryPolicy
{
    public static IReadOnlyList<string> SortNewestFirst(IEnumerable<(string Path, DateTime CreatedUtc)> files) =>
        files.OrderByDescending(item => item.CreatedUtc).ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase).Select(item => item.Path).ToArray();

    public static bool CanStart(bool hasSelection, bool isValid, bool busy) => hasSelection && isValid && !busy;

    public static IReadOnlyList<int> CountdownSeconds { get; } = new[] { 5, 4, 3, 2, 1 };
}

public sealed class PlaybackSelectionState
{
    public string? SelectedPath { get; private set; }
    public bool IsBusy { get; private set; }
    public int CompletedPlaybackCount { get; private set; }

    public void Select(string path)
    {
        if (IsBusy) throw new InvalidOperationException("Cannot change selection during playback.");
        SelectedPath = path;
    }

    public void Begin()
    {
        if (SelectedPath is null || IsBusy) throw new InvalidOperationException("Playback cannot begin.");
        IsBusy = true;
    }

    public void Complete()
    {
        if (!IsBusy) throw new InvalidOperationException("Playback is not active.");
        CompletedPlaybackCount++;
        IsBusy = false;
    }

    public void Cancel() => IsBusy = false;
}

public static class EmergencyElevationPolicy
{
    public static bool ShouldElevate(bool containsHighSession, bool currentProcessIsHigh) =>
        containsHighSession && !currentProcessIsHigh;
}

public static class WatchdogIntegrityPolicy
{
    public static WindowsIntegrityLevel ExpectedChildIntegrity(WindowsIntegrityLevel parentIntegrity) => parentIntegrity;
}

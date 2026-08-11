namespace MacroPlayer;

public sealed class FreeDesktopFocusPolicy : IPlaybackFocusPolicy
{
    private readonly IForegroundWindowService _foreground;
    private nint _lastObserved;
    private bool _hasObservation;

    public FreeDesktopFocusPolicy(IForegroundWindowService foreground) => _foreground = foreground;

    public int FocusChangeCount { get; private set; }
    public int FullResolutionCount { get; private set; }
    public int FastProbeCount { get; private set; }

    public PlaybackSafetyCheck CheckPeriodicSafety()
    {
        FullResolutionCount++;
        if (_foreground.IsSecureDesktop(out string reason))
        {
            return PlaybackSafetyCheck.Failed(PlaybackSafetyFailureKind.SecureDesktop, reason);
        }

        nint current = _foreground.GetForegroundWindowHandleFast();
        FastProbeCount++;
        if (_hasObservation && current != _lastObserved)
        {
            FocusChangeCount++;
        }
        _lastObserved = current;
        _hasObservation = true;
        return PlaybackSafetyCheck.Passed;
    }
}

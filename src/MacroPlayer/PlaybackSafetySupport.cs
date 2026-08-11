namespace MacroPlayer;

public readonly record struct PlaybackKeyIdentity(int VirtualKey, int ScanCode, bool Extended);

public sealed class HeldPlaybackInputs
{
    private readonly object _gate = new();
    private readonly HashSet<PlaybackKeyIdentity> _keys = [];
    private readonly HashSet<string> _buttons = new(StringComparer.OrdinalIgnoreCase);

    public int KeyCount { get { lock (_gate) return _keys.Count; } }
    public int ButtonCount { get { lock (_gate) return _buttons.Count; } }

    public void Track(PlaybackMacroEvent item)
    {
        lock (_gate)
        {
            PlaybackKeyIdentity key = new(item.VirtualKey, item.ScanCode, item.Extended);
            if (item.Kind == PlaybackEventKind.KeyDown)
            {
                _keys.Add(key);
            }
            else if (item.Kind == PlaybackEventKind.KeyUp)
            {
                _keys.Remove(key);
            }
            else if (item.Kind == PlaybackEventKind.MouseDown)
            {
                _buttons.Add(item.MouseButton);
            }
            else if (item.Kind == PlaybackEventKind.MouseUp)
            {
                _buttons.Remove(item.MouseButton);
            }
        }
    }

    public (PlaybackKeyIdentity[] Keys, string[] Buttons) Drain()
    {
        lock (_gate)
        {
            PlaybackKeyIdentity[] keys = _keys.ToArray();
            string[] buttons = _buttons.ToArray();
            _keys.Clear();
            _buttons.Clear();
            return (keys, buttons);
        }
    }
}

public sealed record PlaybackRuntimeCounters(
    int FocusFullResolutionCount,
    int FocusFastProbeCount,
    int ProgressUpdateCount,
    int SendInputCallCount,
    int NativeInputCount,
    int SafetyStopCount)
{
    public static PlaybackRuntimeCounters Empty { get; } = new(0, 0, 0, 0, 0, 0);
}

public enum PlaybackSafetyFailureKind
{
    None,
    SecureDesktop
}

public sealed record PlaybackSafetyCheck(bool Safe, PlaybackSafetyFailureKind Kind, string Reason)
{
    public static PlaybackSafetyCheck Passed { get; } = new(true, PlaybackSafetyFailureKind.None, string.Empty);
    public static PlaybackSafetyCheck Failed(PlaybackSafetyFailureKind kind, string reason) => new(false, kind, reason);
}

public sealed class PlaybackSafetyMonitor
{
    private readonly IPlaybackFocusPolicy _policy;
    private readonly TimeSpan _interval;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private PlaybackSafetyCheck? _failure;

    public PlaybackSafetyMonitor(
        IPlaybackFocusPolicy policy,
        TimeSpan? interval = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _policy = policy;
        _interval = interval ?? TimeSpan.FromMilliseconds(150);
        _delay = delay ?? Task.Delay;
    }

    public PlaybackSafetyCheck? Failure => Volatile.Read(ref _failure);
    public int SafetyStopCount => Failure is null ? 0 : 1;

    public async Task RunAsync(CancellationTokenSource playbackCancellation, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _delay(_interval, cancellationToken).ConfigureAwait(false);
                PlaybackSafetyCheck check = _policy.CheckPeriodicSafety();
                if (check.Safe)
                {
                    continue;
                }

                if (Interlocked.CompareExchange(ref _failure, check, null) is null)
                {
                    playbackCancellation.Cancel();
                }
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}

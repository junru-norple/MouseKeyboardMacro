using MacroCore.Timing;

namespace MacroPlayer;

public enum PlaybackWorkflowState
{
    Selection,
    Validating,
    Countdown,
    Playing,
    Completed,
    Stopped,
    Failed
}

public enum PlaybackDisposition
{
    Completed,
    Cancelled,
    F11Stop,
    PrivilegeRejected,
    SecureDesktop,
    ValidationRejected,
    Failed
}

public sealed record ForegroundSnapshot(
    nint WindowHandle,
    int ProcessId,
    string ProcessName,
    int IntegrityRid)
{
    public bool IsHighIntegrity => IntegrityRid >= 0x3000;
}

public sealed record PlaybackExecutionContext(bool PlayerElevated = false)
{
    public static PlaybackExecutionContext Standard { get; } = new(false);
    public static PlaybackExecutionContext Elevated { get; } = new(true);
}

public sealed record PlaybackProgress(int EventsSent, int TotalEvents, TimeSpan Elapsed)
{
    public TimeSpan TimelinePosition { get; init; }
    public TimeSpan Drift { get; init; }
}

public sealed record PlaybackRunResult(
    bool Completed,
    bool Cancelled,
    int EventsSent,
    string Message,
    PlaybackDisposition Disposition,
    int FocusChangeCount = 0,
    PlaybackTimingMetrics? TimingMetrics = null,
    PlaybackRuntimeCounters? RuntimeCounters = null)
{
    public static PlaybackRunResult Success(int count, int focusChangeCount = 0) =>
        new(true, false, count, "播放完畢", PlaybackDisposition.Completed, focusChangeCount);

    public static PlaybackRunResult Stopped(
        int count,
        string message,
        PlaybackDisposition disposition = PlaybackDisposition.Cancelled,
        int focusChangeCount = 0) =>
        new(false, true, count, message, disposition, focusChangeCount);

    public static PlaybackRunResult Failure(
        int count,
        string message,
        PlaybackDisposition disposition = PlaybackDisposition.Failed,
        int focusChangeCount = 0) =>
        new(false, false, count, message, disposition, focusChangeCount);
}

public interface IForegroundWindowService
{
    ForegroundSnapshot? CaptureCurrent();
    bool TryActivate(ForegroundSnapshot snapshot);
    bool IsSecureDesktop(out string reason);
    nint GetForegroundWindowHandleFast();
}

public interface IPlaybackFocusPolicy
{
    int FocusChangeCount { get; }
    int FullResolutionCount => 0;
    int FastProbeCount => 0;
    PlaybackSafetyCheck CheckPeriodicSafety();
}

public interface IPlayerWindowModeService
{
    Task PrepareAsync(PlayerCountdownMode mode, PlaybackExecutionContext context, CancellationToken cancellationToken);
    Task RestoreAsync();
    void SetFailureHandler(Action<string>? handler) { }
}

public interface ICountdownService
{
    Task RunAsync(int seconds, Action<int> tick, CancellationToken cancellationToken);
}

public interface IPlaybackSession : IDisposable
{
    event EventHandler? FirstEventSent;
    event EventHandler<PlaybackProgress>? ProgressChanged;
    bool FirstEventWasSent { get; }
    int EventsSentCount { get; }
    int FocusChangeCount { get; }
    PlaybackTimingMetrics? TimingMetrics => null;
    PlaybackRuntimeCounters RuntimeCounters => PlaybackRuntimeCounters.Empty;
    Task<PlaybackRunResult> PlayAsync(CancellationToken cancellationToken);
    void Stop();
}

public interface IPlaybackServiceFactory
{
    IPlaybackSession Create(
        PlaybackMacroDocument macro,
        PlaybackExecutionContext context,
        IPlaybackFocusPolicy focusPolicy);
}

public interface IPlaybackSessionLog
{
    void SessionStarted(PlaybackMacroDocument macro, PlaybackExecutionContext context, PlayerCountdownMode mode);
    void Phase(string phase) { }
    void FirstEventSent();
    void Timing(PlaybackTimingMetrics metrics, PlaybackRuntimeCounters counters) { }
    void SessionEnded(string disposition, int sentCount, int focusChangeCount, string? detail = null);
}

public interface IOverlayService
{
    void ShowCountdown(string macroName, int seconds);
    void ShowPlaying(string macroName, PlaybackProgress progress);
    void Close();
}

public sealed class PlaybackStartController
{
    private readonly IForegroundWindowService _foreground;
    private readonly IPlayerWindowModeService _windowMode;
    private readonly ICountdownService _countdown;
    private readonly IPlaybackServiceFactory _factory;
    private readonly IPlaybackSessionLog _log;
    private readonly IOverlayService _overlay;
    private readonly Func<ForegroundSnapshot?> _preferredForegroundProvider;
    private int _running;
    private IPlaybackSession? _session;

    public PlaybackStartController(
        IForegroundWindowService foreground,
        IPlayerWindowModeService windowMode,
        ICountdownService countdown,
        IPlaybackServiceFactory factory,
        IPlaybackSessionLog log,
        IOverlayService overlay,
        Func<ForegroundSnapshot?>? preferredForegroundProvider = null)
    {
        _foreground = foreground;
        _windowMode = windowMode;
        _countdown = countdown;
        _factory = factory;
        _log = log;
        _overlay = overlay;
        _preferredForegroundProvider = preferredForegroundProvider ?? (() => null);
    }

    public bool IsRunning => Volatile.Read(ref _running) != 0;
    public event EventHandler<PlaybackWorkflowState>? StateChanged;
    public event EventHandler<int>? CountdownChanged;
    public event EventHandler<PlaybackProgress>? ProgressChanged;

    public async Task<PlaybackRunResult> StartAsync(
        PlaybackMacroDocument macro,
        PlayerCountdownMode mode,
        bool playerElevated,
        CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return PlaybackRunResult.Failure(0, "已有播放工作正在執行。", PlaybackDisposition.ValidationRejected);
        }

        int sent = 0;
        bool sessionLogged = false;
        try
        {
            ChangeState(PlaybackWorkflowState.Validating);
            if (macro.Events.Count == 0)
            {
                return PlaybackRunResult.Failure(0, "巨集沒有可播放的輸入事件。", PlaybackDisposition.ValidationRejected);
            }

            if (!AbsoluteOnlyPlaybackGate.TryValidate(macro, out string absoluteOnlyError))
            {
                return PlaybackRunResult.Failure(0, absoluteOnlyError, PlaybackDisposition.ValidationRejected);
            }

            EffectivePlaybackPrivilegeResolution privilege = EffectivePlaybackPrivilegeResolver.Resolve(macro);
            if (privilege.Requirement == EffectivePlaybackPrivilegeRequirement.Administrator && !playerElevated)
            {
                return PlaybackRunResult.Failure(0, privilege.Reason, PlaybackDisposition.PrivilegeRejected);
            }

            if (_foreground.IsSecureDesktop(out string desktopReason))
            {
                return PlaybackRunResult.Failure(0, desktopReason, PlaybackDisposition.SecureDesktop);
            }

            PlaybackExecutionContext context = new(playerElevated);
            IPlaybackFocusPolicy focusPolicy = new FreeDesktopFocusPolicy(_foreground);
            _log.SessionStarted(macro, context, mode);
            sessionLogged = true;
            _log.Phase("DESKTOP_ONLY_NO_TARGET");

            await _windowMode.PrepareAsync(mode, context, cancellationToken).ConfigureAwait(true);
            _log.Phase($"PLAYER_WINDOW_ACTION requested={mode} applied={mode}");
            TryRelinquishKeyboardFocus(macro, playerElevated);

            ChangeState(PlaybackWorkflowState.Countdown);
            _log.Phase("COUNTDOWN_STARTED");
            await _countdown.RunAsync(5, seconds =>
            {
                CountdownChanged?.Invoke(this, seconds);
                if (mode == PlayerCountdownMode.MinimizeBeforeCountdown)
                {
                    _overlay.ShowCountdown(macro.Name, seconds);
                }
            }, cancellationToken).ConfigureAwait(true);

            using IPlaybackSession session = _factory.Create(macro, context, focusPolicy);
            _session = session;
            session.FirstEventSent += (_, _) => _log.FirstEventSent();
            session.ProgressChanged += (_, progress) =>
            {
                sent = progress.EventsSent;
                ProgressChanged?.Invoke(this, progress);
                if (mode == PlayerCountdownMode.MinimizeBeforeCountdown)
                {
                    _overlay.ShowPlaying(macro.Name, progress);
                }
            };
            ChangeState(PlaybackWorkflowState.Playing);
            _log.Phase("PLAYBACK_STARTED");
            PlaybackRunResult result = await session.PlayAsync(cancellationToken).ConfigureAwait(true);
            sent = session.EventsSentCount;
            result = result with
            {
                FocusChangeCount = session.FocusChangeCount,
                TimingMetrics = session.TimingMetrics,
                RuntimeCounters = session.RuntimeCounters
            };
            if (result.Completed && !session.FirstEventWasSent)
            {
                result = PlaybackRunResult.Failure(sent, "播放工作未送出第一個事件，已視為失敗。", PlaybackDisposition.Failed, session.FocusChangeCount);
            }

            ChangeState(result.Completed ? PlaybackWorkflowState.Completed : result.Cancelled ? PlaybackWorkflowState.Stopped : PlaybackWorkflowState.Failed);
            LogEnd(result, session.FocusChangeCount);
            return result;
        }
        catch (OperationCanceledException)
        {
            ChangeState(PlaybackWorkflowState.Stopped);
            PlaybackRunResult stopped = PlaybackRunResult.Stopped(sent, "播放已停止");
            if (sessionLogged)
            {
                LogEnd(stopped, _session?.FocusChangeCount ?? 0);
            }
            return stopped;
        }
        catch (Exception ex)
        {
            ChangeState(PlaybackWorkflowState.Failed);
            PlaybackRunResult failed = PlaybackRunResult.Failure(sent, "播放失敗：" + ex.Message);
            if (sessionLogged)
            {
                LogEnd(failed, _session?.FocusChangeCount ?? 0, ex.GetType().Name + ": " + ex.Message);
            }
            return failed;
        }
        finally
        {
            _session = null;
            _overlay.Close();
            await _windowMode.RestoreAsync().ConfigureAwait(true);
            Interlocked.Exchange(ref _running, 0);
        }
    }

    public void Stop() => _session?.Stop();

    private void TryRelinquishKeyboardFocus(PlaybackMacroDocument macro, bool playerElevated)
    {
        if (macro.Events[0].Kind is not (PlaybackEventKind.KeyDown or PlaybackEventKind.KeyUp))
        {
            return;
        }

        try
        {
            ForegroundSnapshot? preferred = _preferredForegroundProvider();
            if (preferred is null)
            {
                _log.Phase("DESKTOP_FOCUS_HANDOFF_UNAVAILABLE_WARNING");
                return;
            }

            if (preferred.IsHighIntegrity && !playerElevated)
            {
                _log.Phase("DESKTOP_FOCUS_HANDOFF_INTEGRITY_WARNING");
                return;
            }

            _log.Phase(_foreground.TryActivate(preferred)
                ? "DESKTOP_FOCUS_HANDOFF_ATTEMPTED"
                : "DESKTOP_FOCUS_HANDOFF_FAILED_WARNING");
        }
        catch (Exception ex)
        {
            _log.Phase("DESKTOP_FOCUS_HANDOFF_EXCEPTION_WARNING " + ex.GetType().Name);
        }
    }

    private void LogEnd(PlaybackRunResult result, int focusChangeCount, string? detail = null)
    {
        if (result.TimingMetrics is not null)
        {
            _log.Timing(result.TimingMetrics, result.RuntimeCounters ?? PlaybackRuntimeCounters.Empty);
        }
        _log.SessionEnded(ToLogCode(result.Disposition), result.EventsSent, focusChangeCount, detail ?? result.Message);
    }

    private static string ToLogCode(PlaybackDisposition disposition) => disposition switch
    {
        PlaybackDisposition.Completed => "COMPLETED",
        PlaybackDisposition.Cancelled => "CANCELLED",
        PlaybackDisposition.F11Stop => "F11_STOP",
        PlaybackDisposition.PrivilegeRejected => "PRIVILEGE_REJECTED",
        PlaybackDisposition.SecureDesktop => "SECURE_DESKTOP_STOP",
        PlaybackDisposition.ValidationRejected => "VALIDATION_REJECTED",
        _ => "FAILED"
    };

    private void ChangeState(PlaybackWorkflowState state) => StateChanged?.Invoke(this, state);
}

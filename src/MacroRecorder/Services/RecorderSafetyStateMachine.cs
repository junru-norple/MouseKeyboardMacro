namespace MacroRecorder.Services;

public readonly record struct RecorderStateSnapshot(
    RecorderUiState State,
    long Version,
    long HoldElapsedMs,
    long HoldRemainingMs,
    string? ErrorCode);

public sealed class RecorderSafetyStateMachine : IDisposable
{
    private readonly int _holdThresholdMs;
    private readonly int _staleGraceMs;
    private readonly System.Threading.Timer _thresholdTimer;
    private readonly System.Threading.Timer _watchdogTimer;
    private int _state = (int)RecorderUiState.Armed;
    private int _f12Down;
    private int _stopHoldArmed = 1;
    private int _disposed;
    private long _holdStartedAt;
    private long _version;
    private string? _errorCode;

    public event Action<RecorderStateSnapshot>? SnapshotChanged;
    public event Action? RecordingStarted;
    public event Action? FinalizationRequested;

    public RecorderSafetyStateMachine(int holdThresholdMs = 5000, int staleGraceMs = 500)
    {
        _holdThresholdMs = Math.Max(1, holdThresholdMs);
        _staleGraceMs = Math.Max(1, staleGraceMs);
        _thresholdTimer = new System.Threading.Timer(_ => EvaluateHold(), null, Timeout.Infinite, Timeout.Infinite);
        _watchdogTimer = new System.Threading.Timer(_ => EvaluateHold(), null, 50, 50);
    }

    public RecorderUiState CurrentState => (RecorderUiState)Volatile.Read(ref _state);
    public bool IsF12Down => Volatile.Read(ref _f12Down) != 0;
    public long Version => Interlocked.Read(ref _version);

    public RecorderStateSnapshot CurrentSnapshot
    {
        get
        {
            var state = CurrentState;
            var elapsed = state is RecorderUiState.StartHolding or RecorderUiState.StopHolding
                ? Math.Max(0, Environment.TickCount64 - Interlocked.Read(ref _holdStartedAt))
                : 0;
            return new RecorderStateSnapshot(
                state,
                Version,
                elapsed,
                state is RecorderUiState.StartHolding or RecorderUiState.StopHolding
                    ? Math.Max(0, _holdThresholdMs - elapsed)
                    : 0,
                Volatile.Read(ref _errorCode));
        }
    }

    public void HandleF12(bool isDown)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (isDown)
        {
            if (Interlocked.Exchange(ref _f12Down, 1) != 0)
            {
                return;
            }

            if (CurrentState == RecorderUiState.Armed)
            {
                BeginHold(RecorderUiState.Armed, RecorderUiState.StartHolding);
            }
            else if (CurrentState == RecorderUiState.Recording && Volatile.Read(ref _stopHoldArmed) != 0)
            {
                BeginHold(RecorderUiState.Recording, RecorderUiState.StopHolding);
            }
            return;
        }

        if (Interlocked.Exchange(ref _f12Down, 0) == 0)
        {
            return;
        }
        _thresholdTimer.Change(Timeout.Infinite, Timeout.Infinite);
        var state = CurrentState;
        if (state == RecorderUiState.StartHolding)
        {
            Transition(RecorderUiState.StartHolding, RecorderUiState.Armed);
        }
        else if (state == RecorderUiState.StopHolding)
        {
            Transition(RecorderUiState.StopHolding, RecorderUiState.Recording);
        }
        else if (state == RecorderUiState.Recording)
        {
            Volatile.Write(ref _stopHoldArmed, 1);
        }
    }

    public bool MarkSaving() => Transition(RecorderUiState.Finalizing, RecorderUiState.Saving);

    public bool MarkArmedAfterSave() => Transition(RecorderUiState.Saving, RecorderUiState.Armed);

    internal bool BeginRecordingForCompatibilityTest() => Transition(RecorderUiState.Armed, RecorderUiState.Recording);

    internal bool BeginFinalizingForCompatibilityTest() => Transition(RecorderUiState.Recording, RecorderUiState.Finalizing);

    public void EnterErrorSafe(string code)
    {
        if (CurrentState is RecorderUiState.Disposed or RecorderUiState.ErrorSafe)
        {
            return;
        }
        Volatile.Write(ref _errorCode, string.IsNullOrWhiteSpace(code) ? "ERROR_SAFE" : code);
        Interlocked.Exchange(ref _state, (int)RecorderUiState.ErrorSafe);
        Interlocked.Increment(ref _version);
        _thresholdTimer.Change(Timeout.Infinite, Timeout.Infinite);
        Publish();
    }

    public bool RejectRecordingStartToArmed()
    {
        Volatile.Write(ref _stopHoldArmed, 1);
        return Transition(RecorderUiState.Recording, RecorderUiState.Armed);
    }

    private void BeginHold(RecorderUiState expected, RecorderUiState holding)
    {
        Interlocked.Exchange(ref _holdStartedAt, Environment.TickCount64);
        if (!Transition(expected, holding))
        {
            return;
        }
        _thresholdTimer.Change(_holdThresholdMs, Timeout.Infinite);
    }

    private void EvaluateHold()
    {
        var state = CurrentState;
        if (state is not RecorderUiState.StartHolding and not RecorderUiState.StopHolding)
        {
            return;
        }
        var elapsed = Math.Max(0, Environment.TickCount64 - Interlocked.Read(ref _holdStartedAt));
        if (elapsed < _holdThresholdMs)
        {
            return;
        }

        if (state == RecorderUiState.StartHolding && Transition(RecorderUiState.StartHolding, RecorderUiState.Recording))
        {
            Volatile.Write(ref _stopHoldArmed, 0);
            ThreadPool.UnsafeQueueUserWorkItem(_ => RecordingStarted?.Invoke(), null);
            return;
        }
        if (state == RecorderUiState.StopHolding && Transition(RecorderUiState.StopHolding, RecorderUiState.Finalizing))
        {
            ThreadPool.UnsafeQueueUserWorkItem(_ => FinalizationRequested?.Invoke(), null);
            return;
        }

        if (elapsed > _holdThresholdMs + _staleGraceMs && CurrentState is RecorderUiState.StartHolding or RecorderUiState.StopHolding)
        {
            EnterErrorSafe("HOLD_STATE_STALE_AT_ZERO");
        }
    }

    private bool Transition(RecorderUiState expected, RecorderUiState next)
    {
        if (Interlocked.CompareExchange(ref _state, (int)next, (int)expected) != (int)expected)
        {
            return false;
        }
        Interlocked.Increment(ref _version);
        Publish();
        return true;
    }

    private void Publish()
    {
        var snapshot = CurrentSnapshot;
        try
        {
            SnapshotChanged?.Invoke(snapshot);
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _thresholdTimer.Dispose();
        _watchdogTimer.Dispose();
        Interlocked.Exchange(ref _state, (int)RecorderUiState.Disposed);
        Interlocked.Increment(ref _version);
        Publish();
        GC.SuppressFinalize(this);
    }
}

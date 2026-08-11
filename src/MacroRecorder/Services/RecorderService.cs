using MacroCore.Diagnostics;
using MacroCore.Display;
using MacroCore.Input;
using MacroCore.Models;
using MacroCore.Security;
using MacroCore.Timing;

namespace MacroRecorder.Services;

public sealed class RecorderService : IDisposable
{
    private const int VkF12 = 0x7B;
    private const int KeyDown = 0x0100;
    private const int KeyDownSystem = 0x0104;
    private const int KeyUp = 0x0101;
    private const int KeyUpSystem = 0x0105;
    private const int MouseMove = 0x0200;
    private const int MouseWheel = 0x020A;
    private const int MouseHorizontalWheel = 0x020E;
    private const int DragDistancePixels = 4;
    private const int DragIntervalMs = 16;
    private const int DragTimedDistancePixels = 2;
    private const int SyntheticReleaseFlag = 2;
    private const int MaximumMacroEvents = 1_000_000;

    private readonly GlobalInputHook _hook = new();
    private readonly RawInputSource _rawInput = new();
    private readonly BoundedCapturePipeline _pipeline = new();
    private readonly InputCaptureCoordinator _captureCoordinator = new();
    private readonly CaptureLifetimeController _captureLifetime = new();
    private readonly RecorderSafetyStateMachine _stateMachine = new();
    private readonly SafetyWatchdogClient _watchdog = new();
    private readonly CaptureModeController _modeController;
    private readonly List<MacroEventRecord> _events = [];
    private readonly HashSet<KeyIdentity> _pressedKeys = [];
    private readonly HashSet<MouseButtonKind> _pressedMouseButtons = [];
    private readonly object _sync = new();
    private readonly MonotonicClock _clock = new();
    private readonly InputMonitorModel _inputMonitor = new();
    private readonly DesktopMouseMoveCoalescer _desktopMoveCoalescer = new();
    private readonly IWindowsPrivilegeService _privilegeService;
    private readonly IInputDesktopProbe _inputDesktopProbe;
    private readonly RecordingStartPrivilegeEvaluator _privilegeEvaluator;
    private bool _isRecording;
    private bool _disposed;
    private bool _sparseWarningLogged;
    private long _acceptedAtStop;
    private long _lastDragTimeMs = -1;
    private int _dragX;
    private int _dragY;
    private bool _hasDragPoint;
    private int _lastMouseX;
    private int _lastMouseY;
    private MacroPoint? _recordedCursorStart;

    public event Action<RecorderUiState>? StateChanged;
    public event Action<RecorderStateSnapshot>? StateSnapshotChanged;
    public event Action<MacroFile>? RecordingReady;
    public event Action<string>? Error;
    public event Action? EmergencyShutdownRequested;
    public event Action? ReplacementShutdownRequested;

    public RecorderUiState CurrentState => _stateMachine.CurrentState;
    public RecorderStateSnapshot CurrentStateSnapshot => _stateMachine.CurrentSnapshot;
    public bool IsPressedForF12 => _stateMachine.IsF12Down;
    public long F12PressedMs => _stateMachine.CurrentSnapshot.HoldElapsedMs;
    public bool IsCaptureArmed => _captureLifetime.IsArmed;
    public CaptureHealthSnapshot CaptureHealth => _captureCoordinator.GetSnapshot();
    public CaptureQueueStats QueueStats => _pipeline.GetStats();
    public InputMonitorSnapshot MonitorSnapshot => _inputMonitor.Snapshot();
    public RawInputRegistrationResult RawRegistration => _rawInput.RegistrationResult;
    public ForegroundWindowDiagnostic TargetDiagnostic { get; private set; }
    public RecorderCaptureMode CaptureMode => _modeController.Mode;
    public string WatchdogStatus => _watchdog.Status;
    public bool WatchdogHealthy => _watchdog.IsHealthy;

    public long RecordingElapsedMs
    {
        get { lock (_sync) { return _isRecording ? _clock.GetElapsedMilliseconds() : 0; } }
    }

    public CaptureInputMode CurrentInputMode => CaptureMode == RecorderCaptureMode.Standard
        ? CaptureInputMode.DesktopHook
        : CaptureHealth.ResolveMode(TargetDiagnostic.PermissionMismatch);

    public RecorderService()
        : this(new WindowsInputDesktopProbe())
    {
    }

    private RecorderService(IInputDesktopProbe inputDesktopProbe)
        : this(new WindowsPrivilegeService(inputDesktopProbe), inputDesktopProbe)
    {
    }

    public RecorderService(IWindowsPrivilegeService privilegeService, IInputDesktopProbe inputDesktopProbe)
    {
        _privilegeService = privilegeService ?? throw new ArgumentNullException(nameof(privilegeService));
        _inputDesktopProbe = inputDesktopProbe ?? throw new ArgumentNullException(nameof(inputDesktopProbe));
        _privilegeEvaluator = new RecordingStartPrivilegeEvaluator(_privilegeService, _inputDesktopProbe);
        TargetDiagnostic = ForegroundWindowDiagnostic.Empty(ToProcessIntegrity(_privilegeService.GetCurrentIntegrity()));
        _modeController = new CaptureModeController(RegisterRawInput, UnregisterRawInput);
        _hook.SuppressionMode = HookSuppressionMode.RecorderF12;
        _hook.TryEnqueue = _pipeline.TryEnqueue;
        _rawInput.TryEnqueue = _pipeline.TryEnqueue;
        _pipeline.EventReady += _captureCoordinator.Capture;
        _pipeline.StatsPublished += _inputMonitor.UpdateQueue;
        _pipeline.CircuitBreakerTripped += EnterErrorSafe;
        _captureCoordinator.EventClassified += _inputMonitor.Observe;
        _captureCoordinator.OutputCaptured += OnCapturedInput;
        _stateMachine.SnapshotChanged += OnStateSnapshotChanged;
        _stateMachine.RecordingStarted += StartRecording;
        _stateMachine.FinalizationRequested += BeginFinalization;
        _watchdog.EmergencyRequested += OnEmergencyRequested;
        _watchdog.ReplacementShutdownRequested += OnReplacementShutdownRequested;
    }

    public async Task StartAsync()
    {
        EnsureNotDisposed();
        try
        {
            RecorderDiagnosticsLog.HookHealth("startup=watchdog_connect_begin mode=Standard");
            await Task.Run(_watchdog.Start);
            if (_disposed)
            {
                _watchdog.Dispose();
                return;
            }
            RecorderDiagnosticsLog.HookHealth("startup=watchdog_connect_pass installing_ll_hooks=true");
            _captureLifetime.Arm(_hook.Start);
            UpdateRegistrationHealth();
            RecorderDiagnosticsLog.HookHealth(
                $"mode=Standard armed=true ll_keyboard={_hook.IsKeyboardHookActive} ll_mouse={_hook.IsMouseHookActive} " +
                $"keyboard_error={_hook.KeyboardHookError} mouse_error={_hook.MouseHookError} watchdog={_watchdog.Status}");
            if (!_hook.IsKeyboardHookActive || !_hook.IsMouseHookActive)
            {
                EnterErrorSafe($"LOW_LEVEL_HOOK_REGISTRATION_FAILED:{_hook.KeyboardHookError}:{_hook.MouseHookError}");
            }
        }
        catch (Exception ex)
        {
            EnterErrorSafe("SAFETY_START_FAILED:" + ex.GetType().Name);
            Error?.Invoke(ex.Message);
        }
    }

    public void Start() => StartAsync().GetAwaiter().GetResult();

    public bool SetRawEnhancedMode(bool enabled, bool explicitlyConfirmed)
    {
        if (CurrentState != RecorderUiState.Armed)
        {
            Error?.Invoke("只能在 ARMED 狀態切換輸入模式。");
            return false;
        }
        var success = enabled
            ? _modeController.EnableRawEnhanced(explicitlyConfirmed)
            : DisableRawMode();
        UpdateRegistrationHealth();
        return success;
    }

    public void ClearInputMonitor() => _inputMonitor.Clear();

    private bool DisableRawMode()
    {
        _modeController.DisableRawEnhanced();
        return true;
    }

    private bool RegisterRawInput()
    {
        var result = _rawInput.Start();
        RecorderDiagnosticsLog.RawInput(
            $"mode=RAW_ENHANCED registered={result.Success} keyboard={result.KeyboardRegistered} keyboard_error={result.KeyboardErrorCode} mouse={result.MouseRegistered} mouse_error={result.MouseErrorCode}");
        if (!result.Success)
        {
            Error?.Invoke(
                $"Raw Input 註冊失敗：Keyboard={result.KeyboardRegistered} (error {result.KeyboardErrorCode})；" +
                $"Mouse={result.MouseRegistered} (error {result.MouseErrorCode})。已保持標準模式。");
        }
        return result.Success;
    }

    private void UnregisterRawInput()
    {
        _rawInput.Stop();
        RecorderDiagnosticsLog.RawInput("mode=DESKTOP_SAFE unregistered=true");
    }

    private void UpdateRegistrationHealth() => _captureCoordinator.SetRegistrationHealth(
        _hook.IsKeyboardHookActive,
        _hook.IsMouseHookActive,
        _rawInput.RegistrationResult.KeyboardRegistered && _rawInput.IsRegistered,
        _rawInput.RegistrationResult.MouseRegistered && _rawInput.IsRegistered);

    public void Stop()
    {
        if (_disposed)
        {
            return;
        }
        _pipeline.AbortRecording();
        lock (_sync)
        {
            _isRecording = false;
            _events.Clear();
            _pressedKeys.Clear();
            _pressedMouseButtons.Clear();
        }
        _rawInput.Stop();
        _hook.Stop();
        _inputMonitor.Clear();
    }

    public bool PrepareReplacementShutdown()
    {
        bool discarded = CurrentState is RecorderUiState.Recording or RecorderUiState.Finalizing or RecorderUiState.Saving;
        Stop();
        RecorderDiagnosticsLog.HookHealth(
            $"replacement=shutdown discarded_active_recording={discarded} partial_macro_saved=false");
        return discarded;
    }

    public bool CheckSparseWarning()
    {
        lock (_sync)
        {
            if (!_isRecording || _clock.GetElapsedMilliseconds() < SparseRecordingClassifier.MinimumMeaningfulDurationMs)
            {
                return false;
            }
            var sparse = CaptureHealth.TotalObservedEvents < SparseRecordingClassifier.MinimumMeaningfulEventCount;
            if (sparse && !_sparseWarningLogged)
            {
                _sparseWarningLogged = true;
                RecorderDiagnosticsLog.GameCompatibility("status=SPARSE_CAPTURE_WARNING");
            }
            return sparse;
        }
    }

    private void OnCapturedInput(HookEvent input)
    {
        if (input.IsKeyboard && input.VirtualKey == VkF12)
        {
            if (input.Message is KeyDown or KeyDownSystem)
            {
                _stateMachine.HandleF12(true);
            }
            else if (input.Message is KeyUp or KeyUpSystem)
            {
                _stateMachine.HandleF12(false);
            }
            return;
        }

        try
        {
            if (input.IsOwnSyntheticInput || !IsRecording())
            {
                return;
            }
            if (input.IsKeyboard)
            {
                HandleKeyboardEvent(input);
            }
            else
            {
                HandleMouseEvent(input);
            }
        }
        catch (Exception ex)
        {
            EnterErrorSafe("CAPTURE_EVENT_ERROR:" + ex.GetType().Name);
        }
    }

    private void StartRecording()
    {
        var compatibilityStart = false;
        if (_disposed)
        {
            return;
        }
        if (CurrentState == RecorderUiState.Armed)
        {
            compatibilityStart = _stateMachine.BeginRecordingForCompatibilityTest();
        }
        if (CurrentState != RecorderUiState.Recording)
        {
            return;
        }
        ForegroundPrivilegeSnapshot privilegeSnapshot;
        if (compatibilityStart)
        {
            privilegeSnapshot = new ForegroundPrivilegeSnapshot(
                false,
                WindowsIntegrityLevel.Medium,
                WindowsIntegrityLevel.Medium,
                0,
                null,
                null);
        }
        else
        {
            var evaluation = _privilegeEvaluator.Evaluate();
            privilegeSnapshot = evaluation.PrivilegeSnapshot;
            TargetDiagnostic = ToTargetDiagnostic(privilegeSnapshot);
            DesktopSecurityProbeLog.Write(
                evaluation.DesktopProbe,
                privilegeSnapshot,
                CaptureMode.ToString(),
                evaluation.Decision);
            if (!evaluation.IsAllowed)
            {
                _stateMachine.RejectRecordingStartToArmed();
                Error?.Invoke(evaluation.UserMessage ?? "錄製未開始。");
                return;
            }

            RecordingPrivilegeTracker.Begin(_privilegeService, CaptureMode.ToString(), privilegeSnapshot);
        }

        TargetDiagnostic = ToTargetDiagnostic(privilegeSnapshot);
        _captureCoordinator.BeginRecording(CaptureMode == RecorderCaptureMode.RawEnhanced);
        MacroPoint? cursorStart = null;
        if (WindowsCursorPosition.TryGet(out var cursorX, out var cursorY))
        {
            cursorStart = new MacroPoint { X = cursorX, Y = cursorY };
        }
        lock (_sync)
        {
            _events.Clear();
            _pressedKeys.Clear();
            _pressedMouseButtons.Clear();
            _hasDragPoint = false;
            _sparseWarningLogged = false;
            _isRecording = true;
            _clock.Restart();
            _lastDragTimeMs = -1;
            _lastMouseX = 0;
            _lastMouseY = 0;
            _recordedCursorStart = cursorStart;
            _desktopMoveCoalescer.Reset(cursorStart?.X, cursorStart?.Y);
            if (!compatibilityStart && cursorStart is not null)
            {
                _events.Add(new MacroEventRecord
                {
                    Type = MacroEventKind.MouseMove,
                    TimeMs = 0,
                    X = cursorStart.X,
                    Y = cursorStart.Y,
                    CaptureSource = CaptureSourceKind.LowLevelMouse,
                    MouseMovementMode = MouseMovementMode.DesktopAbsolute,
                    MouseTrajectoryCapabilities = MouseTrajectoryCapabilities.AbsolutePosition,
                    IsInitialCursorAnchor = true
                });
            }
        }
        _pipeline.BeginRecording(clearPendingControlEvents: false);
        RecorderDiagnosticsLog.GameCompatibility(
            $"recording=start safety_mode={CaptureMode} process={TargetDiagnostic.ProcessName} target_integrity={TargetDiagnostic.ProcessIntegrity} recorder_integrity={TargetDiagnostic.RecorderIntegrity}");
    }

    private static ForegroundWindowDiagnostic ToTargetDiagnostic(ForegroundPrivilegeSnapshot snapshot) =>
        new(
            IntPtr.Zero,
            snapshot.TargetProcessId,
            snapshot.TargetProcessName ?? string.Empty,
            snapshot.TargetWindowTitle ?? string.Empty,
            ToProcessIntegrity(snapshot.TargetIntegrity),
            ToProcessIntegrity(snapshot.RecorderIntegrity),
            false,
            false,
            false);

    private static ProcessIntegrityKind ToProcessIntegrity(WindowsIntegrityLevel value) => value switch
    {
        WindowsIntegrityLevel.Low => ProcessIntegrityKind.Low,
        WindowsIntegrityLevel.Medium => ProcessIntegrityKind.Medium,
        WindowsIntegrityLevel.High => ProcessIntegrityKind.High,
        WindowsIntegrityLevel.System => ProcessIntegrityKind.System,
        _ => ProcessIntegrityKind.Unknown
    };

    private void BeginFinalization()
    {
        _acceptedAtStop = _pipeline.EndIngestion();
        Task.Run(FinalizeRecording);
    }

    private void StopRecording()
    {
        if (CurrentState == RecorderUiState.Recording)
        {
            _stateMachine.BeginFinalizingForCompatibilityTest();
        }
        if (CurrentState != RecorderUiState.Finalizing)
        {
            return;
        }
        _acceptedAtStop = _pipeline.EndIngestion();
        FinalizeRecording();
    }

    private void FinalizeRecording()
    {
        if (!_pipeline.WaitForDrain(_acceptedAtStop, TimeSpan.FromSeconds(3)))
        {
            EnterErrorSafe("CAPTURE_DRAIN_TIMEOUT");
            return;
        }

        List<MacroEventRecord> completedEvents;
        long releaseTime;
        lock (_sync)
        {
            if (!_isRecording)
            {
                EnterErrorSafe("FINALIZE_WITHOUT_RECORDING");
                return;
            }
            var elapsed = _clock.GetElapsedMilliseconds();
            FlushDesktopMoveLocked(elapsed);
            releaseTime = Math.Max(elapsed, _events.Count == 0 ? 0 : _events[^1].TimeMs);
            AppendSyntheticReleaseEvents(releaseTime);
            _isRecording = false;
            _hasDragPoint = false;
            completedEvents = _events.Select(CloneEvent).ToList();
        }

        MacroCore.Security.RecordingPrivilegeTracker.End();

        var health = _captureCoordinator.EndRecording();
        if (!_stateMachine.MarkSaving())
        {
            EnterErrorSafe("FINALIZING_STATE_TRANSITION_FAILED");
            return;
        }
        var mode = CurrentInputMode;
        var completed = new MacroFile
        {
            MacroName = $"macro_{DateTime.Now:yyyyMMdd_HHmmss}",
            CreatedAt = DateTimeOffset.UtcNow,
            DurationMs = releaseTime,
            RecordedDisplayLayout = DisplayLayoutProvider.GetCurrentLayout(),
            CaptureMetadata = new MacroCaptureMetadata
            {
                InputMode = mode,
                TargetProcessName = TargetDiagnostic.ProcessName,
                TargetIntegrity = TargetDiagnostic.ProcessIntegrity,
                RecorderIntegrity = TargetDiagnostic.RecorderIntegrity,
                RecordedRecorderIntegrity = TargetDiagnostic.RecorderIntegrity.ToString(),
                RecordedTargetIntegrity = TargetDiagnostic.ProcessIntegrity.ToString(),
                RequiresElevationForPlayback = RecordingPrivilegeTracker.ResolveRequiresElevation(
                    TargetDiagnostic.RecorderIntegrity.ToString(),
                    TargetDiagnostic.ProcessIntegrity.ToString()),
                CaptureMode = CaptureMode == RecorderCaptureMode.RawEnhanced ? "RawEnhanced" : "Standard",
                TargetWindowTitle = null,
                RecordedWithVersion = typeof(RecorderService).Assembly.GetName().Version?.ToString() ?? "1.2",
                RecommendedMouseReplayMode = MouseReplayMode.AbsoluteDesktop,
                RecordedCursorStart = _recordedCursorStart is null
                    ? null
                    : new MacroPoint { X = _recordedCursorStart.X, Y = _recordedCursorStart.Y },
                CoversMonitorBounds = TargetDiagnostic.CoversMonitorBounds,
                LikelyBorderlessFullscreen = TargetDiagnostic.LikelyBorderlessFullscreen,
                LikelyExclusiveFullscreen = TargetDiagnostic.LikelyExclusiveFullscreen,
                LowLevelKeyboardCount = health.LowLevelKeyboardCount,
                LowLevelMouseCount = health.LowLevelMouseCount,
                RawKeyboardCount = health.RawKeyboardCount,
                RawMouseCount = health.RawMouseCount,
                DuplicateCount = health.DuplicateCount
            },
            Events = completedEvents
        };

        string? validationError = null;
        if (_pipeline.IsCircuitBreakerTripped ||
            !AbsoluteRecordingOutputGate.TryValidate(completed, out validationError) ||
            !MacroCore.Serialization.MacroSerializer.TryValidate(completed, out validationError))
        {
            EnterErrorSafe("MACRO_VALIDATION_FAILED");
            Error?.Invoke(validationError ?? "錄製資料未通過安全驗證，因此不會儲存。");
            return;
        }

        RecorderDiagnosticsLog.HookHealth(
            $"recording=stop safety_mode={CaptureMode} queue={QueueStats.QueueDepth}/{QueueStats.Capacity} events={completed.Events.Count} " +
            $"ll_keyboard={health.LowLevelKeyboardCount} ll_mouse={health.LowLevelMouseCount} raw_keyboard={health.RawKeyboardCount} raw_mouse={health.RawMouseCount}");
        _inputMonitor.Clear();
        RecordingReady?.Invoke(completed);
    }

    public void SaveCompleted()
    {
        _stateMachine.MarkArmedAfterSave();
    }

    private void HandleMouseEvent(HookEvent input)
    {
        _lastMouseX = input.MouseX;
        _lastMouseY = input.MouseY;
        if (input.Message is 0x0201 or 0x0204 or 0x0207 or 0x020B)
        {
            FlushDesktopMove();
            AddMouseButtonEvent(MacroEventKind.MouseDown, input);
        }
        else if (input.Message is 0x0202 or 0x0205 or 0x0208 or 0x020C)
        {
            FlushDesktopMove();
            FlushDragEndpoint(input);
            AddMouseButtonEvent(MacroEventKind.MouseUp, input);
        }
        else if (input.Message == MouseMove)
        {
            if (input.Source == HookSource.RawMouse)
            {
                AddEvent(MacroEventKind.MouseMove, input);
            }
            else
            {
                TryRecordDesktopMove(input);
            }
        }
        else if (input.Message == MouseWheel)
        {
            FlushDesktopMove();
            AddEvent(MacroEventKind.MouseWheel, input);
        }
        else if (input.Message == MouseHorizontalWheel)
        {
            FlushDesktopMove();
            AddEvent(MacroEventKind.MouseHorizontalWheel, input);
        }
    }

    private void HandleKeyboardEvent(HookEvent input)
    {
        // F12 controls Recorder. F11 passes through Windows but is reserved from macro payloads.
        if (input.VirtualKey is HookCallbackSafety.F12 or HookCallbackSafety.F11)
        {
            return;
        }
        if (input.Message is KeyDown or KeyDownSystem)
        {
            AddKeyboardEvent(MacroEventKind.KeyDown, input);
        }
        else if (input.Message is KeyUp or KeyUpSystem)
        {
            AddKeyboardEvent(MacroEventKind.KeyUp, input);
        }
    }

    private void AddMouseButtonEvent(MacroEventKind kind, HookEvent input)
    {
        if (!input.MouseButton.HasValue)
        {
            return;
        }
        lock (_sync)
        {
            if (!_isRecording) return;
            if (kind == MacroEventKind.MouseDown) _pressedMouseButtons.Add(input.MouseButton.Value);
            else
            {
                _pressedMouseButtons.Remove(input.MouseButton.Value);
                if (_pressedMouseButtons.Count == 0) _hasDragPoint = false;
            }
            AddEventLocked(kind, input);
        }
    }

    private void TryRecordDragMove(HookEvent input)
    {
        lock (_sync)
        {
            TryRecordDragMoveLocked(input);
        }
    }

    private void TryRecordDesktopMove(HookEvent input)
    {
        lock (_sync)
        {
            if (!_isRecording)
            {
                return;
            }
            if (_pressedMouseButtons.Count > 0)
            {
                TryRecordDragMoveLocked(input);
                return;
            }

            var elapsed = _clock.GetElapsedMilliseconds();
            var ready = _desktopMoveCoalescer.Observe(input, elapsed);
            if (ready is not null)
            {
                AddEventLocked(MacroEventKind.MouseMove, ready);
            }
        }
    }

    private void TryRecordDragMoveLocked(HookEvent input)
    {
            if (_pressedMouseButtons.Count == 0 || !_isRecording) return;
            var elapsed = _clock.GetElapsedMilliseconds();
            var dx = input.MouseX - _dragX;
            var dy = input.MouseY - _dragY;
            var distanceSquared = dx * dx + dy * dy;
            if (!_hasDragPoint ||
                distanceSquared >= DragDistancePixels * DragDistancePixels ||
                (elapsed - _lastDragTimeMs >= DragIntervalMs && distanceSquared >= DragTimedDistancePixels * DragTimedDistancePixels))
            {
                AddEventLocked(MacroEventKind.MouseMove, input);
                _dragX = input.MouseX;
                _dragY = input.MouseY;
                _lastDragTimeMs = elapsed;
                _hasDragPoint = true;
            }
    }

    private void FlushDesktopMove()
    {
        lock (_sync)
        {
            FlushDesktopMoveLocked(_clock.GetElapsedMilliseconds());
        }
    }

    private void FlushDesktopMoveLocked(long elapsedMilliseconds)
    {
        var pending = _desktopMoveCoalescer.Flush(elapsedMilliseconds);
        if (pending is not null)
        {
            AddEventLocked(MacroEventKind.MouseMove, pending);
        }
    }

    private void FlushDragEndpoint(HookEvent input)
    {
        lock (_sync)
        {
            if (!_isRecording || _pressedMouseButtons.Count == 0 ||
                (_hasDragPoint && _dragX == input.MouseX && _dragY == input.MouseY))
            {
                return;
            }
            AddEventLocked(MacroEventKind.MouseMove, input);
            _dragX = input.MouseX;
            _dragY = input.MouseY;
            _lastDragTimeMs = _clock.GetElapsedMilliseconds();
            _hasDragPoint = true;
        }
    }

    private void AddKeyboardEvent(MacroEventKind kind, HookEvent input)
    {
        lock (_sync)
        {
            if (!_isRecording) return;
            var identity = new KeyIdentity(input.VirtualKey, input.ScanCode, input.IsExtended);
            if (kind == MacroEventKind.KeyDown) _pressedKeys.Add(identity); else _pressedKeys.Remove(identity);
            AddEventLocked(kind, input);
        }
    }

    private void AddEvent(MacroEventKind kind, HookEvent input)
    {
        lock (_sync) { AddEventLocked(kind, input); }
    }

    private void AddEventLocked(MacroEventKind kind, HookEvent input)
    {
        if (!_isRecording) return;
        if (_events.Count >= MaximumMacroEvents)
        {
            ThreadPool.UnsafeQueueUserWorkItem(_ => EnterErrorSafe("MACRO_EVENT_LIMIT_REACHED"), null);
            return;
        }
        long timeMilliseconds = _clock.GetElapsedMilliseconds();
        if (input.IsMouse)
        {
            if (!AbsoluteRecordingMouseNormalizer.TryCreate(
                    kind,
                    input,
                    timeMilliseconds,
                    out MacroEventRecord? mouseRecord,
                    out string mouseError) || mouseRecord is null)
            {
                throw new InvalidDataException(mouseError);
            }
            _events.Add(mouseRecord);
            return;
        }

        _events.Add(new MacroEventRecord
        {
            Type = kind,
            TimeMs = timeMilliseconds,
            VirtualKey = input.VirtualKey,
            ScanCode = input.ScanCode,
            IsExtended = input.IsExtended,
            Flags = (input.IsInjected ? 1 : 0) |
                    (input.IsLowerIntegrityInjected ? 4 : 0) |
                    (input.IsE1 ? 8 : 0),
            CaptureSource = input.CaptureSource
        });
    }

    private void AppendSyntheticReleaseEvents(long releaseTime)
    {
        foreach (var key in _pressedKeys.OrderBy(x => x.VirtualKey).ThenBy(x => x.ScanCode))
        {
            _events.Add(new MacroEventRecord { Type = MacroEventKind.KeyUp, TimeMs = releaseTime, VirtualKey = key.VirtualKey, ScanCode = key.ScanCode, IsExtended = key.IsExtended, Flags = SyntheticReleaseFlag });
        }
        foreach (var button in _pressedMouseButtons.OrderBy(x => x))
        {
            _events.Add(new MacroEventRecord { Type = MacroEventKind.MouseUp, TimeMs = releaseTime, X = _lastMouseX, Y = _lastMouseY, MouseButton = button, Flags = SyntheticReleaseFlag });
        }
        _pressedKeys.Clear();
        _pressedMouseButtons.Clear();
    }

    private static MacroEventRecord CloneEvent(MacroEventRecord item) => new()
    {
        Type = item.Type, TimeMs = item.TimeMs, X = item.X, Y = item.Y, MouseButton = item.MouseButton,
        WheelDelta = item.WheelDelta, VirtualKey = item.VirtualKey, ScanCode = item.ScanCode,
        IsExtended = item.IsExtended, Flags = item.Flags, CaptureSource = item.CaptureSource,
        MouseMovementMode = item.MouseMovementMode, DeltaX = item.DeltaX, DeltaY = item.DeltaY,
        MouseTrajectoryCapabilities = item.MouseTrajectoryCapabilities,
        IsInitialCursorAnchor = item.IsInitialCursorAnchor
    };

    private bool IsRecording() { lock (_sync) { return _isRecording; } }

    private void EnterErrorSafe(string reason)
    {
        _pipeline.AbortRecording();
        lock (_sync)
        {
            _isRecording = false;
            _events.Clear();
            _pressedKeys.Clear();
            _pressedMouseButtons.Clear();
        }
        try { _rawInput.Stop(); } catch { }
        try { _hook.Stop(); } catch { }
        _inputMonitor.Clear();
        _stateMachine.EnterErrorSafe(reason);
        RecorderDiagnosticsLog.HookHealth($"status=ERROR_SAFE reason={reason}");
        Error?.Invoke($"已進入 ERROR SAFE：{reason}。Hook 與 Raw Input 已解除，未儲存不完整錄製。");
    }

    private void OnStateSnapshotChanged(RecorderStateSnapshot snapshot)
    {
        _watchdog.SetActivity(snapshot.State switch
        {
            RecorderUiState.Recording => "Recording",
            RecorderUiState.Finalizing => "Finalizing",
            RecorderUiState.Saving => "Finalizing",
            RecorderUiState.StartHolding or RecorderUiState.StopHolding => "Armed",
            _ => "Idle"
        });
        StateChanged?.Invoke(snapshot.State);
        StateSnapshotChanged?.Invoke(snapshot);
    }

    private void OnEmergencyRequested()
    {
        EmergencyShutdownRequested?.Invoke();
    }

    private void OnReplacementShutdownRequested()
    {
        ReplacementShutdownRequested?.Invoke();
    }

    private void EnsureNotDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
        _stateMachine.Dispose();
        _pipeline.Dispose();
        _rawInput.Dispose();
        _hook.Dispose();
        _watchdog.EmergencyRequested -= OnEmergencyRequested;
        _watchdog.ReplacementShutdownRequested -= OnReplacementShutdownRequested;
        _watchdog.Dispose();
        _captureLifetime.Dispose(() => { });
        GC.SuppressFinalize(this);
    }
}

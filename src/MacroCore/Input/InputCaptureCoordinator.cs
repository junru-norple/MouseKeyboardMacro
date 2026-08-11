using MacroCore.Models;

namespace MacroCore.Input;

public sealed record CaptureHealthSnapshot(
    bool LowLevelKeyboardRegistered,
    bool LowLevelMouseRegistered,
    bool RawKeyboardRegistered,
    bool RawMouseRegistered,
    long LowLevelKeyboardCount,
    long LowLevelMouseCount,
    long RawKeyboardCount,
    long RawMouseCount,
    long DeduplicatedKeyboardCount,
    long DeduplicatedMouseCount,
    long DuplicateCount,
    long LastEventTimestampMs,
    long LastEventAgeMs)
{
    public long TotalObservedEvents => LowLevelKeyboardCount + LowLevelMouseCount + RawKeyboardCount + RawMouseCount;
    public long TotalOutputEvents => DeduplicatedKeyboardCount + DeduplicatedMouseCount;

    public CaptureInputMode ResolveMode(bool permissionMismatch)
    {
        if (permissionMismatch)
        {
            return CaptureInputMode.UnsupportedPermissionMismatch;
        }

        var lowLevel = LowLevelKeyboardCount + LowLevelMouseCount > 0;
        var raw = RawKeyboardCount + RawMouseCount > 0;
        if (lowLevel && raw)
        {
            return CaptureInputMode.Hybrid;
        }

        if (raw)
        {
            return CaptureInputMode.RawInput;
        }

        return CaptureInputMode.DesktopHook;
    }
}

public sealed class InputCaptureCoordinator
{
    private const int DefaultDeduplicationWindowMs = 24;
    public const int MaximumRecentEvents = 512;
    private readonly object _sync = new();
    private readonly int _deduplicationWindowMs;
    private readonly List<RecentEvent> _recent = [];
    private bool _counting;
    private bool _preferRawMouse;
    private bool _rawDeltaSeen;
    private bool _llKeyboardRegistered;
    private bool _llMouseRegistered;
    private bool _rawKeyboardRegistered;
    private bool _rawMouseRegistered;
    private long _llKeyboardCount;
    private long _llMouseCount;
    private long _rawKeyboardCount;
    private long _rawMouseCount;
    private long _outputKeyboardCount;
    private long _outputMouseCount;
    private long _duplicateCount;
    private long _lastEventTimestampMs;

    public event Action<HookEvent>? OutputCaptured;
    public event Action<HookEvent, InputEventDisposition>? EventClassified;
    public int RecentCacheCount { get { lock (_sync) { return _recent.Count; } } }

    public InputCaptureCoordinator(int deduplicationWindowMs = DefaultDeduplicationWindowMs)
    {
        _deduplicationWindowMs = Math.Max(1, deduplicationWindowMs);
    }

    public void SetRegistrationHealth(bool llKeyboard, bool llMouse, bool rawKeyboard, bool rawMouse)
    {
        lock (_sync)
        {
            _llKeyboardRegistered = llKeyboard;
            _llMouseRegistered = llMouse;
            _rawKeyboardRegistered = rawKeyboard;
            _rawMouseRegistered = rawMouse;
        }
    }

    public void BeginRecording(bool preferRawMouse)
    {
        lock (_sync)
        {
            _recent.Clear();
            _counting = true;
            _preferRawMouse = preferRawMouse;
            _rawDeltaSeen = false;
            _llKeyboardCount = 0;
            _llMouseCount = 0;
            _rawKeyboardCount = 0;
            _rawMouseCount = 0;
            _outputKeyboardCount = 0;
            _outputMouseCount = 0;
            _duplicateCount = 0;
            _lastEventTimestampMs = 0;
        }
    }

    public CaptureHealthSnapshot EndRecording()
    {
        lock (_sync)
        {
            _counting = false;
            return CreateSnapshot(Environment.TickCount64);
        }
    }

    public CaptureHealthSnapshot GetSnapshot(long? nowTimestampMs = null)
    {
        lock (_sync)
        {
            return CreateSnapshot(nowTimestampMs ?? Environment.TickCount64);
        }
    }

    public void Capture(HookEvent input)
    {
        var emit = false;
        var disposition = InputEventDisposition.Unsupported;
        lock (_sync)
        {
            if (input.IsOwnSyntheticInput)
            {
                disposition = InputEventDisposition.OwnSyntheticFiltered;
            }
            else
            {
                CountSource(input);
                _lastEventTimestampMs = Math.Max(_lastEventTimestampMs, input.TimestampMs);
                var duplicate = false;

                if (input.Source == HookSource.RawMouse && input.IsMouseMove && !input.IsAbsoluteMouse)
                {
                    _rawDeltaSeen = true;
                    duplicate = !_preferRawMouse;
                }
                else if (input.Source == HookSource.Mouse && input.IsMouseMove && _preferRawMouse && _rawDeltaSeen)
                {
                    duplicate = true;
                }

                if (!duplicate && TryCreateSignature(input, out var signature))
                {
                    RemoveExpired(input.TimestampMs);
                    var raw = input.IsRaw;
                    var duplicateIndex = _recent.FindIndex(item =>
                        item.Signature == signature &&
                        item.IsRaw != raw &&
                        Math.Abs(input.TimestampMs - item.TimestampMs) <= _deduplicationWindowMs);
                    if (duplicateIndex >= 0)
                    {
                        _recent.RemoveAt(duplicateIndex);
                        duplicate = true;
                    }
                    else
                    {
                        if (_recent.Count >= MaximumRecentEvents)
                        {
                            _recent.RemoveAt(0);
                        }
                        _recent.Add(new RecentEvent(signature, raw, input.TimestampMs));
                    }
                }

                if (duplicate)
                {
                    _duplicateCount++;
                    disposition = InputEventDisposition.Duplicate;
                }
                else
                {
                    var control = input.IsKeyboard && input.VirtualKey is HookCallbackSafety.F11 or HookCallbackSafety.F12;
                    if (_counting && !control)
                    {
                        if (input.IsKeyboard) _outputKeyboardCount++;
                        else if (input.IsMouse) _outputMouseCount++;
                    }

                    disposition = control
                        ? InputEventDisposition.ControlNotRecorded
                        : _counting ? InputEventDisposition.Recorded : InputEventDisposition.MonitoredOnly;
                    emit = true;
                }
            }
        }

        try
        {
            EventClassified?.Invoke(input, disposition);
        }
        catch
        {
        }
        if (emit)
        {
            OutputCaptured?.Invoke(input);
        }
    }

    private void CountSource(HookEvent input)
    {
        if (!_counting)
        {
            return;
        }

        switch (input.Source)
        {
            case HookSource.Keyboard:
                _llKeyboardCount++;
                break;
            case HookSource.Mouse:
                _llMouseCount++;
                break;
            case HookSource.RawKeyboard:
                _rawKeyboardCount++;
                break;
            case HookSource.RawMouse:
                _rawMouseCount++;
                break;
        }
    }

    private CaptureHealthSnapshot CreateSnapshot(long nowTimestampMs)
    {
        var age = _lastEventTimestampMs <= 0 ? -1 : Math.Max(0, nowTimestampMs - _lastEventTimestampMs);
        return new CaptureHealthSnapshot(
            _llKeyboardRegistered,
            _llMouseRegistered,
            _rawKeyboardRegistered,
            _rawMouseRegistered,
            _llKeyboardCount,
            _llMouseCount,
            _rawKeyboardCount,
            _rawMouseCount,
            _outputKeyboardCount,
            _outputMouseCount,
            _duplicateCount,
            _lastEventTimestampMs,
            age);
    }

    private void RemoveExpired(long timestampMs)
    {
        _recent.RemoveAll(item => timestampMs - item.TimestampMs > _deduplicationWindowMs);
    }

    private static bool TryCreateSignature(HookEvent input, out EventSignature signature)
    {
        signature = default;
        if (input.IsKeyboard)
        {
            var transition = input.Message is 0x0100 or 0x0104 ? 1 : input.Message is 0x0101 or 0x0105 ? 2 : 0;
            if (transition == 0)
            {
                return false;
            }

            signature = new EventSignature(1, transition, input.VirtualKey, input.ScanCode, input.IsExtended ? 1 : 0);
            return true;
        }

        if (!input.IsMouse || input.IsMouseMove)
        {
            return false;
        }

        var value = input.Message is 0x020A or 0x020E ? (short)(input.MouseData >> 16) : (int?)input.MouseButton ?? -1;
        signature = new EventSignature(2, input.Message, value, 0, 0);
        return true;
    }

    private readonly record struct EventSignature(int Group, int Transition, int Value1, int Value2, int Value3);
    private readonly record struct RecentEvent(EventSignature Signature, bool IsRaw, long TimestampMs);
}

public sealed class CaptureLifetimeController
{
    public bool IsArmed { get; private set; }
    public bool IsDisposed { get; private set; }

    public void Arm(Action startCapture)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (IsArmed)
        {
            return;
        }

        startCapture();
        IsArmed = true;
    }

    public void NotifyWindowDeactivated() { }
    public void NotifyWindowMinimized() { }
    public void NotifyRecordingStarted() { }
    public void NotifyRecordingStopped() { }

    public void Dispose(Action stopCapture)
    {
        if (IsDisposed)
        {
            return;
        }

        if (IsArmed)
        {
            stopCapture();
        }

        IsArmed = false;
        IsDisposed = true;
    }
}

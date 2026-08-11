using System.Buffers;
using System.ComponentModel;
using System.Runtime.InteropServices;
using MacroCore.Input;
using MacroCore.Models;
using MacroCore.Timing;

namespace MacroPlayer;

public sealed class SafePlaybackServiceFactory : IPlaybackServiceFactory
{
    public SafePlaybackServiceFactory(IForegroundWindowService foreground) => _ = foreground;

    public IPlaybackSession Create(
        PlaybackMacroDocument macro,
        PlaybackExecutionContext context,
        IPlaybackFocusPolicy focusPolicy)
    {
        AbsoluteOnlyPlaybackGate.EnsureValid(macro);
        EffectivePlaybackPrivilegeResolution privilege = EffectivePlaybackPrivilegeResolver.Resolve(macro);
        if (privilege.Requirement == EffectivePlaybackPrivilegeRequirement.Administrator && !context.PlayerElevated)
        {
            throw new PlaybackPrivilegeRejectedException(privilege.Reason);
        }

        return new SafePlaybackSession(macro, context, focusPolicy);
    }
}

public sealed class PlaybackPrivilegeRejectedException : InvalidOperationException
{
    public PlaybackPrivilegeRejectedException(string message) : base(message)
    {
    }
}

public sealed class SafePlaybackSession : IPlaybackSession
{
    private static readonly nint SyntheticMarker = unchecked((nint)InputSyntheticMarker.NumericValue);
    private static readonly int NativeInputSize = Marshal.SizeOf<INPUT>();
    private const int InputMouse = 0;
    private const int InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventScanCode = 0x0008;
    private const uint KeyEventExtended = 0x0001;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint MouseRightDown = 0x0008;
    private const uint MouseRightUp = 0x0010;
    private const uint MouseMiddleDown = 0x0020;
    private const uint MouseMiddleUp = 0x0040;
    private const uint MouseXDown = 0x0080;
    private const uint MouseXUp = 0x0100;
    private const uint MouseWheel = 0x0800;
    private const uint MouseHorizontalWheel = 0x1000;
    private const int VkF11 = 0x7A;

    private readonly PlaybackMacroDocument _macro;
    private readonly IPlaybackFocusPolicy _focusPolicy;
    private readonly Rectangle _virtualScreen;
    private readonly ISafePlaybackNativeSink? _nativeSink;
    private readonly PlaybackTimelineScheduler _scheduler;
    private readonly PlaybackProgressThrottler _progressThrottler = new(10);
    private readonly HeldPlaybackInputs _heldInputs = new();
    private readonly CancellationTokenSource _stop = new();
    private PlaybackTimingMetrics? _timingMetrics;
    private PlaybackSafetyMonitor? _safetyMonitor;
    private int _sent;
    private int _first;
    private int _f11Stop;
    private int _sendInputCalls;
    private int _nativeInputs;
    private int _lastProgressWasFinal;
    private bool _disposed;

    public SafePlaybackSession(
        PlaybackMacroDocument macro,
        PlaybackExecutionContext context,
        IPlaybackFocusPolicy focusPolicy,
        IPlaybackClock? clock = null,
        ISafePlaybackNativeSink? nativeSink = null)
    {
        _macro = macro;
        _ = context;
        _focusPolicy = focusPolicy;
        AbsoluteOnlyPlaybackGate.EnsureValid(macro);
        _virtualScreen = SystemInformation.VirtualScreen;
        _nativeSink = nativeSink;
        _scheduler = new PlaybackTimelineScheduler(clock ?? SystemPlaybackClock.Instance);
    }

    public event EventHandler? FirstEventSent;
    public event EventHandler<PlaybackProgress>? ProgressChanged;
    public bool FirstEventWasSent => Volatile.Read(ref _first) != 0;
    public int EventsSentCount => Volatile.Read(ref _sent);
    public int FocusChangeCount => _focusPolicy.FocusChangeCount;
    public PlaybackTimingMetrics? TimingMetrics => _timingMetrics;
    public PlaybackRuntimeCounters RuntimeCounters => new(
        _focusPolicy.FullResolutionCount,
        _focusPolicy.FastProbeCount,
        _progressThrottler.PublishedCount,
        Volatile.Read(ref _sendInputCalls),
        Volatile.Read(ref _nativeInputs),
        _safetyMonitor?.SafetyStopCount ?? 0);

    public async Task<PlaybackRunResult> PlayAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        CancellationToken token = linked.Token;
        PlaybackTimelineEvent<PlaybackMacroEvent>[] timeline = BuildTimeline();
        PlaybackSafetyMonitor safetyMonitor = new(_focusPolicy, TimeSpan.FromMilliseconds(150));
        _safetyMonitor = safetyMonitor;
        Task emergencyTask = MonitorF11Async(linked, token);
        Task safetyTask = safetyMonitor.RunAsync(linked, token);
        PlaybackRunResult result;

        try
        {
            _timingMetrics = await _scheduler.RunAsync(
                timeline,
                _macro.DurationMilliseconds,
                DispatchBatchAsync,
                OnTimelineProgress,
                token).ConfigureAwait(false);
            result = PlaybackRunResult.Success(EventsSentCount, FocusChangeCount);
        }
        catch (OperationCanceledException)
        {
            PlaybackSafetyCheck? safetyFailure = safetyMonitor.Failure;
            PlaybackDisposition disposition = safetyFailure?.Kind switch
            {
                PlaybackSafetyFailureKind.SecureDesktop => PlaybackDisposition.SecureDesktop,
                _ when Volatile.Read(ref _f11Stop) != 0 => PlaybackDisposition.F11Stop,
                _ => PlaybackDisposition.Cancelled
            };
            string? message = safetyFailure?.Reason;
            if (string.IsNullOrWhiteSpace(message))
            {
                message = disposition == PlaybackDisposition.F11Stop ? "播放已由 F11 緊急停止。" : "播放已停止。";
            }
            result = PlaybackRunResult.Stopped(EventsSentCount, message, disposition, FocusChangeCount);
        }
        catch (Exception ex)
        {
            result = PlaybackRunResult.Failure(
                EventsSentCount,
                "播放失敗：" + ex.Message,
                PlaybackDisposition.Failed,
                FocusChangeCount);
        }
        finally
        {
            linked.Cancel();
            await AwaitMonitorAsync(emergencyTask).ConfigureAwait(false);
            await AwaitMonitorAsync(safetyTask).ConfigureAwait(false);
            _timingMetrics ??= _scheduler.LastMetrics;
            ReleaseHeldInputs();
            PublishTerminalProgress();
        }

        return result with
        {
            TimingMetrics = _timingMetrics,
            RuntimeCounters = RuntimeCounters,
            FocusChangeCount = FocusChangeCount
        };
    }

    public void Stop() => _stop.Cancel();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stop.Cancel();
        ReleaseHeldInputs();
        _stop.Dispose();
    }

    private PlaybackTimelineEvent<PlaybackMacroEvent>[] BuildTimeline()
    {
        PlaybackTimelineEvent<PlaybackMacroEvent>[] timeline = new PlaybackTimelineEvent<PlaybackMacroEvent>[_macro.Events.Count];
        for (int index = 0; index < timeline.Length; index++)
        {
            PlaybackMacroEvent item = _macro.Events[index];
            PlaybackTimelineEventClass classification = item.Kind == PlaybackEventKind.MouseMove
                ? PlaybackTimelineEventClass.MouseMove
                : PlaybackTimelineEventClass.Essential;
            timeline[index] = new PlaybackTimelineEvent<PlaybackMacroEvent>(
                item.OffsetMilliseconds,
                item,
                classification);
        }
        return timeline;
    }

    private ValueTask DispatchBatchAsync(
        IReadOnlyList<PlaybackTimelineEvent<PlaybackMacroEvent>> source,
        int startIndex,
        int count,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SafePlaybackNativeInput[] batch = new SafePlaybackNativeInput[count];
        for (int index = 0; index < count; index++)
        {
            batch[index] = BuildNativeInput(source[startIndex + index].Value);
        }

        Interlocked.Increment(ref _sendInputCalls);
        uint nativeSent = SendNative(batch);
        int acknowledged = Math.Min((int)nativeSent, count);
        for (int index = 0; index < acknowledged; index++)
        {
            _heldInputs.Track(source[startIndex + index].Value);
        }
        if (acknowledged > 0)
        {
            Interlocked.Add(ref _sent, acknowledged);
            Interlocked.Add(ref _nativeInputs, acknowledged);
            if (Interlocked.CompareExchange(ref _first, 1, 0) == 0)
            {
                FirstEventSent?.Invoke(this, EventArgs.Empty);
            }
        }

        if (nativeSent != count)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"SendInput accepted {nativeSent} of {count} inputs.");
        }

        return ValueTask.CompletedTask;
    }

    private void OnTimelineProgress(PlaybackTimelineProgress progress)
    {
        if (!_progressThrottler.ShouldPublish(progress.WallElapsedMilliseconds, progress.IsFinal))
        {
            return;
        }

        if (progress.IsFinal)
        {
            Interlocked.Exchange(ref _lastProgressWasFinal, 1);
        }
        ProgressChanged?.Invoke(this, new PlaybackProgress(
            EventsSentCount,
            progress.TotalEvents,
            TimeSpan.FromMilliseconds(progress.WallElapsedMilliseconds))
        {
            TimelinePosition = TimeSpan.FromMilliseconds(progress.TimelinePositionMilliseconds),
            Drift = TimeSpan.FromMilliseconds(progress.DriftMilliseconds)
        });
    }

    private void PublishTerminalProgress()
    {
        if (EventsSentCount == 0 || Volatile.Read(ref _lastProgressWasFinal) != 0 || _timingMetrics is null)
        {
            return;
        }

        if (_progressThrottler.ShouldPublish(_timingMetrics.WallPlaybackDurationMilliseconds, force: true))
        {
            ProgressChanged?.Invoke(this, new PlaybackProgress(
                EventsSentCount,
                _macro.Events.Count,
                TimeSpan.FromMilliseconds(_timingMetrics.WallPlaybackDurationMilliseconds))
            {
                TimelinePosition = TimeSpan.FromMilliseconds(_timingMetrics.TimelinePositionMilliseconds),
                Drift = TimeSpan.FromMilliseconds(_timingMetrics.FinalDriftMilliseconds)
            });
        }
    }

    private async Task MonitorF11Async(CancellationTokenSource linked, CancellationToken token)
    {
        System.Diagnostics.Stopwatch? held = null;
        while (!token.IsCancellationRequested)
        {
            bool down = (GetAsyncKeyState(VkF11) & 0x8000) != 0;
            if (down)
            {
                held ??= System.Diagnostics.Stopwatch.StartNew();
                if (held.Elapsed >= TimeSpan.FromSeconds(2))
                {
                    Interlocked.Exchange(ref _f11Stop, 1);
                    linked.Cancel();
                    return;
                }
            }
            else
            {
                held = null;
            }

            await Task.Delay(40, token).ConfigureAwait(false);
        }
    }

    private static async Task AwaitMonitorAsync(Task monitor)
    {
        try
        {
            await monitor.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private SafePlaybackNativeInput BuildNativeInput(PlaybackMacroEvent item) => item.Kind switch
    {
        PlaybackEventKind.KeyDown => BuildKeyboard(new PlaybackKeyIdentity(item.VirtualKey, item.ScanCode, item.Extended), false),
        PlaybackEventKind.KeyUp => BuildKeyboard(new PlaybackKeyIdentity(item.VirtualKey, item.ScanCode, item.Extended), true),
        PlaybackEventKind.MouseMove or
        PlaybackEventKind.MouseDown or
        PlaybackEventKind.MouseUp or
        PlaybackEventKind.MouseWheel or
        PlaybackEventKind.MouseHorizontalWheel => BuildMouseInput(item),
        _ => throw new InvalidOperationException("Unsupported playback event.")
    };

    private SafePlaybackNativeInput BuildMouseInput(PlaybackMacroEvent item)
    {
        AbsoluteDesktopMousePacket packet = AbsoluteDesktopInputComposer.Compose(item, _virtualScreen);
        return new SafePlaybackNativeInput(
            SafePlaybackNativeInputKind.Mouse,
            X: packet.X,
            Y: packet.Y,
            MouseData: packet.MouseData,
            Flags: packet.Flags,
            ExtraInfo: SyntheticMarker);
    }

    private static SafePlaybackNativeInput BuildKeyboard(PlaybackKeyIdentity identity, bool keyUp)
    {
        uint flags = keyUp ? KeyEventKeyUp : 0;
        ushort virtualKey = (ushort)identity.VirtualKey;
        ushort scan = (ushort)identity.ScanCode;
        if (scan != 0)
        {
            virtualKey = 0;
            flags |= KeyEventScanCode;
        }
        if (identity.Extended)
        {
            flags |= KeyEventExtended;
        }

        return new SafePlaybackNativeInput(
            SafePlaybackNativeInputKind.Keyboard,
            Flags: flags,
            VirtualKey: virtualKey,
            ScanCode: scan,
            ExtraInfo: SyntheticMarker);
    }

    private static SafePlaybackNativeInput BuildMouseButton(string button, bool up)
    {
        string normalized = button.ToUpperInvariant();
        uint flags = normalized switch
        {
            "RIGHT" => up ? MouseRightUp : MouseRightDown,
            "MIDDLE" => up ? MouseMiddleUp : MouseMiddleDown,
            "X1" or "XBUTTON1" or "X2" or "XBUTTON2" => up ? MouseXUp : MouseXDown,
            _ => up ? MouseLeftUp : MouseLeftDown
        };
        uint mouseData = normalized is "X2" or "XBUTTON2" ? 2u : normalized is "X1" or "XBUTTON1" ? 1u : 0u;
        return new SafePlaybackNativeInput(
            SafePlaybackNativeInputKind.Mouse,
            MouseData: mouseData,
            Flags: flags,
            ExtraInfo: SyntheticMarker);
    }

    private void ReleaseHeldInputs()
    {
        (PlaybackKeyIdentity[] keys, string[] buttons) = _heldInputs.Drain();
        foreach (PlaybackKeyIdentity key in keys)
        {
            TrySend(BuildKeyboard(key, keyUp: true));
        }
        foreach (string button in buttons)
        {
            TrySend(BuildMouseButton(button, up: true));
        }
    }

    private void TrySend(SafePlaybackNativeInput input)
    {
        try
        {
            Interlocked.Increment(ref _sendInputCalls);
            uint sent = SendNative([input]);
            if (sent == 1)
            {
                Interlocked.Increment(ref _nativeInputs);
            }
        }
        catch
        {
        }
    }

    private uint SendNative(IReadOnlyList<SafePlaybackNativeInput> source)
    {
        if (_nativeSink is not null)
        {
            return _nativeSink.Send(source);
        }

        INPUT[] buffer = ArrayPool<INPUT>.Shared.Rent(source.Count);
        try
        {
            for (int index = 0; index < source.Count; index++)
            {
                buffer[index] = ToNativeInput(source[index]);
            }
            return SendInput((uint)source.Count, buffer, NativeInputSize);
        }
        finally
        {
            ArrayPool<INPUT>.Shared.Return(buffer);
        }
    }

    private static INPUT ToNativeInput(SafePlaybackNativeInput input) => input.Kind switch
    {
        SafePlaybackNativeInputKind.Mouse => new INPUT
        {
            Type = InputMouse,
            Data = new InputUnion
            {
                Mouse = new MOUSEINPUT
                {
                    X = input.X,
                    Y = input.Y,
                    MouseData = input.MouseData,
                    Flags = input.Flags,
                    ExtraInfo = input.ExtraInfo
                }
            }
        },
        SafePlaybackNativeInputKind.Keyboard => new INPUT
        {
            Type = InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KEYBDINPUT
                {
                    VirtualKey = input.VirtualKey,
                    ScanCode = input.ScanCode,
                    Flags = input.Flags,
                    ExtraInfo = input.ExtraInfo
                }
            }
        },
        _ => throw new InvalidOperationException("Unsupported native input kind.")
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT Mouse;
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint numberOfInputs, INPUT[] inputs, int sizeOfInput);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}

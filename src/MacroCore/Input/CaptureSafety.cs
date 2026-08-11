using System.Diagnostics;
using System.Threading.Channels;

namespace MacroCore.Input;

public enum HookSuppressionMode
{
    None,
    RecorderF12,
    PlayerF11
}

public enum RecorderCaptureMode
{
    Standard,
    RawEnhanced
}

public readonly record struct HookDispatchResult(bool Enqueued, bool Suppressed, bool CallNext);

public static class HookCallbackSafety
{
    public const int F11 = 0x7A;
    public const int F12 = 0x7B;

    public static HookDispatchResult Dispatch(
        HookEvent input,
        HookSuppressionMode suppressionMode,
        Func<HookEvent, bool>? tryEnqueue)
    {
        var enqueued = false;
        try
        {
            enqueued = tryEnqueue?.Invoke(input) ?? false;
        }
        catch
        {
            // A producer failure must never escape into the Windows hook chain.
        }

        var suppressed = input.Source == HookSource.Keyboard && suppressionMode switch
        {
            HookSuppressionMode.RecorderF12 => input.VirtualKey == F12,
            HookSuppressionMode.PlayerF11 => input.VirtualKey == F11,
            _ => false
        };

        // Suppressed control keys intentionally stop here. Every other event calls next.
        return new HookDispatchResult(enqueued, suppressed, !suppressed);
    }
}

public readonly record struct CaptureQueueStats(
    int Capacity,
    int QueueDepth,
    int UsagePercent,
    long Accepted,
    long Processed,
    long IgnoredBeforeRecording,
    long DroppedMoveEvents,
    long RawReports,
    long AggregatedRawMoves,
    long EventsPerSecond,
    bool Warning50,
    bool Shedding80,
    bool CircuitBreakerTripped);

public sealed class BoundedCapturePipeline : IDisposable
{
    public const int DefaultCapacity = 8192;
    public const int RawAggregationWindowMs = 12;

    private readonly Channel<HookEvent> _channel;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task? _consumerTask;
    private readonly System.Threading.Timer _rawFlushTimer;
    private readonly System.Threading.Timer _statsTimer;
    private readonly object _rawSync = new();
    private readonly int _controlVirtualKey;
    private readonly int _capacity;
    private int _recording;
    private int _disposed;
    private int _queueDepth;
    private int _circuitBreaker;
    private int _rawDx;
    private int _rawDy;
    private int _rawX;
    private int _rawY;
    private int _rawCount;
    private bool _rawHasAbsolutePosition;
    private bool _rawHasRelativeDelta;
    private long _accepted;
    private long _processed;
    private long _ignoredBeforeRecording;
    private long _droppedMoves;
    private long _rawReports;
    private long _aggregatedRawMoves;
    private long _rateWindowAccepted;
    private long _rateWindowStart = Environment.TickCount64;
    private long _eventsPerSecond;

    public event Action<HookEvent>? EventReady;
    public event Action<string>? CircuitBreakerTripped;
    public event Action<CaptureQueueStats>? StatsPublished;

    public BoundedCapturePipeline(
        int capacity = DefaultCapacity,
        int controlVirtualKey = HookCallbackSafety.F12,
        bool startConsumer = true)
    {
        _capacity = Math.Max(16, capacity);
        _controlVirtualKey = controlVirtualKey;
        _channel = Channel.CreateBounded<HookEvent>(new BoundedChannelOptions(_capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        if (startConsumer)
        {
            _consumerTask = Task.Run(ConsumeAsync);
        }
        _rawFlushTimer = new System.Threading.Timer(_ => FlushRawAggregation(), null, RawAggregationWindowMs, RawAggregationWindowMs);
        _statsTimer = new System.Threading.Timer(_ => PublishStats(), null, 250, 250);
    }

    public int Capacity => _capacity;
    public int QueueDepth => Math.Max(0, Volatile.Read(ref _queueDepth));
    public bool IsRecording => Volatile.Read(ref _recording) != 0;
    public bool IsCircuitBreakerTripped => Volatile.Read(ref _circuitBreaker) != 0;

    public void BeginRecording(bool clearPendingControlEvents = true)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        while (clearPendingControlEvents && _channel.Reader.TryRead(out _))
        {
            Interlocked.Decrement(ref _queueDepth);
        }
        lock (_rawSync)
        {
            _rawDx = 0;
            _rawDy = 0;
            _rawCount = 0;
            _rawHasAbsolutePosition = false;
            _rawHasRelativeDelta = false;
        }
        Interlocked.Exchange(ref _accepted, 0);
        Interlocked.Exchange(ref _processed, 0);
        Interlocked.Exchange(ref _ignoredBeforeRecording, 0);
        Interlocked.Exchange(ref _droppedMoves, 0);
        Interlocked.Exchange(ref _rawReports, 0);
        Interlocked.Exchange(ref _aggregatedRawMoves, 0);
        Interlocked.Exchange(ref _rateWindowAccepted, 0);
        Interlocked.Exchange(ref _circuitBreaker, 0);
        Volatile.Write(ref _rateWindowStart, Environment.TickCount64);
        Volatile.Write(ref _recording, 1);
    }

    public long EndIngestion()
    {
        Volatile.Write(ref _recording, 0);
        FlushRawAggregation(forceAfterStop: true);
        return Interlocked.Read(ref _accepted);
    }

    public void AbortRecording()
    {
        Volatile.Write(ref _recording, 0);
        lock (_rawSync)
        {
            _rawDx = 0;
            _rawDy = 0;
            _rawCount = 0;
            _rawHasAbsolutePosition = false;
            _rawHasRelativeDelta = false;
        }
    }

    public bool WaitForDrain(long acceptedSequence, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (Interlocked.Read(ref _processed) < acceptedSequence)
        {
            if (stopwatch.Elapsed >= timeout || IsCircuitBreakerTripped)
            {
                return false;
            }
            Thread.Sleep(1);
        }
        return true;
    }

    public bool TryEnqueue(HookEvent input)
    {
        if (Volatile.Read(ref _disposed) != 0 || IsCircuitBreakerTripped)
        {
            return false;
        }

        var isControl = input.IsKeyboard && input.VirtualKey == _controlVirtualKey;
        if (!IsRecording)
        {
            if (input.IsMouseMove)
            {
                Interlocked.Increment(ref _ignoredBeforeRecording);
                return true;
            }
            return TryWrite(input, isControl, bypassRecordingCheck: true);
        }

        if (!isControl && input.Source == HookSource.RawMouse && input.IsMouseMove && !input.IsAbsoluteMouse)
        {
            Interlocked.Increment(ref _rawReports);
            lock (_rawSync)
            {
                _rawDx += input.DeltaX;
                _rawDy += input.DeltaY;
                _rawX = input.MouseX;
                _rawY = input.MouseY;
                _rawHasAbsolutePosition = input.HasAbsoluteMousePosition;
                _rawHasRelativeDelta |= input.HasRelativeMouseDelta;
                _rawCount++;
            }
            return true;
        }

        if (!isControl && input.Source == HookSource.RawMouse && !input.IsMouseMove)
        {
            // Preserve raw trajectory ordering: the final aggregated move must precede its button/wheel.
            FlushRawAggregation();
        }

        return TryWrite(input, isControl, bypassRecordingCheck: false);
    }

    public bool PumpOneForTest()
    {
        if (!_channel.Reader.TryRead(out var input))
        {
            return false;
        }
        Interlocked.Decrement(ref _queueDepth);
        Dispatch(input);
        return true;
    }

    public void DrainAllForTest()
    {
        FlushRawAggregation(forceAfterStop: true);
        while (PumpOneForTest())
        {
        }
    }

    public CaptureQueueStats GetStats()
    {
        var depth = QueueDepth;
        var usage = Math.Clamp((int)Math.Ceiling(depth * 100d / _capacity), 0, 100);
        return new CaptureQueueStats(
            _capacity,
            depth,
            usage,
            Interlocked.Read(ref _accepted),
            Interlocked.Read(ref _processed),
            Interlocked.Read(ref _ignoredBeforeRecording),
            Interlocked.Read(ref _droppedMoves),
            Interlocked.Read(ref _rawReports),
            Interlocked.Read(ref _aggregatedRawMoves),
            Interlocked.Read(ref _eventsPerSecond),
            usage >= 50,
            usage >= 80,
            IsCircuitBreakerTripped);
    }

    private bool TryWrite(HookEvent input, bool isControl, bool bypassRecordingCheck)
    {
        if (!bypassRecordingCheck && !isControl && !IsRecording)
        {
            Interlocked.Increment(ref _ignoredBeforeRecording);
            return true;
        }

        var depth = QueueDepth;
        var usage = depth * 100 / _capacity;
        if (!isControl && usage >= 95)
        {
            TripCircuitBreaker("CAPTURE_QUEUE_95_PERCENT");
            return false;
        }
        if (!isControl && usage >= 80 && input.IsMouseMove)
        {
            Interlocked.Increment(ref _droppedMoves);
            return true;
        }

        if (_channel.Writer.TryWrite(input))
        {
            Interlocked.Increment(ref _queueDepth);
            Interlocked.Increment(ref _accepted);
            Interlocked.Increment(ref _rateWindowAccepted);
            return true;
        }

        if (input.IsMouseMove && !isControl)
        {
            Interlocked.Increment(ref _droppedMoves);
            return true;
        }

        TripCircuitBreaker("CAPTURE_QUEUE_FULL_NON_DROPPABLE_EVENT");
        return false;
    }

    private void FlushRawAggregation(bool forceAfterStop = false)
    {
        HookEvent? aggregated = null;
        int reports;
        lock (_rawSync)
        {
            reports = _rawCount;
            if (reports > 0)
            {
                aggregated = new HookEvent
                {
                    Source = HookSource.RawMouse,
                    Message = 0x0200,
                    TimestampMs = Environment.TickCount64,
                    MouseX = _rawX,
                    MouseY = _rawY,
                    DeltaX = _rawDx,
                    DeltaY = _rawDy,
                    IsMouseMove = true,
                    IsAbsoluteMouse = false,
                    HasAbsoluteMousePosition = _rawHasAbsolutePosition,
                    HasRelativeMouseDelta = _rawHasRelativeDelta
                };
                _rawDx = 0;
                _rawDy = 0;
                _rawCount = 0;
                _rawHasAbsolutePosition = false;
                _rawHasRelativeDelta = false;
            }
        }

        if (aggregated is not null)
        {
            Interlocked.Add(ref _aggregatedRawMoves, reports);
            if (!TryWrite(aggregated, isControl: false, bypassRecordingCheck: forceAfterStop))
            {
                TripCircuitBreaker("RAW_AGGREGATE_ENQUEUE_FAILED");
            }
        }
    }

    private async Task ConsumeAsync()
    {
        try
        {
            await foreach (var input in _channel.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                Interlocked.Decrement(ref _queueDepth);
                Dispatch(input);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Dispatch(HookEvent input)
    {
        try
        {
            EventReady?.Invoke(input);
        }
        catch (Exception ex)
        {
            TripCircuitBreaker($"CAPTURE_CONSUMER_EXCEPTION:{ex.GetType().Name}");
        }
        finally
        {
            Interlocked.Increment(ref _processed);
        }
    }

    private void TripCircuitBreaker(string reason)
    {
        if (Interlocked.Exchange(ref _circuitBreaker, 1) != 0)
        {
            return;
        }
        Volatile.Write(ref _recording, 0);
        ThreadPool.UnsafeQueueUserWorkItem(_ => CircuitBreakerTripped?.Invoke(reason), null);
    }

    private void PublishStats()
    {
        var now = Environment.TickCount64;
        var start = Volatile.Read(ref _rateWindowStart);
        var elapsed = Math.Max(1, now - start);
        if (elapsed >= 1000)
        {
            var accepted = Interlocked.Exchange(ref _rateWindowAccepted, 0);
            Interlocked.Exchange(ref _eventsPerSecond, accepted * 1000 / elapsed);
            Volatile.Write(ref _rateWindowStart, now);
        }
        try
        {
            StatsPublished?.Invoke(GetStats());
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
        Volatile.Write(ref _recording, 0);
        _rawFlushTimer.Dispose();
        _statsTimer.Dispose();
        _channel.Writer.TryComplete();
        _shutdown.Cancel();
        try
        {
            _consumerTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
        }
        _shutdown.Dispose();
        GC.SuppressFinalize(this);
    }
}

public sealed class CaptureModeController
{
    private readonly Func<bool> _registerRaw;
    private readonly Action _unregisterRaw;

    public CaptureModeController(Func<bool> registerRaw, Action unregisterRaw)
    {
        _registerRaw = registerRaw;
        _unregisterRaw = unregisterRaw;
    }

    public RecorderCaptureMode Mode { get; private set; } = RecorderCaptureMode.Standard;
    public int RegisterCalls { get; private set; }
    public int UnregisterCalls { get; private set; }

    public bool EnableRawEnhanced(bool explicitlyConfirmed)
    {
        if (!explicitlyConfirmed || Mode == RecorderCaptureMode.RawEnhanced)
        {
            return Mode == RecorderCaptureMode.RawEnhanced;
        }
        RegisterCalls++;
        if (!_registerRaw())
        {
            return false;
        }
        Mode = RecorderCaptureMode.RawEnhanced;
        return true;
    }

    public void DisableRawEnhanced()
    {
        if (Mode != RecorderCaptureMode.RawEnhanced)
        {
            return;
        }
        _unregisterRaw();
        UnregisterCalls++;
        Mode = RecorderCaptureMode.Standard;
    }
}

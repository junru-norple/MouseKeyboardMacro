using System.Diagnostics;

namespace MacroCore.Timing;

public sealed class LongPressDetector
{
    private readonly int _requiredMilliseconds;
    private readonly object _sync = new();
    private bool _pressed;
    private bool _triggered;
    private CancellationTokenSource? _pressCts;

    public event Action? Triggered;

    public bool IsPressed { get; private set; }
    public bool HasTriggeredThisPress => _triggered;
    public long PressedMilliseconds { get; private set; }

    public LongPressDetector(int requiredMilliseconds)
    {
        _requiredMilliseconds = requiredMilliseconds;
    }

    public void OnKeyDown()
    {
        lock (_sync)
        {
            if (_pressed)
            {
                return;
            }

            _pressed = true;
            IsPressed = true;
            _triggered = false;
            PressedMilliseconds = 0;
            _pressCts?.Dispose();
            _pressCts = new CancellationTokenSource();
            var token = _pressCts.Token;
            var started = Stopwatch.GetTimestamp();

            _ = Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        await Task.Delay(10, token).ConfigureAwait(false);
                        if (_pressed && !_triggered && _requiredMilliseconds <= 0)
                        {
                            break;
                        }
                        lock (_sync)
                        {
                            if (!_pressed || _triggered)
                            {
                                break;
                            }

                            PressedMilliseconds = (long)(Stopwatch.GetTimestamp() - started) * 1000 / Stopwatch.Frequency;
                            if (PressedMilliseconds >= _requiredMilliseconds)
                            {
                                _triggered = true;
                                Triggered?.Invoke();
                                break;
                            }
                        }
                    }
                }
                catch (TaskCanceledException)
                {
                }
            }, token);
        }
    }

    public bool OnKeyUp()
    {
        bool triggered;
        lock (_sync)
        {
            if (!_pressed)
            {
                return false;
            }

            _pressed = false;
            IsPressed = false;
            _pressCts?.Cancel();
            _pressCts?.Dispose();
            _pressCts = null;
            triggered = _triggered;
            if (!_triggered)
            {
                PressedMilliseconds = 0;
            }
        }

        return triggered;
    }
}

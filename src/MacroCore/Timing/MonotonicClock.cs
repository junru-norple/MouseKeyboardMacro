using System.Diagnostics;

namespace MacroCore.Timing;

public sealed class MonotonicClock
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    public long ElapsedMilliseconds => _stopwatch.ElapsedMilliseconds;
    public long GetElapsedMilliseconds() => _stopwatch.ElapsedMilliseconds;
    public void Reset() => _stopwatch.Restart();
    public void Restart() => _stopwatch.Restart();
}

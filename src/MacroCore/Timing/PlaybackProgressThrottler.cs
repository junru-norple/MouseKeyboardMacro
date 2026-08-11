namespace MacroCore.Timing;

public sealed class PlaybackProgressThrottler
{
    private readonly double _minimumIntervalMilliseconds;
    private double _lastPublished = double.NegativeInfinity;

    public PlaybackProgressThrottler(double maximumUpdatesPerSecond = 10)
    {
        if (maximumUpdatesPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumUpdatesPerSecond));
        }
        _minimumIntervalMilliseconds = 1000d / maximumUpdatesPerSecond;
    }

    public int PublishedCount { get; private set; }

    public bool ShouldPublish(double wallElapsedMilliseconds, bool force = false)
    {
        if (!force && PublishedCount > 0 && wallElapsedMilliseconds - _lastPublished < _minimumIntervalMilliseconds)
        {
            return false;
        }

        _lastPublished = wallElapsedMilliseconds;
        PublishedCount++;
        return true;
    }
}

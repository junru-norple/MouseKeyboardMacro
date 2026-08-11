using MacroCore.Models;
using MacroCore.Serialization;
using MacroCore.Timing;

namespace MacroCore.Tests;

public class TimingTests
{
    [Fact]
    public void LongPress_Detect_Only_Triggers_After_Threshold()
    {
        var detector = new LongPressDetector(120);
        bool triggered = false;
        detector.Triggered += () => triggered = true;

        detector.OnKeyDown();
        Assert.False(triggered);
        Thread.Sleep(100);
        Assert.False(detector.OnKeyUp());
        Assert.False(triggered);

        detector.OnKeyDown();
        Assert.True(SpinWait.SpinUntil(() => detector.HasTriggeredThisPress, TimeSpan.FromSeconds(1)));
        Assert.True(detector.HasTriggeredThisPress);
        Assert.True(detector.OnKeyUp());
    }

    [Fact]
    public void LongPress_Detect_Different_Thresholds_For_F11_F12_State_Machine()
    {
        var detector = new LongPressDetector(2000);
        bool triggered = false;
        detector.Triggered += () => triggered = true;

        detector.OnKeyDown();
        Thread.Sleep(120);
        Assert.False(triggered);

        detector.OnKeyUp();
        Assert.False(triggered);

        detector.OnKeyDown();
        Assert.True(SpinWait.SpinUntil(() => triggered, TimeSpan.FromSeconds(3)));
        Assert.True(triggered);
        Assert.True(detector.OnKeyUp());
    }
}

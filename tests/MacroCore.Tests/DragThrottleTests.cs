using MacroCore.Input;
using MacroCore.Models;
using MacroRecorder.Services;
using System.Reflection;
using MacroCore.Timing;

namespace MacroCore.Tests;

public class DragThrottleTests
{
    [Fact]
    public void Drag_Throttles_Short_Moves_and_Records_Start_And_End_In_Order()
    {
        using var service = new RecorderService();

        typeof(RecorderService).GetMethod("StartRecording", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(service, null);
        var pressedButtons = (HashSet<MouseButtonKind>)typeof(RecorderService).GetField("_pressedMouseButtons", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)!;
        var eventsField = typeof(RecorderService).GetField("_events", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var tryDrag = typeof(RecorderService).GetMethod("TryRecordDragMove", BindingFlags.NonPublic | BindingFlags.Instance)!;

        pressedButtons.Add(MouseButtonKind.Left);

        tryDrag.Invoke(service, [new HookEvent { Source = HookSource.Mouse, Message = 0x0200, MouseX = 10, MouseY = 10, MouseButton = MouseButtonKind.Left }]);
        var events = (List<MacroEventRecord>)eventsField.GetValue(service)!;
        Assert.Equal(1, events.Count);
        Assert.Equal(10, events[0].X);
        Assert.Equal(10, events[0].Y);

        Thread.Sleep(1);
        tryDrag.Invoke(service, [new HookEvent { Source = HookSource.Mouse, Message = 0x0200, MouseX = 11, MouseY = 10, MouseButton = MouseButtonKind.Left }]);
        Assert.Equal(1, events.Count);

        Thread.Sleep(20);
        tryDrag.Invoke(service, [new HookEvent { Source = HookSource.Mouse, Message = 0x0200, MouseX = 11, MouseY = 12, MouseButton = MouseButtonKind.Left }]);
        Assert.Equal(2, events.Count);
        Assert.Equal(11, events[1].X);
        Assert.Equal(12, events[1].Y);
        Assert.True(events[0].TimeMs <= events[1].TimeMs);
    }
}

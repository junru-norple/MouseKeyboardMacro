using MacroCore.Display;
using MacroCore.Input;
using MacroCore.Models;

namespace MacroCore.Tests;

public class LayoutTests
{
    [Fact]
    public void Layout_Equivalence_Check()
    {
        var a = new MacroDisplayLayout
        {
            ScreenCount = 2,
            VirtualBounds = new MacroRect { X = -1920, Y = -40, Width = 2880, Height = 1200 },
            PrimaryScreenBounds = new MacroRect { X = 0, Y = -40, Width = 960, Height = 1200 },
            Screens =
            {
                new MacroScreenInfo { DeviceName = "\\\\.\\DISPLAY1", IsPrimary = true, Bounds = new MacroRect { X = 0, Y = 0, Width = 1, Height = 1 } },
                new MacroScreenInfo { DeviceName = "\\\\.\\DISPLAY2", IsPrimary = false, Bounds = new MacroRect { X = -1920, Y = 0, Width = 1, Height = 1 } },
            }
        };

        var b = new MacroDisplayLayout
        {
            ScreenCount = 2,
            VirtualBounds = new MacroRect { X = -1920, Y = -40, Width = 2880, Height = 1200 },
            PrimaryScreenBounds = new MacroRect { X = 0, Y = -40, Width = 960, Height = 1200 },
            Screens =
            {
                new MacroScreenInfo { DeviceName = "\\\\.\\DISPLAY2", IsPrimary = false, Bounds = new MacroRect { X = -1920, Y = 0, Width = 1, Height = 1 } },
                new MacroScreenInfo { DeviceName = "\\\\.\\DISPLAY1", IsPrimary = true, Bounds = new MacroRect { X = 0, Y = 0, Width = 1, Height = 1 } },
            }
        };

        Assert.True(a.IsEquivalentTo(b));
    }

    [Fact]
    public void Normalize_Virtual_Coordinates_For_Negative_Screen()
    {
        var layout = new MacroDisplayLayout
        {
            VirtualBounds = new MacroRect { X = -1920, Y = -200, Width = 2880, Height = 1200 }
        };

        var left = SendInputService.NormalizeToAbsoluteX(-1920, layout);
        var leftUnder = SendInputService.NormalizeToAbsoluteX(-3000, layout);
        var right = SendInputService.NormalizeToAbsoluteX(959, layout);
        var center = SendInputService.NormalizeToAbsoluteX(0, layout);
        var rightOver = SendInputService.NormalizeToAbsoluteX(2000, layout);
        var top = SendInputService.NormalizeToAbsoluteY(-200, layout);
        var bottom = SendInputService.NormalizeToAbsoluteY(999, layout);
        var bottomOver = SendInputService.NormalizeToAbsoluteY(1500, layout);

        Assert.Equal(0, left);
        Assert.Equal(0, leftUnder);
        Assert.Equal(65535, rightOver);
        Assert.True(right >= 64000 && right <= 65535);
        Assert.True(center > 40000 && center < 65535);
        Assert.Equal(0, top);
        Assert.True(bottom >= 64000 && bottom <= 65535);
        Assert.Equal(65535, bottomOver);
    }
}

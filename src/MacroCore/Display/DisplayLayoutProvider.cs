using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using MacroCore.Models;

namespace MacroCore.Display;

public static class DisplayLayoutProvider
{
    public static MacroDisplayLayout GetCurrentLayout()
    {
        var screens = Screen.AllScreens.OrderBy(s => s.DeviceName, StringComparer.OrdinalIgnoreCase).ToList();

        var layout = new MacroDisplayLayout
        {
            VirtualBounds = new MacroRect
            {
                X = GetSystemMetrics((int)SystemMetric.SM_XVIRTUALSCREEN),
                Y = GetSystemMetrics((int)SystemMetric.SM_YVIRTUALSCREEN),
                Width = GetSystemMetrics((int)SystemMetric.SM_CXVIRTUALSCREEN),
                Height = GetSystemMetrics((int)SystemMetric.SM_CYVIRTUALSCREEN)
            },
            PrimaryScreenBounds = new MacroRect
            {
                X = Screen.PrimaryScreen?.Bounds.X ?? 0,
                Y = Screen.PrimaryScreen?.Bounds.Y ?? 0,
                Width = Screen.PrimaryScreen?.Bounds.Width ?? 0,
                Height = Screen.PrimaryScreen?.Bounds.Height ?? 0
            },
            ScreenCount = screens.Count
        };

        foreach (var screen in screens)
        {
            var monitor = MonitorFromPoint(
                new NativePoint(screen.Bounds.X, screen.Bounds.Y),
                (uint)MonitorFromPointFlags.MONITOR_DEFAULTTONEAREST);
            var dpiX = 96u;
            var dpiY = 96u;
            try
            {
                if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out dpiX, out dpiY) != 0)
                {
                    dpiX = 96u;
                    dpiY = 96u;
                }
            }
            catch
            {
                dpiX = 96u;
                dpiY = 96u;
            }

            var info = new MacroScreenInfo
            {
                DeviceName = screen.DeviceName,
                IsPrimary = screen.Primary,
                DpiX = (int)dpiX,
                DpiY = (int)dpiY,
                Bounds = new MacroRect
                {
                    X = screen.Bounds.X,
                    Y = screen.Bounds.Y,
                    Width = screen.Bounds.Width,
                    Height = screen.Bounds.Height
                }
            };
            layout.Screens.Add(info);
        }

        return layout;
    }

    public static bool CompareWithCurrent(MacroDisplayLayout recordedLayout)
    {
        return GetCurrentLayout().IsEquivalentTo(recordedLayout);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr MonitorFromPoint(NativePoint pt, uint dwFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int MDT_EFFECTIVE_DPI = 0;

    private enum SystemMetric
    {
        SM_XVIRTUALSCREEN = 76,
        SM_YVIRTUALSCREEN = 77,
        SM_CXVIRTUALSCREEN = 78,
        SM_CYVIRTUALSCREEN = 79
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint
    {
        public readonly int X;
        public readonly int Y;

        public NativePoint(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    private enum MonitorFromPointFlags : uint
    {
        MONITOR_DEFAULTTOPRIMARY = 1,
        MONITOR_DEFAULTTONEAREST = 2
    }
}

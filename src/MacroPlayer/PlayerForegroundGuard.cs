using System.Runtime.InteropServices;

namespace MacroPlayer;

internal static class PlayerForegroundGuard
{
    public static bool IsPlayerForeground(IntPtr playerWindowHandle)
    {
        return playerWindowHandle != IntPtr.Zero && GetForegroundWindow() == playerWindowHandle;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetForegroundWindow();
}

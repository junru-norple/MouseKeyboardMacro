namespace MacroCore.Input;

public sealed class StandardInputRejectedException : Exception
{
    public int Win32Error { get; }

    public StandardInputRejectedException(int win32Error)
        : base($"標準 Windows SendInput 被拒絕（Win32 error={win32Error}）。此遊戲可能不接受使用者層輸入；本工具不會使用驅動、注入或防作弊繞過。")
    {
        Win32Error = win32Error;
    }
}

using MacroCore.Security;

namespace MacroRecorder;

public sealed record RecorderPrivilegeDisplayModel(
    bool Elevated,
    WindowsIntegrityLevel Integrity,
    string WindowTitle,
    string HeaderTitle,
    string Description,
    Color Background,
    Color Foreground)
{
    public static RecorderPrivilegeDisplayModel ForProbe(bool elevated) => elevated
        ? new RecorderPrivilegeDisplayModel(
            true,
            WindowsIntegrityLevel.High,
            "滑鼠鍵盤軌跡錄製器 - 管理員錄製模式",
            "Recorder：管理員權限  |  Integrity：High",
            "管理員錄製模式：本模式可操作高權限應用程式。請只錄製可信任且必要的流程，完成後請關閉 Recorder。",
            Color.FromArgb(255, 239, 214),
            Color.DarkOrange)
        : new RecorderPrivilegeDisplayModel(
            false,
            WindowsIntegrityLevel.Medium,
            "滑鼠鍵盤軌跡錄製器 - 一般桌面錄製模式",
            "Recorder：一般權限  |  Integrity：Medium",
            "一般桌面錄製模式：可錄製一般權限應用程式。若目標程式以管理員身分執行，請改用管理員錄製入口。",
            Color.FromArgb(231, 242, 247),
            Color.DarkSlateBlue);
}

public static class RecorderPrivilegeUi
{
    public static RecorderPrivilegeDisplayModel Create(
        MacroToolLaunchOptions options,
        IWindowsPrivilegeService privilegeService)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(privilegeService);
        var integrity = privilegeService.GetCurrentIntegrity();
        return RecorderPrivilegeDisplayModel.ForProbe(integrity >= WindowsIntegrityLevel.High);
    }
}

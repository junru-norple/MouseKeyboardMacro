using Microsoft.Win32;

namespace MacroLauncher;

public interface IInstallRootStore
{
    string? Read();
    void Write(string projectRoot);
}

public sealed class RegistryInstallRootStore : IInstallRootStore
{
    public const string KeyPath = @"Software\MouseKeyboardMacro";
    public const string ValueName = "InstallRoot";

    public string? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        return key?.GetValue(ValueName) as string;
    }

    public void Write(string projectRoot)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true)
            ?? throw new InvalidOperationException("Unable to open the HKCU install-root registry key.");
        key.SetValue(ValueName, Path.GetFullPath(projectRoot), RegistryValueKind.String);
    }
}

public static class InstallRootLocator
{
    public static bool IsValid(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        try
        {
            var full = Path.GetFullPath(root);
            return File.Exists(Path.Combine(full, "Program", "project-root.marker")) &&
                   File.Exists(Path.Combine(full, "Program", "App", "Launcher", "MacroLauncher.exe"));
        }
        catch
        {
            return false;
        }
    }

    public static bool TryRegister(string root, IInstallRootStore store, out string error)
    {
        error = string.Empty;
        if (!IsValid(root))
        {
            error = "InstallRoot is invalid: " + root;
            return false;
        }

        try
        {
            store.Write(Path.GetFullPath(root));
            return true;
        }
        catch (Exception exception)
        {
            error = "Unable to update HKCU InstallRoot: " + exception.Message;
            return false;
        }
    }

    public static bool TryResolve(string commandDirectory, IInstallRootStore store, out string root, out string error)
    {
        root = string.Empty;
        error = string.Empty;
        if (IsValid(commandDirectory))
        {
            root = Path.GetFullPath(commandDirectory);
            return TryRegister(root, store, out error);
        }

        var installed = store.Read();
        if (IsValid(installed))
        {
            root = Path.GetFullPath(installed!);
            return true;
        }

        error = "InstallRoot is missing or invalid. Run one of the five CMD files once from the original project root.";
        return false;
    }
}

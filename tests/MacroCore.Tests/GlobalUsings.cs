global using Xunit;

using System.Runtime.CompilerServices;
using MacroCore.Runtime;
using MacroPlayer;

internal static class TestProjectEnvironment
{
    public static string Root { get; } = FindRoot();
    public static bool IsSourceOnly { get; } = !Directory.Exists(Path.Combine(Root, "Development"));
    public static string DevelopmentRoot => IsSourceOnly ? Root : Path.Combine(Root, "Development");
    public static string RecordingsRoot => Path.Combine(Root, "Recordings");
    public static ProjectSandboxPaths SandboxPaths { get; } = ProjectSandboxPaths.Create(
        Root,
        $"unit-tests-{Environment.ProcessId}-{Guid.NewGuid():N}");
    public static string RuntimeRoot { get; } = ResolveRuntimeRoot();
    public static string CasesRoot { get; } = Path.Combine(RuntimeRoot, "Cases");
    public static string SyntheticRawFixture => MacroCore.Tests.SyntheticMacroFixtureFactory.GetPath("SyntheticRawDual.macro");

    [ModuleInitializer]
    internal static void Initialize()
    {
        if (string.Equals(Path.GetFullPath(RuntimeRoot), Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Unit-test runtime root must not be the repository root.");
        }

        foreach (string path in new[] { RuntimeRoot, CasesRoot })
        {
            ProjectSandboxGuard.EnsureAllowed(SandboxPaths, path);
            Directory.CreateDirectory(path);
        }

        RootMarker.Ensure(RuntimeRoot);
        try
        {
            AppPaths.Initialize(RuntimeRoot);
        }
        catch (InvalidOperationException exception)
        {
            string activeRoot;
            try { activeRoot = AppPaths.Current.ProjectRoot; }
            catch { activeRoot = "UNRESOLVED"; }
            throw new InvalidOperationException(
                $"Testhost runtime isolation was not established before MacroCore loaded. Expected={RuntimeRoot}; Active={activeRoot}",
                exception);
        }
        PlayerRuntimePaths.Initialize(RuntimeRoot);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { ProjectSandboxGuard.DeleteTree(SandboxPaths, SandboxPaths.RunRoot); } catch { }
        };
    }

    public static void ResetPlayerRuntimePaths() => PlayerRuntimePaths.Initialize(RuntimeRoot);

    private static string ResolveRuntimeRoot()
    {
        string? configured = Environment.GetEnvironmentVariable("MKM_PROJECT_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            string full = Path.GetFullPath(configured);
            if (!string.Equals(full, Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase) &&
                ProjectSandboxGuard.IsWithin(SandboxPaths.TestSandboxRoot, full))
            {
                return full;
            }
        }
        return Path.Combine(SandboxPaths.SimulatedDesktop, "RuntimeRoot");
    }

    public static string SourcePath(params string[] parts) =>
        Path.Combine(new[] { DevelopmentRoot }.Concat(parts).ToArray());

    public static string RootCommandPath(string name) => IsSourceOnly
        ? Path.Combine(Root, "scripts", "portable-launchers", name)
        : Path.Combine(Root, name);

    private static string FindRoot()
    {
        for (DirectoryInfo? cursor = new(AppContext.BaseDirectory); cursor is not null; cursor = cursor.Parent)
        {
            if (File.Exists(Path.Combine(cursor.FullName, "Development", "MouseKeyboardMacro.sln")))
            {
                return cursor.FullName;
            }

            if (File.Exists(Path.Combine(cursor.FullName, "MouseKeyboardMacro.sln")) &&
                Directory.Exists(Path.Combine(cursor.FullName, "src")) &&
                Directory.Exists(Path.Combine(cursor.FullName, "tests")))
            {
                if (cursor.Name.Equals("Development", StringComparison.OrdinalIgnoreCase) &&
                    cursor.Parent is not null &&
                    Directory.Exists(Path.Combine(cursor.Parent.FullName, "Program")) &&
                    Directory.Exists(Path.Combine(cursor.Parent.FullName, "Recordings")))
                {
                    return cursor.Parent.FullName;
                }
                return cursor.FullName;
            }
        }

        throw new DirectoryNotFoundException("Test project root not found.");
    }
}

internal static class ProjectLocalTestSandbox
{
    private static int _counter;

    public static string Create()
    {
        string path = Path.Combine(TestProjectEnvironment.CasesRoot, Interlocked.Increment(ref _counter).ToString("D5"));
        ProjectSandboxGuard.EnsureAllowed(TestProjectEnvironment.SandboxPaths, path);
        Directory.CreateDirectory(path);
        return path;
    }
}

internal static class TestInstallLayout
{
    public static string CreateValidPortableInstallLayout(string parent, string name)
    {
        string root = Path.Combine(parent, name);
        ProjectSandboxGuard.EnsureAllowed(TestProjectEnvironment.SandboxPaths, root);
        RootMarker.Ensure(root);
        string launcherDirectory = Path.Combine(root, "Program", "App", "Launcher");
        ProjectSandboxGuard.EnsureAllowed(TestProjectEnvironment.SandboxPaths, launcherDirectory);
        Directory.CreateDirectory(launcherDirectory);
        File.WriteAllBytes(Path.Combine(launcherDirectory, "MacroLauncher.exe"), []);
        return root;
    }
}

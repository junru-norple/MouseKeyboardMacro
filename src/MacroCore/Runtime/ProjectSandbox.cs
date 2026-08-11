using System.Collections.ObjectModel;

namespace MacroCore.Runtime;

public sealed record ProjectSandboxPaths
{
    private ProjectSandboxPaths(string projectRoot, string developmentRoot, string runId)
    {
        ProjectRoot = projectRoot;
        DevelopmentRoot = developmentRoot;
        RunId = runId;
    }

    public string ProjectRoot { get; }
    public string DevelopmentRoot { get; }
    public string RunId { get; }
    public string TestSandboxRoot => Path.Combine(DevelopmentRoot, "TestSandbox");
    public string RunRoot => Path.Combine(TestSandboxRoot, RunId);
    public string Temp => Path.Combine(RunRoot, "Temp");
    public string SimulatedDesktop => Path.Combine(RunRoot, "SimulatedDesktop");
    public string BuildProfileRoot => Path.Combine(DevelopmentRoot, ".build-profile");
    public string RunProfileRoot => Path.Combine(BuildProfileRoot, RunId);
    public string UserProfile => Path.Combine(RunProfileRoot, "UserProfile");
    public string AppData => Path.Combine(RunProfileRoot, "AppData", "Roaming");
    public string LocalAppData => Path.Combine(RunProfileRoot, "AppData", "Local");
    public string DotNetCliHome => Path.Combine(RunProfileRoot, "dotnet-cli");
    public string NuGetPackages => Path.Combine(DevelopmentRoot, ".nuget-packages");
    public string NuGetHttpCache => Path.Combine(DevelopmentRoot, ".nuget-http-cache");
    public string TestResultsRoot => Path.Combine(DevelopmentRoot, "TestResults");
    public string TestResults => Path.Combine(TestResultsRoot, RunId);
    public string ProgramState => Path.Combine(ProjectRoot, "Program", "State");
    public string GitHubExport => Path.Combine(ProjectRoot, "GitHub_上傳版本");

    public IReadOnlyList<string> AllowedWriteRoots => new ReadOnlyCollection<string>(
    [
        TestSandboxRoot,
        BuildProfileRoot,
        NuGetPackages,
        NuGetHttpCache,
        TestResultsRoot,
        ProgramState,
        GitHubExport
    ]);

    public IReadOnlyDictionary<string, string> ChildEnvironment => new ReadOnlyDictionary<string, string>(
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["TEMP"] = Temp,
            ["TMP"] = Temp,
            ["TMPDIR"] = Temp,
            ["USERPROFILE"] = UserProfile,
            ["HOME"] = UserProfile,
            ["APPDATA"] = AppData,
            ["LOCALAPPDATA"] = LocalAppData,
            ["DOTNET_CLI_HOME"] = DotNetCliHome,
            ["NUGET_CLI_HOME"] = DotNetCliHome,
            ["NUGET_PACKAGES"] = NuGetPackages,
            ["NUGET_HTTP_CACHE_PATH"] = NuGetHttpCache,
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
            ["DOTNET_NOLOGO"] = "1",
            ["NUGET_XMLDOC_MODE"] = "skip",
            ["MSBUILDDISABLENODEREUSE"] = "1",
            ["MKM_PROJECT_ROOT"] = ProjectRoot,
            ["MKM_SANDBOX_RUN_ID"] = RunId
        });

    public static ProjectSandboxPaths Create(string projectRoot, string? runId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        string root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (ProjectSandboxGuard.IsUnc(root))
        {
            throw new InvalidOperationException("UNC project roots are not permitted for the local safety sandbox.");
        }

        string development = File.Exists(Path.Combine(root, "Development", "MouseKeyboardMacro.sln"))
            ? Path.Combine(root, "Development")
            : root;
        string id = NormalizeRunId(runId ?? (DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")));
        return new ProjectSandboxPaths(root, Path.GetFullPath(development), id);
    }

    public void CreateDirectories()
    {
        foreach (string path in new[] { Temp, SimulatedDesktop, UserProfile, AppData, LocalAppData, DotNetCliHome, NuGetPackages, NuGetHttpCache, TestResults, ProgramState })
        {
            ProjectSandboxGuard.EnsureAllowed(this, path);
            Directory.CreateDirectory(path);
        }
    }

    public static string NormalizeRunId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 96 || value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')) || value is "." or "..")
        {
            throw new ArgumentException("Run id may contain only ASCII letters, digits, dot, underscore, and hyphen.", nameof(value));
        }
        return value;
    }
}

public static class ProjectSandboxGuard
{
    public static bool IsUnc(string path) => path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);

    public static bool ContainsParentTraversal(string path) => path
        .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
        .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
        .Any(segment => segment == "..");

    public static bool IsWithin(string root, string candidate)
    {
        string normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return normalizedCandidate.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
               normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static string EnsureAllowed(ProjectSandboxPaths paths, string candidate)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);
        if (IsUnc(candidate) || ContainsParentTraversal(candidate))
        {
            throw new InvalidOperationException("UNC and parent traversal paths are not permitted.");
        }

        string full = Path.GetFullPath(candidate);
        if (!IsWithin(paths.ProjectRoot, full) || !paths.AllowedWriteRoots.Any(root => IsWithin(root, full)))
        {
            throw new InvalidOperationException("Path is outside the project-local write allowlist: " + full);
        }

        RejectReparseTraversal(paths.ProjectRoot, full);
        return full;
    }

    public static void DeleteTree(ProjectSandboxPaths paths, string candidate, bool allowAllowlistRoot = false)
    {
        string full = EnsureAllowed(paths, candidate);
        if (!allowAllowlistRoot && paths.AllowedWriteRoots.Any(root => Path.GetFullPath(root).TrimEnd('\\').Equals(full.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Refusing to delete an allowlist root.");
        }
        if (Directory.Exists(full))
        {
            Directory.Delete(full, recursive: true);
        }
    }

    private static void RejectReparseTraversal(string projectRoot, string candidate)
    {
        for (DirectoryInfo? cursor = NearestExistingDirectory(candidate); cursor is not null && IsWithin(projectRoot, cursor.FullName); cursor = cursor.Parent)
        {
            if ((cursor.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("Reparse points are not permitted in sandbox write paths: " + cursor.FullName);
            }
            if (cursor.FullName.TrimEnd('\\').Equals(Path.GetFullPath(projectRoot).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }
    }

    private static DirectoryInfo? NearestExistingDirectory(string path)
    {
        string? cursor = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        while (!string.IsNullOrWhiteSpace(cursor) && !Directory.Exists(cursor))
        {
            cursor = Path.GetDirectoryName(cursor);
        }
        return string.IsNullOrWhiteSpace(cursor) ? null : new DirectoryInfo(cursor);
    }
}

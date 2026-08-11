using System.Text.RegularExpressions;

namespace MacroPlayer;

internal static class PublicationPackageContract
{
    public const string DefaultVersion = "1.0.0";
    public const string CanonicalManualName = "README_操作手冊.txt";
    public const string RuntimeManualRelativePath = "Program/Docs/README_操作手冊.txt";

    public static IReadOnlyList<string> RequiredReadmeSections { get; } =
    [
        "Project purpose", "Windows 11 x64", "Version 1.0.0", "Installation", "Daily launchers",
        "Recording", "Playback", "Input modes", "KeepVisible", "Emergency stop", "Security",
        "Privacy", "Build from source", "Known limitations", "License",
        "Sanitized interface previews"
    ];

    public static IReadOnlyList<string> RequiredRepositoryEntries { get; } =
    [
        "README.md", "INSTALL.md", "USER_GUIDE.md", "BUILDING.md", "TROUBLESHOOTING.md",
        "SECURITY.md", "PRIVACY.md", "CHANGELOG.md", "CONTRIBUTING.md", "LICENSE", "LICENSE_STATUS.md",
        "SOURCE_BASELINE.md", "THIRD_PARTY_NOTICES.md", "START_HERE.txt", CanonicalManualName,
        "REPOSITORY_MANIFEST_SHA256.txt", "REPOSITORY_MANIFEST_SHA256.sha256",
        "global.json", "Directory.Build.props", "MouseKeyboardMacro.sln", "NuGet.Config",
        ".gitignore", ".gitattributes", ".editorconfig",
        "src/MacroCore", "src/MacroLauncher", "src/MacroRecorder", "src/MacroPlayer",
        "src/MacroSafetyWatchdog", "tests/MacroCore.Tests",
        "tools/EmergencySessionTestHost", "tools/MacroPlaybackPerformanceProbe",
        "tools/PlayerPresentationTestHost", "scripts",
        "scripts/Build.ps1", "scripts/Clean.ps1", "scripts/Publish.ps1",
        "scripts/Publish-Release.ps1", "scripts/Test.ps1", "scripts/Verify-Release.ps1",
        "scripts/portable-launchers/06_啟動錄製器_一般模式.cmd",
        "scripts/portable-launchers/06A_啟動錄製器_管理員模式.cmd",
        "scripts/portable-launchers/07_選擇並重播巨集_一般模式.cmd",
        "scripts/portable-launchers/07A_選擇並重播巨集_管理員模式.cmd",
        "scripts/portable-launchers/99_緊急終止巨集工具.cmd",
        "docs/ARCHITECTURE.md", "docs/INPUT_MODES.md", "docs/SECURITY_MODEL.md", "docs/FAQ.md",
        "docs/images/recorder-standard.png", "docs/images/recorder-raw.png",
        "docs/images/player-main.png", "docs/images/player-mouse-modes.png",
        "docs/images/player-keep-visible.png",
        ".github/workflows/windows-ci.yml", ".github/ISSUE_TEMPLATE/bug_report.yml",
        ".github/ISSUE_TEMPLATE/feature_request.yml", ".github/ISSUE_TEMPLATE/config.yml",
        ".github/pull_request_template.md"
    ];

    public static IReadOnlyList<string> RequiredReleaseEntries { get; } =
    [
        "06_啟動錄製器_一般模式.cmd",
        "06A_啟動錄製器_管理員模式.cmd",
        "07_選擇並重播巨集_一般模式.cmd",
        "07A_選擇並重播巨集_管理員模式.cmd",
        "99_緊急終止巨集工具.cmd",
        "START_HERE.txt", CanonicalManualName, "README.md", "INSTALL.md", "USER_GUIDE.md", "BUILDING.md",
        "TROUBLESHOOTING.md", "SECURITY.md", "PRIVACY.md", "CHANGELOG.md",
        "CONTRIBUTING.md", "LICENSE", "LICENSE_STATUS.md", "LICENSE_STATUS.txt", "THIRD_PARTY_NOTICES.txt",
        "DOTNET_LICENSE.txt", "DOTNET_THIRD_PARTY_NOTICES.txt", "BINARY_SHA256SUMS.txt",
        "RELEASE_BINARY_MANIFEST.sha256", "BUILD_INFO.txt", "PORTABLE_RELEASE.txt",
        "docs/FAQ.md", "docs/INPUT_MODES.md", "docs/SECURITY_MODEL.md",
        "docs/images/recorder-standard.png", "docs/images/recorder-raw.png",
        "docs/images/player-main.png", "docs/images/player-mouse-modes.png",
        "docs/images/player-keep-visible.png",
        "Program/App", "Program/project-root.marker", RuntimeManualRelativePath, "Program/State/Logs",
        "Program/State/Settings", "Recordings"
    ];

    public static bool IsForbiddenRepositoryPath(string relativePath)
    {
        if (HasUnsafePathSyntax(relativePath)) return true;
        string path = Normalize(relativePath);
        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment =>
                segment.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("artifacts", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("temp", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("ManualOnly", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("MacroMigration", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("TestResults", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("TestSandbox", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals(".dotnet-cli-home", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals(".nuget-packages", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals(".nuget-http-cache", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals(".toolcache", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals(".repro", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (path.Contains("/NuGet/Migrations/", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/NuGet/Migrations/1", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("GitHubPackageContract.cs", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("ReproduciblePublicationGateTests.cs", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string[] rootDuplicates = ["ARCHITECTURE.md", "FAQ.md", "INPUT_MODES.md", "SECURITY_MODEL.md"];
        return rootDuplicates.Contains(path, StringComparer.OrdinalIgnoreCase) ||
               path.StartsWith("Program/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("Recordings/", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".macro", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".trx", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsForbiddenReleasePath(string relativePath)
    {
        if (HasUnsafePathSyntax(relativePath)) return true;
        string path = Normalize(relativePath);
        return path.StartsWith("Development/", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("ManualOnly", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/.git/", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".macro", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith("player-settings.json", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith("recorder-settings.json", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith("current_session.json", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith("active_tool.json", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".trx", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsCmdCrLfAsciiNoBom(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return false;
        for (int index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] > 0x7F ||
                bytes[index] == (byte)'\n' && (index == 0 || bytes[index - 1] != (byte)'\r') ||
                bytes[index] == (byte)'\r' && (index + 1 >= bytes.Length || bytes[index + 1] != (byte)'\n'))
            {
                return false;
            }
        }
        return true;
    }

    public static bool IsLfTextWithoutBom(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return false;
        return !bytes.Contains((byte)'\r');
    }

    public static IReadOnlyList<string> FindLocalIdentityLeaks(string text)
    {
        List<string> findings = [];
        string codexPrivatePathToken = string.Concat(".", "codex");
        string ownerOnlyToken = string.Concat("Owner", "Only");
        string privateEvidencePattern = @"\bMKM_" + @"v1_[A-Za-z0-9_-]*_test\b";
        string githubTokenPattern = @"\b(?:gh[pousr]_|github_" + @"pat_)[A-Za-z0-9_]+";
        string openAiTokenPattern = @"\bsk" + @"-(?:proj-)?[A-Za-z0-9_-]+";
        if (Regex.IsMatch(text, @"[A-Za-z]:\\Users\\", RegexOptions.IgnoreCase))
            findings.Add("WINDOWS_USER_PROFILE_PATH");
        if (Regex.IsMatch(text, @"(?<![A-Za-z0-9_])[A-Za-z]:[\\/]", RegexOptions.IgnoreCase))
            findings.Add("WINDOWS_ABSOLUTE_PATH");
        if (Regex.IsMatch(text, @"(?<![A-Za-z0-9_])/Users(?:/|\b)", RegexOptions.IgnoreCase))
            findings.Add("MACOS_USER_PROFILE_PATH");
        if (Regex.IsMatch(text, @"(?<![A-Za-z0-9_])/home(?:/|\b)", RegexOptions.IgnoreCase))
            findings.Add("LINUX_USER_PROFILE_PATH");
        if (Regex.IsMatch(text, @"[A-Za-z]:\\[^\r\n]*(?:\(codex|TestSandbox|\.toolcache)", RegexOptions.IgnoreCase))
            findings.Add("LOCAL_PROJECT_PATH");
        if (text.Contains(codexPrivatePathToken, StringComparison.OrdinalIgnoreCase))
            findings.Add("CODEX_PRIVATE_PATH");
        if (text.Contains(ownerOnlyToken, StringComparison.OrdinalIgnoreCase))
            findings.Add("OWNER_ONLY_PATH");
        if (Regex.IsMatch(text, privateEvidencePattern, RegexOptions.IgnoreCase))
            findings.Add("PRIVATE_TEST_EVIDENCE_PATH");
        if (Regex.IsMatch(text, @"\b[0-9a-f]{32}\b", RegexOptions.IgnoreCase))
            findings.Add("SESSION_TOKEN_LIKE_VALUE");
        if (Regex.IsMatch(text, githubTokenPattern, RegexOptions.IgnoreCase))
            findings.Add("GITHUB_TOKEN_LIKE_VALUE");
        if (Regex.IsMatch(text, openAiTokenPattern, RegexOptions.IgnoreCase))
            findings.Add("OPENAI_TOKEN_LIKE_VALUE");
        if (Regex.IsMatch(text, @"\bAKIA[A-Z0-9]{16}\b", RegexOptions.IgnoreCase))
            findings.Add("AWS_ACCESS_KEY_LIKE_VALUE");
        if (Regex.IsMatch(text, @"\bAIza[0-9A-Za-z_-]{20,}\b", RegexOptions.IgnoreCase))
            findings.Add("GOOGLE_API_KEY_LIKE_VALUE");
        if (Regex.IsMatch(text, @"\bxox[baprs]-[0-9A-Za-z-]{10,}\b", RegexOptions.IgnoreCase))
            findings.Add("SLACK_TOKEN_LIKE_VALUE");
        if (Regex.IsMatch(text, @"\b(?:api[_-]?key|client[_-]?secret|access[_-]?token|password)\b\s*[:=]\s*\S{8,}", RegexOptions.IgnoreCase))
            findings.Add("SECRET_ASSIGNMENT_LIKE_VALUE");
        if (Regex.IsMatch(text, @"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----", RegexOptions.IgnoreCase))
            findings.Add("PRIVATE_KEY");
        return findings;
    }

    public static bool HasRequiredReadmeSections(string markdown) =>
        RequiredReadmeSections.All(section => markdown.Contains(section, StringComparison.Ordinal));

    private static bool HasUnsafePathSyntax(string path)
    {
        string normalized = path.Replace('\\', '/');
        return string.IsNullOrWhiteSpace(normalized) ||
               normalized.StartsWith("/", StringComparison.Ordinal) ||
               Regex.IsMatch(normalized, @"^[A-Za-z]:") ||
               normalized.Contains(':') ||
               normalized.Split('/', StringSplitOptions.None).Any(segment => segment is "." or "..");
    }

    public static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');
}

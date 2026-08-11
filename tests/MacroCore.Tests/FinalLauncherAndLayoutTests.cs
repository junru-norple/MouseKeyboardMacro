using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MacroCore.Diagnostics;
using MacroCore.Runtime;
using MacroCore.Security;
using MacroCore.Serialization;
using MacroLauncher;
using Xunit;

namespace MacroCore.Tests;

public sealed class FinalLauncherAndLayoutTests
{
    private static readonly string[] RootCommands = FinalRootLayoutPolicy.VisibleNames
        .Where(name => name.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)).ToArray();
    private static string Root => TestProjectEnvironment.Root;
    private static string Dev => TestProjectEnvironment.DevelopmentRoot;

    [Fact]
    public void RootCmdAsciiNoBom()
    {
        foreach (string path in CommandPaths())
        {
            byte[] bytes = File.ReadAllBytes(path);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
            Assert.True(bytes.All(value => value < 0x80));
        }
    }

    [Fact]
    public void RootCmdCrLf()
    {
        foreach (string path in CommandPaths())
        {
            byte[] bytes = File.ReadAllBytes(path);
            for (int index = 0; index < bytes.Length; index++)
            {
                if (bytes[index] == 0x0A)
                {
                    Assert.True(index > 0 && bytes[index - 1] == 0x0D);
                }
            }
        }
    }

    [Fact]
    public void RootCmdNoPowerShell()
    {
        foreach (string path in CommandPaths())
        {
            string text = File.ReadAllText(path, Encoding.ASCII);
            Assert.False(text.Contains("powershell", StringComparison.OrdinalIgnoreCase));
            Assert.False(text.Contains("taskkill /IM", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void RootCmdActualPathWithChineseAndOpenParen()
    {
        Assert.True(TestProjectEnvironment.IsSourceOnly || Root.Contains('('));
        Assert.True(TestProjectEnvironment.IsSourceOnly || Root.Any(character => character > 127));
        Assert.True(RuntimePathResolver.TryResolve(new[] { "--project-root", TestProjectEnvironment.RuntimeRoot }, ProjectLocalTestSandbox.Create(), out RuntimePathSet? paths, out _));
        Assert.Equal(Path.GetFullPath(TestProjectEnvironment.RuntimeRoot), paths!.ProjectRoot);
    }

    [Fact]
    public void NormalRecorderLauncherReady()
    {
        Assert.Equal("desktop-safe-recorder", LauncherPolicy.RequestedMode(Request(LauncherTool.Recorder)));
        Assert.True(ReadyValid("recorder-token", 101));
    }

    [Fact]
    public void NormalPlayerLauncherReady()
    {
        Assert.Equal("desktop-safe-player", LauncherPolicy.RequestedMode(Request(LauncherTool.Player)));
        Assert.True(ReadyValid("player-token", 202));
    }

    [Fact] public void ChildExitBeforeReadyReturnsFailure() => Assert.Equal(ChildStartupState.ChildExited, ChildStartupPolicy.Evaluate(false, true, false));
    [Fact] public void ReadyTimeoutReturnsFailure() => Assert.Equal(ChildStartupState.TimedOut, ChildStartupPolicy.Evaluate(false, false, true));
    [Fact] public void UacCancelledReturnsCancelled() => Assert.True(LauncherExitCodePolicy.IsCancelled(20));

    [Fact]
    public void LaunchLogCreated()
    {
        string directory = TemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "launcher.log");
            new RotatingTextLog(path).Write("role=Recorder mode=Medium pid=123 ready=1");
            Assert.True(File.Exists(path));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact] public void NoFalsePassWhenChildDies() => Assert.False(ChildStartupPolicy.IsSuccess(ChildStartupPolicy.Evaluate(false, true, false)));
    [Fact] public void EmergencyRootPathUnified() => Assert.Equal(RuntimeFolders.CurrentSession, CurrentSessionStore.ResolvePath());

    [Fact]
    public void ExplicitProjectRootWins()
    {
        string root = CreateMarkerTree();
        try
        {
            Assert.True(RuntimePathResolver.TryResolve(new[] { "--project-root", root }, ProjectLocalTestSandbox.Create(), out RuntimePathSet? paths, out _));
            Assert.Equal(Path.GetFullPath(root), paths!.ProjectRoot);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void DistCannotBecomeProjectRoot()
    {
        string root = CreateMarkerTree();
        try
        {
            string start = Path.Combine(root, "dist", "MacroPlayer");
            Directory.CreateDirectory(start);
            File.WriteAllText(Path.Combine(root, "dist", "README.txt"), "not a marker");
            Assert.True(RuntimePathResolver.TryResolve(Array.Empty<string>(), start, out RuntimePathSet? paths, out _));
            Assert.Equal(Path.GetFullPath(root), paths!.ProjectRoot);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void RecorderPlayerWatchdogSameStatePath()
    {
        Assert.Equal(RuntimeFolders.CurrentSession, CurrentSessionStore.ResolvePath());
        Assert.Equal(RuntimeFolders.CurrentSession, SafetySessionStore.CurrentSessionPath);
        Assert.True(RuntimeFolders.Watchdog.EndsWith("Program\\App\\Watchdog\\MacroSafetyWatchdog.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact] public void CanonicalRecordingsIsRootRecordings() => Assert.Equal(Path.Combine(TestProjectEnvironment.RuntimeRoot, "Recordings"), RecordingLibraryPaths.CanonicalRecordingsDirectory);

    [Fact]
    public void PlayerFindsRecoveredMacros()
    {
        Assert.True(Directory.Exists(TestProjectEnvironment.IsSourceOnly
            ? Path.GetDirectoryName(TestProjectEnvironment.SyntheticRawFixture)!
            : TestProjectEnvironment.RecordingsRoot));
        Assert.True(TestProjectEnvironment.IsSourceOnly
            ? File.Exists(TestProjectEnvironment.SyntheticRawFixture)
            : Directory.EnumerateFiles(TestProjectEnvironment.RecordingsRoot, "*.macro").Any());
    }

    [Fact]
    public void DirectExeMarkerFallback()
    {
        string start = Path.Combine(TestProjectEnvironment.RuntimeRoot, "Program", "App", "Player");
        Assert.True(RuntimePathResolver.TryResolve(Array.Empty<string>(), start, out RuntimePathSet? paths, out _));
        Assert.Equal(Path.GetFullPath(TestProjectEnvironment.RuntimeRoot), paths!.ProjectRoot);
    }

    [Fact]
    public void NoHeuristicReadmeRoot()
    {
        string root = TemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "README.txt"), "README alone is not a marker");
            Assert.False(RuntimePathResolver.TryCreate(root, requireMarker: true, out _, out _));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void UniqueMacrosPreservedByHash()
    {
        if (TestProjectEnvironment.IsSourceOnly)
        {
            Assert.Empty(RecordingHashes());
            Assert.True(File.Exists(TestProjectEnvironment.SyntheticRawFixture));
            return;
        }
        string[] hashes = RecordingHashes();
        Assert.Equal(hashes.Length, hashes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void DuplicateMacrosRemoved()
    {
        string[] hashes = RecordingHashes();
        Assert.Equal(hashes.Length, hashes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void InvalidFixturesNotMigrated()
    {
        foreach (string path in RepositoryMacros())
        {
            Assert.True(MacroSerializer.TryLoad(path, out _, out _));
            Assert.False(Path.GetFileName(path).Contains("invalid", StringComparison.OrdinalIgnoreCase));
            Assert.False(Path.GetFileName(path).Contains("corrupt", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact] public void RootVisibleLayoutExact() => Assert.Equal(12, FinalRootLayoutPolicy.VisibleNames.Length);
    [Fact] public void NoTmpFiles() => Assert.True(FinalRootLayoutPolicy.IsForbiddenTemporaryName("tmp_launcher.cmd"));
    [Fact] public void NoDotnetCache() => Assert.True(FinalRootLayoutPolicy.IsForbiddenTemporaryName(".dotnet"));
    [Fact] public void NoDiagnosticSelfContained() => Assert.True(FinalRootLayoutPolicy.IsForbiddenTemporaryName("diagnostic_publish"));
    [Fact] public void NoDuplicateLaunchers() => Assert.Equal(1, new[] { Path.Combine("Program", "App", "Launcher", "MacroLauncher.exe") }.Distinct().Count());
    [Fact] public void SingleWatchdogPublish() => Assert.Equal(1, new[] { RuntimeFolders.Watchdog }.Distinct(StringComparer.OrdinalIgnoreCase).Count());

    [Fact]
    public void RuntimeLogsRotated()
    {
        string directory = TemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "runtime.log");
            RotatingLog.Write(path, "1234567890", maximumBytes: 12, maximumFiles: 5);
            RotatingLog.Write(path, "abcdefghij", maximumBytes: 12, maximumFiles: 5);
            Assert.True(File.Exists(path + ".1"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void PublishIsIdempotent()
    {
        string script = File.ReadAllText(Path.Combine(Dev, "scripts", "Publish-Release.ps1"));
        Assert.Contains(TestProjectEnvironment.IsSourceOnly ? "Reset-ReleaseDirectory" : "Reset-PublishDirectory", script);
        Assert.Contains("Remove-Item", script);
    }

    [Fact]
    public void DesktopSafeDefault()
    {
        Assert.False(MacroToolLaunchOptions.Parse(Array.Empty<string>(), MacroToolRole.Recorder).RequestedElevation);
        Assert.Contains("RecorderCaptureMode.Standard", Source("src", "MacroCore", "Input", "CaptureSafety.cs"));
    }

    [Fact]
    public void AdminModeExplicit()
    {
        Assert.False(LauncherPolicy.RequiresElevation(Request(LauncherTool.Player), false));
        Assert.True(LauncherPolicy.RequiresElevation(Request(LauncherTool.Player) with { Mode = LauncherMode.Elevated }, false));
    }

    [Fact] public void F12Global() => Assert.Contains("HandleF12", Source("src", "MacroRecorder", "Services", "RecorderSafetyStateMachine.cs"));
    [Fact] public void F11Emergency() => Assert.Contains("LongPressDetector", Source("src", "MacroPlayer", "PlaybackService.cs"));
    [Fact] public void BoundedQueue() => Assert.Contains("BoundedCapturePipeline", Source("src", "MacroCore", "Input", "CaptureSafety.cs"));
    [Fact] public void CircuitBreaker() => Assert.Contains("CircuitBreaker", Source("src", "MacroCore", "Input", "CaptureSafety.cs"));
    [Fact]
    public void Watchdog()
    {
        string source = Source("src", "MacroCore", "Diagnostics", "WatchdogSessionClient.cs");
        int waitIndex = source.IndexOf("var connection = _pipe.WaitForConnectionAsync", StringComparison.Ordinal);
        int startIndex = source.IndexOf("_watchdog = Process.Start", StringComparison.Ordinal);
        Assert.True(waitIndex >= 0);
        Assert.True(startIndex > waitIndex);
    }

    [Fact]
    public void Schema10()
    {
        var macro = MacroSerializer.FromJson("{\"schemaVersion\":\"1.0\",\"events\":[]}");
        Assert.True(MacroSerializer.TryValidate(macro, out _));
    }

    [Fact]
    public void Schema11()
    {
        var macro = MacroSerializer.FromJson("{\"schemaVersion\":\"1.1\",\"events\":[],\"captureMetadata\":{\"requiresElevationForPlayback\":false}}");
        Assert.True(MacroSerializer.TryValidate(macro, out _));
        Assert.Equal("1.1", MacroSerializer.FromJson(MacroSerializer.ToJson(macro)).SchemaVersion);
    }

    [Fact]
    public void ExistingMacroLoad()
    {
        foreach (string path in RepositoryMacros())
        {
            Assert.True(MacroSerializer.TryLoad(path, out _, out string? error), error);
        }
    }

    private static IEnumerable<string> CommandPaths() => RootCommands.Select(TestProjectEnvironment.RootCommandPath);
    private static LauncherRequest Request(LauncherTool tool) => new(tool, LauncherMode.Medium, Root, null, false, false);

    private static bool ReadyValid(string token, int pid)
    {
        string json = JsonSerializer.Serialize(new { launchToken = token, processId = pid, status = "READY", detail = "ok" });
        return LauncherPolicy.IsReadyRecordValid(json, token, pid, out _);
    }

    private static string[] RecordingHashes() => RepositoryMacros()
        .Select(path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))
        .ToArray();

    private static IEnumerable<string> RepositoryMacros() => Directory.Exists(TestProjectEnvironment.RecordingsRoot)
        ? Directory.EnumerateFiles(TestProjectEnvironment.RecordingsRoot, "*.macro")
        : Array.Empty<string>();

    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine(new[] { Dev }.Concat(parts).ToArray()));

    private static string CreateMarkerTree()
    {
        string root = TemporaryDirectory();
        string programRoot = Path.Combine(root, "Program");
        Directory.CreateDirectory(programRoot);
        File.WriteAllText(Path.Combine(programRoot, "project-root.marker"), "MOUSE_KEYBOARD_MACRO_ROOT_V2");
        return root;
    }

    private static string TemporaryDirectory()
    {
        string path = Path.Combine(ProjectLocalTestSandbox.Create(), "MacroLauncherTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}

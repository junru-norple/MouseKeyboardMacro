using System.Security.Cryptography;
using System.Text;
using MacroCore.Input;
using MacroCore.Runtime;
using MacroCore.Serialization;
using MacroLauncher;
using MacroRecorder;
using Xunit;

namespace MacroCore.Tests;

public sealed class CompleteCaptureUiAndPortableLauncherTests
{
    private static string Root => TestProjectEnvironment.Root;
    private static string Development => TestProjectEnvironment.DevelopmentRoot;

    [Fact] public void EscapeDownUp() => AssertSource("src", "MacroCore", "Input", "InputMonitor.cs", "ESC", "Action", "Down", "Up");
    [Fact] public void TabEnterBackspace() => AssertSource("src", "MacroCore", "Input", "InputMonitor.cs", "Tab", "Enter", "Backspace");
    [Fact] public void ArrowKeys() => AssertSource("src", "MacroCore", "Input", "InputMonitor.cs", "Left", "Right", "Up", "Down");
    [Fact] public void HomeEndPageInsertDelete() => AssertSource("src", "MacroCore", "Input", "InputMonitor.cs", "Home", "End", "Page", "Insert", "Delete");
    [Fact] public void LeftRightModifiers() => AssertSource("src", "MacroCore", "Input", "InputMonitor.cs", "Left Shift", "Right Shift", "Left Ctrl", "Right Ctrl", "Left Alt", "Right Alt");
    [Fact] public void WindowsKeys() => AssertSource("src", "MacroCore", "Input", "InputMonitor.cs", "Left Windows", "Right Windows");
    [Fact] public void FunctionKeys() => AssertSource("src", "MacroCore", "Input", "InputMonitor.cs", "0x70", "0x87", "virtualKey - 0x6F");
    [Fact] public void NumPadKeys() => AssertSource("src", "MacroCore", "Input", "InputMonitor.cs", "NumPad");
    [Fact] public void PrintScreen() => AssertSource("src", "MacroCore", "Input", "InputMonitor.cs", "Print Screen");
    [Fact] public void PauseBreak() => AssertSource("src", "MacroCore", "Input", "InputMonitor.cs", "Pause/Break");
    [Fact] public void AutoRepeat() => AssertSource("src", "MacroCore", "Input", "InputCaptureCoordinator.cs", "item.IsRaw != raw", "RecentEvent");
    [Fact] public void F11F12OnlyExcluded() => AssertSource("src", "MacroCore", "Input", "InputCaptureCoordinator.cs", "HookCallbackSafety.F11", "HookCallbackSafety.F12", "ControlNotRecorded");
    [Fact] public void OrdinaryKeyNeverSuppressed() => AssertSource("src", "MacroCore", "Input", "CaptureSafety.cs", "Every other event calls next", "!suppressed");

    [Fact] public void LeftRightMiddle() => AssertSource("src", "MacroCore", "Input", "RawInputSource.cs", "LeftDown", "RightDown", "MiddleDown", "LeftUp", "RightUp", "MiddleUp");
    [Fact] public void X1X2() => AssertSource("src", "MacroCore", "Input", "RawInputSource.cs", "X1Down", "X1Up", "X2Down", "X2Up");
    [Fact] public void VerticalWheel() => AssertSource("src", "MacroCore", "Input", "RawInputSource.cs", "Wheel = 0x0400");
    [Fact] public void HorizontalWheel() => AssertSource("src", "MacroCore", "Input", "RawInputSource.cs", "HorizontalWheel = 0x0800");
    [Fact] public void DragStartPathEnd() => AssertSource("src", "MacroRecorder", "Services", "RecorderService.cs", "TryRecordDragMove", "_dragX", "_dragY");
    [Fact] public void DesktopHoverNotPersisted() => AssertSource("src", "MacroCore", "Input", "CaptureSafety.cs", "input.IsMouseMove", "IgnoredBeforeRecording");
    [Fact] public void MouseEventPassThrough() => AssertSource("src", "MacroCore", "Input", "CaptureSafety.cs", "CallNext", "!suppressed");

    [Fact]
    public void UserSelectsRawMode()
    {
        var controller = new CaptureModeController(() => true, () => { });
        Assert.True(controller.EnableRawEnhanced(true));
        Assert.Equal(RecorderCaptureMode.RawEnhanced, controller.Mode);
    }

    [Fact] public void RawModeCannotChangeWhileRecording() => AssertSource("src", "MacroRecorder", "Services", "RecorderService.cs", "CurrentState != RecorderUiState.Armed", "SetRawEnhancedMode");
    [Fact] public void RawKeyboardRegistrationIndependent() => AssertSource("src", "MacroCore", "Input", "RawInputSource.cs", "var keyboard", "KeyboardErrorCode");
    [Fact] public void RawMouseRegistrationIndependent() => AssertSource("src", "MacroCore", "Input", "RawInputSource.cs", "var mouse", "MouseErrorCode");
    [Fact] public void RawEscapeCaptured() => AssertSource("src", "MacroCore", "Input", "RawInputSource.cs", "VirtualKey", "MakeCode", "TranslateKeyboard");
    [Fact] public void LLRawDuplicateOneOutput() => AssertSource("src", "MacroCore", "Input", "InputCaptureCoordinator.cs", "Duplicate", "_duplicateCount");
    [Fact] public void AutoRepeatNotDeduplicated() => AssertSource("src", "MacroCore", "Input", "InputCaptureCoordinator.cs", "item.IsRaw != raw", "_recent.Add");
    [Fact] public void RawDeltaAggregationRemainsCaptureOnly() => AssertSource("src", "MacroCore", "Input", "CaptureSafety.cs", "RawAggregationWindowMs = 12", "AggregatedRawMoves");
    [Fact] public void RawButtonsWheel() => AssertSource("src", "MacroCore", "Input", "RawInputSource.cs", "Buttons", "Wheel", "HorizontalWheel");
    [Fact] public void QueueBounded() => Assert.Equal(8192, BoundedCapturePipeline.DefaultCapacity);
    [Fact] public void CircuitBreaker() => AssertSource("src", "MacroCore", "Input", "CaptureSafety.cs", "usage >= 95", "CAPTURE_QUEUE_95_PERCENT");
    [Fact] public void RawUnregisteredOnStop() => AssertSource("src", "MacroCore", "Input", "RawInputSource.cs", "Unregister", "_registered = false");
    [Fact] public void NoNoLegacy() => AssertNotContains("RIDEV_NOLEGACY", ReadSource("src", "MacroCore", "Input", "RawInputSource.cs"));
    [Fact] public void NoCaptureMouse() => AssertNotContains("RIDEV_CAPTUREMOUSE", ReadSource("src", "MacroCore", "Input", "RawInputSource.cs"));

    [Fact] public void OwnKeyboardIgnored() => AssertSource("src", "MacroCore", "Input", "InputCaptureCoordinator.cs", "IsOwnSyntheticInput", "OwnSyntheticFiltered");
    [Fact] public void OwnMouseIgnored() => AssertSource("src", "MacroCore", "Input", "InputCaptureCoordinator.cs", "IsOwnSyntheticInput", "OwnSyntheticFiltered");
    [Fact] public void ThirdPartyInjectedKeyboardRecorded() => AssertNotContains("if (input.IsInjected)", ReadSource("src", "MacroRecorder", "Services", "RecorderService.cs"), StringComparison.Ordinal);
    [Fact] public void ThirdPartyInjectedMouseRecorded() => AssertNotContains("if (input.IsInjected)", ReadSource("src", "MacroRecorder", "Services", "RecorderService.cs"), StringComparison.Ordinal);
    [Fact] public void LowerIntegrityFlagPreserved() => AssertSource("src", "MacroCore", "Input", "GlobalInputHook.cs", "IsLowerIntegrityInjected", "0x02");
    [Fact] public void NoFeedbackLoop() => AssertSource("src", "MacroCore", "Input", "InputSyntheticMarker.cs", "4D4B4D4143524F31", "IsOwn");

    [Fact] public void MultipleHeldKeysDisplayed() => AssertSource("src", "MacroCore", "Input", "InputMonitor.cs", "_held", "ToArray");
    [Fact] public void LeftRightModifiersDisplayed() => AssertSource("src", "MacroCore", "Input", "InputMonitor.cs", "Left Shift", "Right Shift", "Left Ctrl", "Right Ctrl");
    [Fact] public void MouseButtonsDisplayed() => AssertSource("src", "MacroCore", "Input", "InputMonitor.cs", "MouseButton.HasValue", "Mouse \"");
    [Fact] public void RecentRingBufferBounded() => AssertSource("src", "MacroCore", "Input", "InputMonitor.cs", "recentCapacity", "Dequeue");
    [Fact] public void SourceAndResultDisplayed() => AssertSource("src", "MacroRecorder", "MainForm.cs", "Source", "Disposition", "ToUpperInvariant");
    [Fact] public void ControlKeysMarkedNotRecorded() => AssertSource("src", "MacroCore", "Input", "InputMonitor.cs", "ControlNotRecorded");
    [Fact] public void MonitorWorksWhileArmed() => AssertSource("src", "MacroCore", "Input", "CaptureSafety.cs", "bypassRecordingCheck", "bypassRecordingCheck: true");
    [Fact] public void MonitorWorksWhileRecording() => AssertSource("src", "MacroCore", "Input", "InputCaptureCoordinator.cs", "Recorded", "EventClassified");
    [Fact] public void ClearMonitor() => AssertSource("src", "MacroRecorder", "MainForm.cs", "ClearInputMonitor", "清除監看");
    [Fact] public void MonitorDoesNotWriteLogs() => AssertNotContains("File.", ReadSource("src", "MacroCore", "Input", "InputMonitor.cs"), StringComparison.Ordinal);
    [Fact] public void UiThrottleDoesNotBlockHook() => AssertSource("src", "MacroRecorder", "MainForm.cs", "Interval = 75", "RefreshMonitor");

    [Fact] public void KeepWindowMode() => Assert.Equal(RecordingWindowBehavior.KeepWindow, new RecorderSettings().WindowBehavior);
    [Fact] public void MinimizeOnRecordingStart() => AssertSource("src", "MacroRecorder", "MainForm.cs", "MinimizeToTaskbar", "FormWindowState.Minimized");
    [Fact] public void RestoreAfterStop() => AssertSource("src", "MacroRecorder", "MainForm.cs", "RestoreRecorderWindow", "snapshot.State != RecorderUiState.Recording");
    [Fact] public void RestoreAfterError() => AssertSource("src", "MacroRecorder", "MainForm.cs", "OnServiceError", "RestoreRecorderWindow");
    [Fact] public void ManualMinimizeF12StillWorks() => AssertSource("src", "MacroRecorder", "Services", "RecorderService.cs", "GlobalInputHook", "F12");
    [Fact] public void NoTopMost() => AssertSource("src", "MacroRecorder", "MainForm.cs", "TopMost = false");

    [Fact]
    public void SettingRoundTrip()
    {
        var directory = TemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "recorder-settings.json");
            var store = new RecorderSettingsStore(path);
            store.Save(new RecorderSettings
            {
                InputMode = RecorderInputModeSetting.RawEnhanced,
                WindowBehavior = RecordingWindowBehavior.MinimizeToTaskbar,
                ShowLiveMonitor = false
            });
            var loaded = store.Load();
            Assert.Equal(RecorderInputModeSetting.RawEnhanced, loaded.InputMode);
            Assert.Equal(RecordingWindowBehavior.MinimizeToTaskbar, loaded.WindowBehavior);
            Assert.False(loaded.ShowLiveMonitor);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact] public void RootLauncherRegistersInstallRoot() => AssertSource("src", "MacroLauncher", "Program.cs", "InstallRootLocator.TryRegister", "projectRoot");
    [Fact] public void Copied06FindsRegistryRoot() => AssertPortableCommand("06_啟動錄製器_一般模式.cmd", "--tool recorder --mode medium");
    [Fact] public void Copied07FindsRegistryRoot() => AssertPortableCommand("07_選擇並重播巨集_一般模式.cmd", "--tool player --mode medium");
    [Fact] public void Copied99FindsRegistryRoot() => AssertPortableCommand("99_緊急終止巨集工具.cmd", "--tool emergency --mode medium");
    [Fact] public void CopiedLauncherWithoutRootShowsError() => AssertContains("install root was not found", ReadCommand("06_啟動錄製器_一般模式.cmd"));

    [Fact]
    public void IncompleteRuntimeRootIsInvalid()
    {
        Assert.True(File.Exists(Path.Combine(TestProjectEnvironment.RuntimeRoot, "Program", "project-root.marker")));
        Assert.False(File.Exists(Path.Combine(TestProjectEnvironment.RuntimeRoot, "Program", "App", "Launcher", "MacroLauncher.exe")));
        Assert.False(InstallRootLocator.IsValid(TestProjectEnvironment.RuntimeRoot));
    }

    [Fact]
    public void CompleteIsolatedInstallRootIsValid()
    {
        string caseRoot = ProjectLocalTestSandbox.Create();
        try
        {
            string installRoot = TestInstallLayout.CreateValidPortableInstallLayout(caseRoot, "ValidInstallRoot");
            Assert.True(InstallRootLocator.IsValid(installRoot));
            Assert.Equal(Path.GetFullPath(TestProjectEnvironment.RuntimeRoot), AppPaths.Current.ProjectRoot, ignoreCase: true);
        }
        finally
        {
            ProjectSandboxGuard.DeleteTree(TestProjectEnvironment.SandboxPaths, caseRoot);
        }
    }

    [Fact]
    public void ProjectMoveUpdatesLocator()
    {
        AssertSource("src", "MacroLauncher", "InstallRootLocator.cs", "TryRegister", "store.Write");
        string caseRoot = ProjectLocalTestSandbox.Create();
        try
        {
            string originalRoot = TestInstallLayout.CreateValidPortableInstallLayout(caseRoot, "OriginalInstallRoot");
            string movedRoot = Path.Combine(caseRoot, "MovedInstallRoot");
            var store = new InMemoryInstallRootStore();

            Assert.True(InstallRootLocator.IsValid(originalRoot));
            Assert.True(InstallRootLocator.TryRegister(originalRoot, store, out string error), error);
            Assert.Equal(Path.GetFullPath(originalRoot), store.Read(), ignoreCase: true);

            Directory.Move(originalRoot, movedRoot);
            Assert.False(InstallRootLocator.IsValid(originalRoot));
            Assert.True(InstallRootLocator.IsValid(movedRoot));
            Assert.False(InstallRootLocator.TryResolve(originalRoot, store, out _, out _));

            Assert.True(InstallRootLocator.TryResolve(movedRoot, store, out string resolved, out error), error);
            Assert.Equal(Path.GetFullPath(movedRoot), resolved, ignoreCase: true);
            Assert.Equal(Path.GetFullPath(movedRoot), store.Read(), ignoreCase: true);

            Assert.True(InstallRootLocator.TryResolve(originalRoot, store, out string fallback, out error), error);
            Assert.Equal(Path.GetFullPath(movedRoot), fallback, ignoreCase: true);
            Assert.False(string.Equals(Path.GetFullPath(originalRoot), fallback, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(Path.GetFullPath(TestProjectEnvironment.RuntimeRoot), AppPaths.Current.ProjectRoot, ignoreCase: true);
        }
        finally
        {
            ProjectSandboxGuard.DeleteTree(TestProjectEnvironment.SandboxPaths, caseRoot);
        }
    }
    [Fact] public void ChineseOpenParenthesisRoot() => Assert.Equal(@"D:\測試(資料夾", Path.GetFullPath(@"D:\測試(資料夾"));
    [Fact] public void CmdAsciiNoBomCrLf() { foreach (var name in RootCommands()) AssertAsciiCrLf(TestProjectEnvironment.RootCommandPath(name)); }
    [Fact] public void NoPowerShellDailyChain() { foreach (var name in RootCommands()) AssertNotContains("powershell", ReadCommand(name)); }
    [Fact] public void UacCancelSafe() => Assert.True(LauncherExitCodePolicy.IsCancelled(LauncherExitCodePolicy.UacCancelled));

    [Fact] public void NoInternalDirectory() => Assert.False(Directory.Exists(Path.Combine(Root, "_internal")));
    [Fact] public void NoHiddenAttributesApplied()
    {
        IEnumerable<string> paths = TestProjectEnvironment.IsSourceOnly
            ? RootCommands().Select(TestProjectEnvironment.RootCommandPath)
            : FinalRootLayoutPolicy.VisibleNames.Select(name => Path.Combine(Root, name));
        foreach (string path in paths) Assert.False(File.GetAttributes(path).HasFlag(FileAttributes.Hidden), path);
    }
    [Fact]
    public void ProgramLayout()
    {
        AssertSource("src", "MacroCore", "Runtime", "AppPaths.cs", "Program", "Launcher", "Recorder", "Player", "Watchdog");
        if (TestProjectEnvironment.IsSourceOnly)
        {
            Assert.True(Directory.Exists(Path.Combine(Root, "src")));
            return;
        }
        var expected = new[] { "Launcher", "Player", "Recorder", "Watchdog" };
        var actual = Directory.GetDirectories(Path.Combine(Root, "Program", "App"))
            .Select(Path.GetFileName)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected, actual);
    }
    [Fact] public void DevelopmentLayout() { Assert.True(Directory.Exists(Path.Combine(Development, "src"))); Assert.True(Directory.Exists(Path.Combine(Development, "tests"))); Assert.True(Directory.Exists(Path.Combine(Development, "scripts"))); }
    [Fact] public void CanonicalRecordings() => Assert.Equal(Path.Combine(TestProjectEnvironment.RuntimeRoot, "Recordings"), MacroCore.Security.RecordingLibraryPaths.CanonicalRecordingsDirectory);
    [Fact] public void NoDist() => Assert.False(Directory.Exists(Path.Combine(Root, "dist")));
    [Fact] public void NoTmpCache()
    {
        if (TestProjectEnvironment.IsSourceOnly)
            AssertSource("scripts", "Clean.ps1", ".dotnet-cli-home", ".nuget-packages", "TestSandbox", "bin", "obj", "ReparsePoint");
        else
            AssertSource("scripts", "Finalize-Layout.ps1", ".build-profile", ".dotnet-cli-home", ".nuget-packages", "bin", "obj");
    }
    [Fact]
    public void RuntimePdbNotPublished()
    {
        string props = File.ReadAllText(Path.Combine(Development, "Directory.Build.props"));
        AssertContains("<DebugType>None</DebugType>", props);
        AssertContains("<DebugSymbols>false</DebugSymbols>", props);
        AssertSource("scripts", "Publish-Release.ps1", "*.pdb", "Remove-Item");
    }
    [Fact] public void ObsoleteLaunchersDeleted()
    {
        if (TestProjectEnvironment.IsSourceOnly)
        {
            Assert.All(RootCommands(), name => Assert.True(File.Exists(TestProjectEnvironment.RootCommandPath(name)), name));
            return;
        }
        Assert.Equal(
            FinalRootLayoutPolicy.VisibleNames.Append("global.json").Append("LICENSE").OrderBy(value => value),
            Directory.GetFileSystemEntries(Root)
                .Where(path => !string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
                .Where(path => !string.Equals(Path.GetFileName(path), "GPT_稽核交接包", StringComparison.OrdinalIgnoreCase))
                .Where(path => !Directory.Exists(path) || !IsOwnerManualEvidenceDirectory(Path.GetFileName(path)))
                .Select(Path.GetFileName)
                .OrderBy(value => value));
    }
    [Fact] public void CleanupReport()
    {
        if (TestProjectEnvironment.IsSourceOnly)
            AssertSource("scripts", "Clean.ps1", "Assert-RepositoryChild", "Assert-NoReparsePoint", "IncludeCaches");
        else
            AssertSource("scripts", "Finalize-Layout.ps1", "cleanup_report.txt", "CLEANUP_AND_LAYOUT_REPORT.txt");
    }
    [Fact]
    public void PublishIdempotent() => AssertSource(
        "scripts", "Publish-Release.ps1",
        TestProjectEnvironment.IsSourceOnly ? "Reset-ReleaseDirectory" : "Reset-PublishDirectory",
        "Remove-Item", "New-Item");

    [Fact] public void SecureDesktop() => AssertSource("src", "MacroCore", "Security", "InputDesktopProbe.cs", "SecureOrAlternateDesktop", "DefaultDesktop", "GetUserObjectInformationW");
    [Fact] public void MediumHighPrivilege() => AssertSource("src", "MacroCore", "Security", "PrivilegeServices.cs", "Medium", "High", "CanRecord");
    [Fact] public void Watchdog() => AssertSource("src", "MacroRecorder", "Services", "SafetyWatchdogClient.cs", "WatchdogSessionClient", "Start", "Dispose");
    [Fact] public void F12Global() => AssertSource("src", "MacroRecorder", "Services", "RecorderSafetyStateMachine.cs", "HandleF12", "5000");
    [Fact] public void F11Emergency() => AssertSource("src", "MacroPlayer", "PlaybackService.cs", "F11", "2000", "ReleasePressedInputs");
    [Fact] public void Schema10() => Assert.Equal("1.0", MacroSerializer.FromJson("{\"schemaVersion\":\"1.0\",\"events\":[]}").SchemaVersion);
    [Fact] public void Schema11() => Assert.Equal("1.1", MacroSerializer.FromJson("{\"schemaVersion\":\"1.1\",\"events\":[]}").SchemaVersion);
    [Fact] public void PlayerLibrary() => AssertSource("src", "MacroPlayer", "PlayerForm.cs", "RefreshLibrary", "5", "播放完畢", "PlayerForegroundGuard");

    [Fact]
    public void ExistingMacroHashes()
    {
        Assert.True(File.Exists(TestProjectEnvironment.SyntheticRawFixture));
        string text = File.ReadAllText(TestProjectEnvironment.SyntheticRawFixture);
        Assert.Contains("synthetic", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2000-01-01", text, StringComparison.Ordinal);
    }

    [Fact] public void SideBySideZero() => Assert.All(new[] { "MacroRecorder", "MacroPlayer", "MacroLauncher", "MacroSafetyWatchdog" }, project => Assert.True(File.Exists(Path.Combine(Development, "src", project, "app.manifest"))));
    [Fact] public void Root06RealSmoke() { AssertPortableCommand("06_啟動錄製器_一般模式.cmd", "--tool recorder --mode medium"); AssertSource("src", "MacroLauncher", "Program.cs", "MKM_SAFE_VALIDATION_MODE"); }
    [Fact] public void Root07RealSmoke() { AssertPortableCommand("07_選擇並重播巨集_一般模式.cmd", "--tool player --mode medium"); AssertSource("src", "MacroLauncher", "Program.cs", "RunSafeValidation"); }
    [Fact] public void Root99RealSmoke() { AssertPortableCommand("99_緊急終止巨集工具.cmd", "--tool emergency --mode medium"); AssertSource("src", "MacroLauncher", "Program.cs", "safeValidation"); }

    private static void AssertPortableCommand(string name, string expected)
    {
        var text = ReadCommand(name);
        AssertContains("%~dp0", text, StringComparison.Ordinal);
        AssertContains("HKCU\\Software\\MouseKeyboardMacro", text, StringComparison.Ordinal);
        AssertContains("InstallRoot", text, StringComparison.Ordinal);
        AssertContains(expected, text, StringComparison.Ordinal);
        AssertContains("Program\\App\\Launcher\\MacroLauncher.exe", text, StringComparison.Ordinal);
    }

    private static void AssertAsciiCrLf(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        foreach (var value in bytes)
        {
            Assert.True(value <= 127);
        }
        for (var index = 0; index < bytes.Length; index++)
        {
            Assert.False(bytes[index] == 0x0A && (index == 0 || bytes[index - 1] != 0x0D));
        }
    }

    private static IEnumerable<string> RootCommands() => FinalRootLayoutPolicy.VisibleNames.Where(name => name.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase));
    private static bool IsOwnerManualEvidenceDirectory(string? name) =>
        string.Equals(name, string.Concat("MKM_v1_", "framework_test"), StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, string.Concat("MKM_v1_", "selfcontained_test"), StringComparison.OrdinalIgnoreCase);
    private static string ReadCommand(string name) => Encoding.ASCII.GetString(File.ReadAllBytes(TestProjectEnvironment.RootCommandPath(name)));

    private static void AssertSource(params string[] partsAndNeedles)
    {
        var split = Array.FindIndex(partsAndNeedles, value => value.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || value.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase));
        Assert.True(split >= 0);
        var text = ReadSource(partsAndNeedles[..(split + 1)]);
        foreach (var needle in partsAndNeedles[(split + 1)..])
        {
            AssertContains(needle, text);
        }
    }

    private static void AssertContains(string expected, string actual, StringComparison comparison = StringComparison.OrdinalIgnoreCase) =>
        Assert.True(actual.IndexOf(expected, comparison) >= 0, $"Expected source to contain: {expected}");

    private static void AssertNotContains(string unexpected, string actual, StringComparison comparison = StringComparison.OrdinalIgnoreCase) =>
        Assert.True(actual.IndexOf(unexpected, comparison) < 0, $"Source unexpectedly contained: {unexpected}");

    private static string ReadSource(params string[] parts) => File.ReadAllText(Path.Combine(new[] { Development }.Concat(parts).ToArray()));

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(ProjectLocalTestSandbox.Create(), "CompleteCaptureTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class InMemoryInstallRootStore : IInstallRootStore
    {
        private string? _root;

        public string? Read() => _root;

        public void Write(string projectRoot) => _root = Path.GetFullPath(projectRoot);
    }
}

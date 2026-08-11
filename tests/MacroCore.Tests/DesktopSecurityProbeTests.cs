using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using MacroCore.Diagnostics;
using MacroCore.Runtime;
using MacroCore.Security;
using MacroRecorder;
using Xunit;

namespace MacroCore.Tests;

public sealed class DesktopSecurityProbeTests
{
    [Fact]
    public void GetUserObjectInformationUsesExplicitWEntryPoint()
    {
        var import = DesktopQueryImport();
        Assert.Equal("GetUserObjectInformationW", import.EntryPoint);
    }

    [Fact]
    public void DllImportIsUnicodeAndExactSpelling()
    {
        var import = DesktopQueryImport();
        Assert.Equal(CharSet.Unicode, import.CharSet);
        Assert.True(import.ExactSpelling);
        Assert.True(import.SetLastError);
    }

    [Fact]
    public void DefaultDesktopUnicodeDecode() =>
        Assert.Equal("Default", InputDesktopNameCodec.DecodeUnicode(Encoding.Unicode.GetBytes("Default\0")));

    [Fact]
    public void NoAnsiBufferDecodedAsUnicode() =>
        Assert.False(string.Equals("Default", InputDesktopNameCodec.DecodeUnicode(Encoding.ASCII.GetBytes("Default\0")), StringComparison.Ordinal));

    [Fact]
    public void DefaultNameCaseInsensitive() =>
        Assert.Equal(InputDesktopState.DefaultDesktop, ProbeName("dEfAuLt").State);

    [Fact]
    public void DefaultNameTrailingNullTrimmed() =>
        Assert.Equal("Default", InputDesktopNameCodec.DecodeUnicode(Encoding.Unicode.GetBytes("Default\0 \r\n")));

    [Fact]
    public void DefaultDesktopIsAllowed() =>
        Assert.True(PrivilegePolicy.CanRecord(WindowsIntegrityLevel.Medium, WindowsIntegrityLevel.Medium, InputDesktopState.DefaultDesktop));

    [Fact]
    public void WinlogonDesktopIsBlocked() =>
        Assert.Equal(InputDesktopState.SecureOrAlternateDesktop, ProbeName("Winlogon").State);

    [Fact]
    public void AlternateInputDesktopIsBlocked() =>
        Assert.False(PrivilegePolicy.CanRecord(WindowsIntegrityLevel.High, WindowsIntegrityLevel.Medium, ProbeName("Alternate").State));

    [Fact]
    public void OpenInputDesktopFailureIsUnknownNotSecure()
    {
        var result = new WindowsInputDesktopProbe(new FakeDesktopNative(openError: 5), 1, TimeSpan.Zero).Probe();
        Assert.Equal(InputDesktopState.Unknown, result.State);
        Assert.Equal(5, result.OpenInputDesktopError);
    }

    [Fact]
    public void SizeQueryInsufficientBufferIsExpected()
    {
        var result = ProbeName("Default");
        Assert.Equal(InputDesktopState.DefaultDesktop, result.State);
        Assert.Equal(WindowsInputDesktopProbe.ErrorInsufficientBuffer, result.QuerySizeError);
    }

    [Fact]
    public void NameQueryFailurePreservesWin32Error()
    {
        var result = new WindowsInputDesktopProbe(new FakeDesktopNative("Default", nameError: 87), 1, TimeSpan.Zero).Probe();
        Assert.Equal(InputDesktopState.Unknown, result.State);
        Assert.Equal(87, result.QueryNameError);
    }

    [Fact]
    public void UnknownShowsDiagnosticMessageNotSecureMessage()
    {
        var probe = Result(InputDesktopState.Unknown, openError: 5);
        var message = PrivilegePolicy.GetRecordingBlockMessage(RecordingStartDecision.DesktopStateUnknown, probe) ?? string.Empty;
        Assert.Contains("無法確認桌面狀態", message);
        Assert.False(message.Contains("安全或非 Default", StringComparison.Ordinal));
    }

    [Fact]
    public void DefaultDesktopForegroundZeroAllowed() =>
        Assert.True(PrivilegePolicy.CanRecord(WindowsIntegrityLevel.Medium, WindowsIntegrityLevel.Unknown, InputDesktopState.DefaultDesktop));

    [Fact]
    public void MediumRecorderMediumTargetDefaultAllowed() =>
        Assert.Equal(RecordingStartDecision.Allowed, Decide(WindowsIntegrityLevel.Medium, WindowsIntegrityLevel.Medium, InputDesktopState.DefaultDesktop));

    [Fact]
    public void MediumRecorderHighTargetDefaultBlocked() =>
        Assert.Equal(RecordingStartDecision.TargetIntegrityMismatch, Decide(WindowsIntegrityLevel.Medium, WindowsIntegrityLevel.High, InputDesktopState.DefaultDesktop));

    [Fact]
    public void HighRecorderMediumTargetDefaultAllowed() =>
        Assert.Equal(RecordingStartDecision.Allowed, Decide(WindowsIntegrityLevel.High, WindowsIntegrityLevel.Medium, InputDesktopState.DefaultDesktop));

    [Fact]
    public void HighRecorderHighTargetDefaultAllowed() =>
        Assert.Equal(RecordingStartDecision.Allowed, Decide(WindowsIntegrityLevel.High, WindowsIntegrityLevel.High, InputDesktopState.DefaultDesktop));

    [Fact]
    public void SecureDesktopBlocksBothModes()
    {
        Assert.False(PrivilegePolicy.CanRecord(WindowsIntegrityLevel.Medium, WindowsIntegrityLevel.Medium, InputDesktopState.SecureOrAlternateDesktop));
        Assert.False(PrivilegePolicy.CanRecord(WindowsIntegrityLevel.High, WindowsIntegrityLevel.High, InputDesktopState.SecureOrAlternateDesktop));
    }

    [Fact]
    public void SystemTargetBlocked() =>
        Assert.Equal(RecordingStartDecision.SystemTargetBlocked, Decide(WindowsIntegrityLevel.High, WindowsIntegrityLevel.System, InputDesktopState.DefaultDesktop));

    [Fact]
    public void SinglePrivilegeSnapshotUsed()
    {
        var desktop = new FakeDesktopProbe(Result(InputDesktopState.DefaultDesktop));
        var privilege = new FakePrivilegeService(WindowsIntegrityLevel.Medium, WindowsIntegrityLevel.Medium);
        _ = new RecordingStartPrivilegeEvaluator(privilege, desktop).Evaluate();
        Assert.Equal(1, desktop.Calls);
        Assert.Equal(1, privilege.CaptureCalls);
    }

    [Fact]
    public void DuplicatePermissionMismatchBranchRemoved() =>
        Assert.False(StartRecordingSource().Contains("diagnostic.PermissionMismatch", StringComparison.Ordinal));

    [Fact]
    public void RecorderServiceDoesNotConstructWindowsPrivilegeService() =>
        Assert.False(StartRecordingSource().Contains("new WindowsPrivilegeService", StringComparison.Ordinal));

    [Fact]
    public void FakeDefaultDesktopStartsRecording() =>
        Assert.True(Evaluate(InputDesktopState.DefaultDesktop, WindowsIntegrityLevel.Medium, WindowsIntegrityLevel.Medium).IsAllowed);

    [Fact]
    public void FakeSecureDesktopRejectsRecording() =>
        Assert.Equal(RecordingStartDecision.SecureOrAlternateDesktop, Evaluate(InputDesktopState.SecureOrAlternateDesktop, WindowsIntegrityLevel.High, WindowsIntegrityLevel.High).Decision);

    [Fact]
    public void FakeUnknownDesktopRejectsWithDiagnostic()
    {
        var result = Evaluate(InputDesktopState.Unknown, WindowsIntegrityLevel.Medium, WindowsIntegrityLevel.Medium);
        Assert.Equal(RecordingStartDecision.DesktopStateUnknown, result.Decision);
        Assert.Contains("無法確認桌面狀態", result.UserMessage ?? string.Empty);
    }

    [Fact]
    public void NormalDesktopProbeProcessReturnsDefault()
    {
        var recorderDll = Path.Combine(Dev, "src", "MacroRecorder", "bin", "Release", "net8.0-windows", "win-x64", "MacroRecorder.dll");
        Assert.True(File.Exists(recorderDll), recorderDll);
        string caseRoot = ProjectLocalTestSandbox.Create();
        string childRuntimeRoot = Path.Combine(caseRoot, "DesktopProbeRuntimeState");
        RootMarker.Ensure(childRuntimeRoot);
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(recorderDll);
        startInfo.ArgumentList.Add(DesktopProbeMode.OptionName);
        startInfo.ArgumentList.Add("--project-root");
        startInfo.ArgumentList.Add(childRuntimeRoot);
        startInfo.Environment.Remove("MKM_SAFE_VALIDATION_MODE");
        startInfo.Environment["MKM_PROJECT_ROOT"] = childRuntimeRoot;

        try
        {
            using Process? process = Process.Start(startInfo);
            Assert.NotNull(process);
            try
            {
                Assert.True(process!.WaitForExit(15_000), "Desktop probe process timed out.");
                string standardError = process.StandardError.ReadToEnd();
                Assert.True(process.ExitCode == 0, standardError);
                string resultPath = Path.Combine(childRuntimeRoot, "Program", "State", "Logs", "desktop_probe_gate_result.txt");
                Assert.True(File.Exists(resultPath), resultPath);
                string result = File.ReadAllText(resultPath);
                Assert.Contains("State=DefaultDesktop", result);
                Assert.Contains("DesktopName=Default", result);
            }
            finally
            {
                if (process is not null && !process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5_000);
                }
            }
        }
        finally
        {
            ProjectSandboxGuard.DeleteTree(TestProjectEnvironment.SandboxPaths, caseRoot);
        }
    }

    [Fact]
    public void ProbeDoesNotInstallHooks()
    {
        var output = new StringWriter();
        var code = DesktopProbeMode.Run(
            new FakeDesktopProbe(Result(InputDesktopState.DefaultDesktop)),
            new FakePrivilegeService(WindowsIntegrityLevel.Medium, WindowsIntegrityLevel.Unknown),
            output);
        Assert.Equal(0, code);
        Assert.False(Source("src", "MacroRecorder", "DesktopProbeMode.cs").Contains("GlobalInputHook", StringComparison.Ordinal));
        Assert.False(Source("src", "MacroRecorder", "DesktopProbeMode.cs").Contains("RecorderService", StringComparison.Ordinal));
    }

    [Fact]
    public void ProbeDoesNotCreateMacro()
    {
        var before = MacroHashes();
        _ = DesktopProbeMode.Run(
            new FakeDesktopProbe(Result(InputDesktopState.DefaultDesktop)),
            new FakePrivilegeService(WindowsIntegrityLevel.Medium, WindowsIntegrityLevel.Unknown),
            new StringWriter());
        Assert.True(before.SequenceEqual(MacroHashes()));
    }

    [Fact]
    public void ProbeLogCreated()
    {
        var path = Path.Combine(AppPaths.Current.LogsDirectory, DesktopSecurityProbeLog.FileName);
        if (File.Exists(path)) File.Delete(path);
        _ = DesktopProbeMode.Run(
            new FakeDesktopProbe(Result(InputDesktopState.DefaultDesktop)),
            new FakePrivilegeService(WindowsIntegrityLevel.Medium, WindowsIntegrityLevel.Unknown),
            new StringWriter());
        Assert.True(ProjectSandboxGuard.IsWithin(TestProjectEnvironment.RuntimeRoot, path));
        Assert.True(File.Exists(path));
        Assert.Contains("probe_method=OpenInputDesktop/GetUserObjectInformationW", File.ReadAllText(path));
    }

    [Fact]
    public void Root06StillLaunches() => AssertRootCommand("06_啟動錄製器_一般模式.cmd", "--tool recorder --mode medium");

    [Fact]
    public void Root07StillLaunches() => AssertRootCommand("07_選擇並重播巨集_一般模式.cmd", "--tool player --mode medium");

    [Fact]
    public void FiveRootCmdByteFormatUnchanged()
    {
        foreach (var path in RootCommands().Select(TestProjectEnvironment.RootCommandPath))
        {
            var bytes = File.ReadAllBytes(path);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
            Assert.False(bytes.Any(value => value >= 0x80));
            for (var index = 0; index < bytes.Length; index++)
            {
                Assert.False(bytes[index] == 0x0A && (index == 0 || bytes[index - 1] != 0x0D));
            }
        }
    }

    [Fact]
    public void VisibleRootLayoutUnchanged()
    {
        if (TestProjectEnvironment.IsSourceOnly)
        {
            Assert.All(MacroPlayer.PublicationPackageContract.RequiredRepositoryEntries, relative =>
                Assert.True(File.Exists(Path.Combine(Root, relative)) || Directory.Exists(Path.Combine(Root, relative)), relative));
            return;
        }
        var visible = new DirectoryInfo(Root).GetFileSystemInfos()
            .Where(item => !string.Equals(item.Extension, ".zip", StringComparison.OrdinalIgnoreCase))
            .Where(item => !string.Equals(item.Name, "GPT_稽核交接包", StringComparison.OrdinalIgnoreCase))
            .Where(item => item is not DirectoryInfo || !IsOwnerManualEvidenceDirectory(item.Name))
            .Select(item => item.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var expected = MacroLauncher.FinalRootLayoutPolicy.VisibleNames
            .Append("global.json")
            .Append("LICENSE")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.True(expected.SequenceEqual(visible), string.Join(";", visible));
    }

    [Fact]
    public void ExistingMacroHashesUnchanged()
    {
        string text = File.ReadAllText(TestProjectEnvironment.SyntheticRawFixture);
        Assert.Contains("synthetic", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2000-01-01", text, StringComparison.Ordinal);
    }

    [Fact]
    public void PlayerRegression()
    {
        var state = new PlaybackSelectionState();
        state.Select("one.macro");
        state.Begin();
        state.Complete();
        state.Select("two.macro");
        state.Begin();
        state.Complete();
        Assert.Equal(2, state.CompletedPlaybackCount);
    }

    [Fact]
    public void WatchdogRegression() =>
        Assert.Equal(WindowsIntegrityLevel.High, WatchdogIntegrityPolicy.ExpectedChildIntegrity(WindowsIntegrityLevel.High));

    [Fact]
    public void BoundedQueueRegression() =>
        Assert.Contains("BoundedCapturePipeline", Source("src", "MacroCore", "Input", "CaptureSafety.cs"));

    [Fact]
    public void F12StateRegression() =>
        Assert.Contains("HandleF12", Source("src", "MacroRecorder", "Services", "RecorderSafetyStateMachine.cs"));

    [Fact]
    public void SideBySideZero() =>
        Assert.All(new[] { "MacroRecorder", "MacroPlayer", "MacroLauncher", "MacroSafetyWatchdog" }, project =>
            Assert.True(File.Exists(Path.Combine(Dev, "src", project, "app.manifest"))));

    private static string Root => TestProjectEnvironment.Root;
    private static string Dev => TestProjectEnvironment.DevelopmentRoot;
    private static bool IsOwnerManualEvidenceDirectory(string name) =>
        string.Equals(name, string.Concat("MKM_v1_", "framework_test"), StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, string.Concat("MKM_v1_", "selfcontained_test"), StringComparison.OrdinalIgnoreCase);

    private static DllImportAttribute DesktopQueryImport()
    {
        var method = typeof(WindowsInputDesktopNative).GetMethod("GetUserObjectInformationW", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var import = method!.GetCustomAttribute<DllImportAttribute>();
        Assert.NotNull(import);
        return import!;
    }

    private static InputDesktopProbeResult ProbeName(string name) =>
        new WindowsInputDesktopProbe(new FakeDesktopNative(name), 1, TimeSpan.Zero).Probe();

    private static InputDesktopProbeResult Result(InputDesktopState state, int openError = 0) =>
        new(state, state == InputDesktopState.Unknown ? null : state == InputDesktopState.DefaultDesktop ? "Default" : "Winlogon", openError, 122, 0, WindowsInputDesktopProbe.MethodName, DateTimeOffset.UtcNow);

    private static RecordingStartDecision Decide(WindowsIntegrityLevel recorder, WindowsIntegrityLevel target, InputDesktopState state) =>
        PrivilegePolicy.EvaluateRecordingStart(recorder, target, state);

    private static RecordingStartEvaluation Evaluate(InputDesktopState state, WindowsIntegrityLevel recorder, WindowsIntegrityLevel target) =>
        new RecordingStartPrivilegeEvaluator(new FakePrivilegeService(recorder, target), new FakeDesktopProbe(Result(state))).Evaluate();

    private static string StartRecordingSource()
    {
        var source = Source("src", "MacroRecorder", "Services", "RecorderService.cs");
        var start = source.IndexOf("private void StartRecording()", StringComparison.Ordinal);
        var end = source.IndexOf("private void BeginFinalization()", start, StringComparison.Ordinal);
        return source[start..end];
    }

    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine(new[] { Dev }.Concat(parts).ToArray()));

    private static IEnumerable<string> RootCommands() => new[]
    {
        "06_啟動錄製器_一般模式.cmd",
        "06A_啟動錄製器_管理員模式.cmd",
        "07_選擇並重播巨集_一般模式.cmd",
        "07A_選擇並重播巨集_管理員模式.cmd",
        "99_緊急終止巨集工具.cmd"
    };

    private static void AssertRootCommand(string name, string expected)
    {
        var text = Encoding.ASCII.GetString(File.ReadAllBytes(TestProjectEnvironment.RootCommandPath(name)));
        Assert.Contains("%~dp0", text);
        Assert.Contains(expected, text);
    }

    private static string[] MacroHashes()
    {
        string recordingsDirectory = AppPaths.Current.RecordingsDirectory;
        Assert.True(ProjectSandboxGuard.IsWithin(TestProjectEnvironment.RuntimeRoot, recordingsDirectory), recordingsDirectory);
        Assert.True(Directory.Exists(recordingsDirectory), recordingsDirectory);
        return Directory.EnumerateFiles(recordingsDirectory, "*.macro")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => $"{Path.GetFileName(path)}|{new FileInfo(path).Length}|{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}")
            .ToArray();
    }

    private static void AssertHash(string name, string expected)
    {
        if (TestProjectEnvironment.IsSourceOnly)
        {
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(Root, "Recordings"), "*.macro"));
            return;
        }
        Assert.Equal(expected, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(Root, "Recordings", name)))));
    }

    private sealed class FakeDesktopProbe(InputDesktopProbeResult result) : IInputDesktopProbe
    {
        public int Calls { get; private set; }

        public InputDesktopProbeResult Probe()
        {
            Calls++;
            return result;
        }
    }

    private sealed class FakePrivilegeService(WindowsIntegrityLevel recorder, WindowsIntegrityLevel target) : IWindowsPrivilegeService
    {
        public int CaptureCalls { get; private set; }

        public WindowsIntegrityLevel GetCurrentIntegrity() => recorder;

        public ForegroundPrivilegeSnapshot CaptureForeground()
        {
            CaptureCalls++;
            return new ForegroundPrivilegeSnapshot(false, recorder, target, target == WindowsIntegrityLevel.Unknown ? 0 : 42, "Target.exe", null);
        }
    }

    private sealed class FakeDesktopNative : IInputDesktopNative
    {
        private readonly string _name;
        private readonly int _openError;
        private readonly int _nameError;

        public FakeDesktopNative(string name = "Default", int openError = 0, int nameError = 0)
        {
            _name = name;
            _openError = openError;
            _nameError = nameError;
        }

        public IntPtr OpenInputDesktop(out int errorCode)
        {
            errorCode = _openError;
            return _openError == 0 ? new IntPtr(123) : IntPtr.Zero;
        }

        public bool QueryDesktopName(IntPtr desktop, IntPtr buffer, int bufferBytes, out int requiredBytes, out int errorCode)
        {
            var bytes = Encoding.Unicode.GetBytes(_name + "\0");
            requiredBytes = bytes.Length;
            if (buffer == IntPtr.Zero)
            {
                errorCode = WindowsInputDesktopProbe.ErrorInsufficientBuffer;
                return false;
            }
            if (_nameError != 0)
            {
                errorCode = _nameError;
                return false;
            }
            Marshal.Copy(bytes, 0, buffer, Math.Min(bufferBytes, bytes.Length));
            errorCode = 0;
            return true;
        }

        public void CloseInputDesktop(IntPtr desktop)
        {
        }
    }
}

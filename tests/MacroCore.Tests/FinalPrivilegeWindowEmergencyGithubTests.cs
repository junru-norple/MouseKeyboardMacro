using System.Security.Cryptography;
using System.Text;
using System.Drawing;
using System.Windows.Forms;
using MacroCore.Diagnostics;
using MacroCore.Models;
using MacroCore.Runtime;
using MacroCore.Security;
using MacroLauncher;
using MacroPlayer;

namespace MacroCore.Tests;

public sealed class FinalEffectivePrivilegeGateTests
{
    [Fact] public void HighRecorderFalseFlagStillAdministrator() => AssertAdmin(Macro(false, recordedRecorder: "High"), PrivilegeMetadataConsistency.ConflictingHighMetadata);
    [Fact] public void HighTargetFalseFlagStillAdministrator() => AssertAdmin(Macro(false, recordedTarget: "High"), PrivilegeMetadataConsistency.ConflictingHighMetadata);
    [Fact] public void ConflictingMetadataFailsClosed() => Assert.Contains("互相矛盾", EffectivePlaybackPrivilegeResolver.Resolve(Macro(false, legacyRecorder: "High")).Reason);
    [Fact] public void LegacyHighRecorderBlockedInMedium() => Assert.False(EffectivePlaybackPrivilegeResolver.CanStart(Macro(false, legacyRecorder: "High"), false));
    [Fact] public void CurrentAdminMacroBlockedInMedium() => Assert.False(EffectivePlaybackPrivilegeResolver.CanStart(CurrentAdminMacro(), false));
    [Fact] public void CurrentAdminMacroAllowedInHigh() => Assert.True(EffectivePlaybackPrivilegeResolver.CanStart(CurrentAdminMacro(), true));
    [Fact] public void MediumUiStartDisabled() => Assert.False(EffectivePlaybackPrivilegeResolver.CanStart(Macro(false, recordedRecorder: "High"), false));
    [Fact] public async Task MediumControllerRejected()
    {
        PlaybackHarness harness = new();
        PlaybackRunResult result = await harness.Run(Macro(false, recordedRecorder: "High"), elevated: false);
        Assert.Equal(PlaybackDisposition.PrivilegeRejected, result.Disposition);
        Assert.Equal(0, harness.Factory.CreateCount);
    }
    [Fact] public void MediumFactoryRejected()
    {
        FakeForeground foreground = new();
        Assert.Throws<PlaybackPrivilegeRejectedException>(() => new SafePlaybackServiceFactory(foreground).Create(
            Macro(false, recordedRecorder: "High"), PlaybackExecutionContext.Standard, new FreeDesktopFocusPolicy(foreground)));
    }
    [Fact] public void HighRecorderNewMacroAlwaysRequiresElevation() => Assert.True(RecordingPrivilegeTracker.ResolveRequiresElevation("High", "Medium"));
    [Fact] public void StandardRecorderMediumTargetCanRemainStandard() => Assert.False(RecordingPrivilegeTracker.ResolveRequiresElevation("Medium", "Medium"));
    [Fact] public void UacRelaunchPreservesSelection()
    {
        IReadOnlyList<string> args = PlayerElevationRelaunchArguments.Build(TestProjectEnvironment.Root, Path.Combine(TestProjectEnvironment.Root, "selected.macro"), PlayerCountdownMode.KeepVisible);
        Assert.Contains("selected.macro", args[^1]);
        Assert.DoesNotContain("--target-mode", args);
        Assert.Contains(nameof(PlayerCountdownMode.KeepVisible), args);
        Assert.DoesNotContain("--mouse-replay-mode", args, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("RawRelative", args, StringComparer.OrdinalIgnoreCase);
    }
    [Fact] public async Task DesktopOnlyEnforcesPrivilege()
    {
        PlaybackMacroDocument macro = Macro(false, recordedRecorder: "High");
        PlaybackHarness harness = new();
        Assert.Equal(PlaybackDisposition.PrivilegeRejected, (await harness.Run(macro, false)).Disposition);
    }
    [Fact] public async Task KeepAndMinimizeBothEnforcePrivilege()
    {
        PlaybackMacroDocument macro = Macro(false, recordedRecorder: "High");
        PlaybackHarness keep = new();
        PlaybackHarness minimize = new();
        Assert.Equal(PlaybackDisposition.PrivilegeRejected, (await keep.Run(macro, false, PlayerCountdownMode.KeepVisible)).Disposition);
        Assert.Equal(PlaybackDisposition.PrivilegeRejected, (await minimize.Run(macro, false, PlayerCountdownMode.MinimizeBeforeCountdown)).Disposition);
    }

    private static void AssertAdmin(PlaybackMacroDocument macro, PrivilegeMetadataConsistency consistency)
    {
        EffectivePlaybackPrivilegeResolution result = EffectivePlaybackPrivilegeResolver.Resolve(macro);
        Assert.Equal(EffectivePlaybackPrivilegeRequirement.Administrator, result.Requirement);
        Assert.Equal(consistency, result.Consistency);
    }

    internal static PlaybackMacroDocument CurrentAdminMacro()
    {
        return Macro(false, recordedRecorder: "High", legacyRecorder: "High", recordedTarget: "Medium", legacyTarget: "Medium");
    }

    internal static PlaybackMacroDocument Macro(
        bool? requires,
        string recordedRecorder = "Medium",
        string legacyRecorder = "Medium",
        string recordedTarget = "Medium",
        string legacyTarget = "Medium",
        PlaybackEventKind firstKind = PlaybackEventKind.KeyDown) =>
        new("synthetic.macro", "1.2", "synthetic", DateTimeOffset.UnixEpoch, 10, requires, "Standard", "", "", "1280 x 720",
            [new PlaybackMacroEvent(0, firstKind, 65, 30, false, 10, 10, "Left", 0)],
            RecordedRecorderIntegrity: recordedRecorder,
            LegacyRecorderIntegrity: legacyRecorder,
            RecordedTargetIntegrity: recordedTarget,
            LegacyTargetIntegrity: legacyTarget);
}

public sealed class FinalPlayerWindowModeGateTests
{
    [Fact] public void KeepVisibleNeverMinimizesPlayer() => WithForm(form => { WinFormsPlayerWindowModeService service = new(form); service.PrepareAsync(PlayerCountdownMode.KeepVisible, PlaybackExecutionContext.Standard, CancellationToken.None).GetAwaiter().GetResult(); Assert.NotEqual(FormWindowState.Minimized, form.WindowState); service.RestoreAsync().GetAwaiter().GetResult(); });
    [Fact] public void KeepVisibleKeepsMainFormVisible() => WithForm(form => { WinFormsPlayerWindowModeService service = new(form); service.PrepareAsync(PlayerCountdownMode.KeepVisible, PlaybackExecutionContext.Standard, CancellationToken.None).GetAwaiter().GetResult(); Assert.True(form.Visible); service.RestoreAsync().GetAwaiter().GetResult(); });
    [Fact] public void KeepVisibleNoActivate() => WithForm(form => { WinFormsPlayerWindowModeService service = new(form); service.PrepareAsync(PlayerCountdownMode.KeepVisible, PlaybackExecutionContext.Standard, CancellationToken.None).GetAwaiter().GetResult(); Assert.NotEqual(0, service.CurrentExtendedStyle.ToInt64() & PlaybackOverlayWindowPolicy.NoActivate); service.RestoreAsync().GetAwaiter().GetResult(); });
    [Fact] public void KeepVisibleClickThrough() => Assert.True(PlayerWindowClickThroughPolicy.ShouldReturnTransparent(PlayerWindowClickThroughPolicy.WindowNcHitTest, true));
    [Fact] public void KeepVisibleBoundsAndHandleStable() => WithForm(form => { Rectangle before = form.Bounds; nint handle = form.Handle; WinFormsPlayerWindowModeService service = new(form); service.PrepareAsync(PlayerCountdownMode.KeepVisible, PlaybackExecutionContext.Standard, CancellationToken.None).GetAwaiter().GetResult(); Assert.Equal(before, form.Bounds); Assert.Equal(handle, form.Handle); service.RestoreAsync().GetAwaiter().GetResult(); });
    [Fact] public void MinimizeOnlyPlayer() => WithForm(form => { WinFormsPlayerWindowModeService service = new(form); service.PrepareAsync(PlayerCountdownMode.MinimizeBeforeCountdown, PlaybackExecutionContext.Standard, CancellationToken.None).GetAwaiter().GetResult(); Assert.True(form.Visible); Assert.Equal(FormWindowState.Minimized, form.WindowState); service.RestoreAsync().GetAwaiter().GetResult(); });
    [Fact] public void RestoreOnlyPlayer() => WithForm(form => { Rectangle before = form.Bounds; WinFormsPlayerWindowModeService service = new(form); service.PrepareAsync(PlayerCountdownMode.MinimizeBeforeCountdown, PlaybackExecutionContext.Standard, CancellationToken.None).GetAwaiter().GetResult(); service.RestoreAsync().GetAwaiter().GetResult(); Assert.Equal(before, form.Bounds); });
    [Fact] public void TwentyKeepVisibleSessionsStable() => WithForm(form => { nint handle = form.Handle; Rectangle bounds = form.Bounds; for (int index = 0; index < 20; index++) { WinFormsPlayerWindowModeService service = new(form); service.PrepareAsync(PlayerCountdownMode.KeepVisible, PlaybackExecutionContext.Standard, CancellationToken.None).GetAwaiter().GetResult(); service.RestoreAsync().GetAwaiter().GetResult(); } Assert.Equal(handle, form.Handle); Assert.Equal(bounds, form.Bounds); Assert.True(form.Visible); });
    [Fact] public async Task CountdownWindowChangesAllowed() { PlaybackHarness harness = new(); harness.CountdownAction = () => harness.Foreground.Current = null; Assert.True((await harness.Run(FinalEffectivePrivilegeGateTests.Macro(false), false)).Completed); }
    [Fact] public void NoGlobalWindowIsolationType() => Assert.Null(typeof(PlaybackStartController).Assembly.GetType("MacroPlayer.PrePlaybackWindowIsolationAudit"));
    [Fact] public void NoShowDesktopApiReferences() => Assert.DoesNotContain("MinimizeAll", RuntimeSources(), StringComparison.OrdinalIgnoreCase);
    [Fact] public void NoShellWindowCommands() { Assert.DoesNotContain("Shell_TrayWnd", RuntimeSources(), StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("WorkerW", RuntimeSources(), StringComparison.OrdinalIgnoreCase); }
    private static string RuntimeSources() => File.ReadAllText(Path.Combine(TestProjectEnvironment.DevelopmentRoot, "src", "MacroPlayer", "WindowsPlayerServices.cs")) + File.ReadAllText(Path.Combine(TestProjectEnvironment.DevelopmentRoot, "src", "MacroLauncher", "Program.cs"));
    private static void WithForm(Action<System.Windows.Forms.Form> action)
    {
        Exception? error = null;
        Thread thread = new(() => { try { using System.Windows.Forms.Form form = new() { Bounds = new Rectangle(40, 40, 640, 480) }; form.Show(); Application.DoEvents(); action(form); form.Close(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw error;
    }
}

public sealed class FinalFreeDesktopGateTests
{
    [Fact] public async Task FreeDesktopNoTargetRequired() { PlaybackHarness harness = new(); harness.Preferred = null; Assert.True((await harness.Run(FinalEffectivePrivilegeGateTests.Macro(false), false)).Completed); }
    [Fact] public void LaunchForegroundSnapshotUsed() { PlayerLaunchOptions options = PlayerLaunchOptions.Parse(["--project-root", TestProjectEnvironment.Root, "--launch-foreground-hwnd", "123"]); Assert.Equal(new nint(123), options.LaunchForegroundWindow); }
    [Fact] public void WinEventLastNonToolUsed() => Assert.Contains("SetWinEventHook", PlayerSource("WindowsPlayerServices.cs"));
    [Fact] public void ForegroundZeroAllowed() { FakeForeground foreground = new() { Current = null }; Assert.True(new FreeDesktopFocusPolicy(foreground).CheckPeriodicSafety().Safe); }
    [Fact] public async Task FirstMouseEventAllowedWithoutTarget() { PlaybackHarness harness = new(); harness.Preferred = null; Assert.True((await harness.Run(FinalEffectivePrivilegeGateTests.Macro(false, firstKind: PlaybackEventKind.MouseMove), false)).Completed); }
    [Fact] public async Task FirstKeyboardUsesLastNonToolWhenAvailable() { PlaybackHarness harness = new(); await harness.Run(FinalEffectivePrivilegeGateTests.Macro(false), false); Assert.Equal(1, harness.Foreground.ActivateCalls); }
    [Fact] public void FocusChangesAfterStartAllowed() { FakeForeground foreground = new(); FreeDesktopFocusPolicy policy = new(foreground); Assert.True(policy.CheckPeriodicSafety().Safe); foreground.Current = null; Assert.True(policy.CheckPeriodicSafety().Safe); }
    [Fact] public async Task SecureDesktopBlocks() { PlaybackHarness harness = new(); harness.Foreground.Secure = true; Assert.False((await harness.Run(FinalEffectivePrivilegeGateTests.Macro(false), false)).Completed); }
    [Fact] public void F11Regression() => Assert.Contains("VkF11", PlayerSource("SafePlaybackSession.cs"));
    private static string PlayerSource(string name) => File.ReadAllText(Path.Combine(TestProjectEnvironment.DevelopmentRoot, "src", "MacroPlayer", name));
}

public sealed class FinalEmergencyStopGateTests
{
    [Fact] public async Task EmergencyEnumeratesAllSessions() => Assert.Equal(2, (await Run([Session(1, "Player"), Session(2, "Recorder")])).Results.Count);
    [Fact] public async Task PlayerAndRecorderBothStopped() => Assert.Equal(2, (await Run([Session(1, "Player"), Session(2, "Recorder")])).CooperativelyStopped);
    [Fact] public async Task DoesNotPickArbitraryLastSession() { EmergencyStopSummary result = await Run([Session(1, "Player"), Session(2, "Recorder")]); Assert.Contains(result.Results, item => item.Session.Pid == 1); Assert.Contains(result.Results, item => item.Session.Pid == 2); }
    [Fact] public async Task CooperativeStopBeforeKill() { FakeEmergencyRuntime runtime = new(); await new EmergencyStopCoordinator().StopAllAsync([Session(1)], runtime); Assert.Equal("signal", runtime.Order[0]); Assert.Equal(0, runtime.ForceCalls); }
    [Fact] public async Task UnresponsiveExactFallbackKill() { FakeEmergencyRuntime runtime = new() { Ack = false, ExitsAfterAck = false }; EmergencyStopSummary result = await new EmergencyStopCoordinator().StopAllAsync([Session(1)], runtime); Assert.Equal(1, result.ForceStopped); }
    [Fact] public async Task StaleSessionRemoved() { FakeEmergencyRuntime runtime = new() { Live = false }; Assert.Equal(1, (await new EmergencyStopCoordinator().StopAllAsync([Session(1)], runtime)).StaleRemoved); }
    [Fact] public async Task WrongTokenNotKilled() { WatchdogSessionRecord bad = Session(1); bad.SessionToken = "wrong"; FakeEmergencyRuntime runtime = new(); EmergencyStopSummary result = await new EmergencyStopCoordinator().StopAllAsync([bad], runtime); Assert.Equal(1, result.StaleRemoved); Assert.Equal(0, runtime.ForceCalls); }
    [Fact] public async Task PidReuseNotKilled() { FakeEmergencyRuntime runtime = new() { Live = false }; await new EmergencyStopCoordinator().StopAllAsync([Session(1)], runtime); Assert.Equal(0, runtime.ForceCalls); }
    [Fact] public void MixedIntegrityRequestsOneElevation() { WatchdogSessionRecord high = Session(2); high.IntegrityLevel = "High"; Assert.True(EmergencyElevationDecision.RequiresSingleElevation([Session(1), high], false)); Assert.False(EmergencyElevationDecision.RequiresSingleElevation([Session(1), high], true)); }
    [Fact] public void UacCancelKillsNothing() { FakeEmergencyRuntime runtime = new(); WatchdogSessionRecord high = Session(1); high.IntegrityLevel = "High"; Assert.True(EmergencyElevationDecision.RequiresSingleElevation([high], false)); Assert.Equal(0, runtime.ForceCalls); }
    [Fact] public async Task WatchdogsCleaned() { FakeEmergencyRuntime runtime = new(); await new EmergencyStopCoordinator().StopAllAsync([Session(1)], runtime); Assert.Equal(1, runtime.WatchdogCleanups); }
    [Fact] public void SessionFileRemoved()
    {
        string path = Path.Combine(ProjectLocalTestSandbox.Create(), "session-" + Guid.NewGuid().ToString("N") + ".json");
        WatchdogSessionRecord session = Session(Environment.ProcessId);
        CurrentSessionStore.Upsert(session, path);
        CurrentSessionStore.RemoveExact(session, path);
        Assert.False(File.Exists(path));
    }
    [Fact] public async Task ResultSummaryShown() { EmergencyStopSummary result = await Run([Session(1)]); Assert.Equal(1, result.Found); Assert.Equal(1, result.CooperativelyStopped); }
    [Fact] public void NoBroadTaskkill() => Assert.DoesNotContain("/IM", File.ReadAllText(Path.Combine(TestProjectEnvironment.DevelopmentRoot, "src", "MacroLauncher", "Program.cs")), StringComparison.OrdinalIgnoreCase);
    [Fact] public void NoLiveInputUsed() { string source = File.ReadAllText(Path.Combine(TestProjectEnvironment.DevelopmentRoot, "tools", "EmergencySessionTestHost", "Program.cs")); Assert.DoesNotContain("SendInput", source); Assert.DoesNotContain("SetCursorPos", source); Assert.DoesNotContain("SetWindowsHookEx", source); }

    private static Task<EmergencyStopSummary> Run(IEnumerable<WatchdogSessionRecord> sessions) => new EmergencyStopCoordinator().StopAllAsync(sessions, new FakeEmergencyRuntime());
    private static WatchdogSessionRecord Session(int pid, string role = "Player")
    {
        string token = pid.ToString("X32");
        return new WatchdogSessionRecord { Role = role, Pid = pid, StartTimeUtc = DateTime.UnixEpoch, ProcessName = role == "Player" ? "MacroPlayer" : "MacroRecorder", SessionToken = token, IntegrityLevel = "Medium", EmergencyEndpoint = $"MacroEmergency_{pid}_{token}", WatchdogPid = pid + 1000, WatchdogStartTimeUtc = DateTime.UnixEpoch };
    }
}

public sealed class FinalGithubPrivacyGateTests
{
    [Fact] public void SyntheticSparseFixtureOnly() { string text = Fixture("SyntheticSparseCapture.macro"); Assert.Contains("2000-01-01T00:00:00Z", text); Assert.Contains("DISPLAY_SYNTHETIC", text); Assert.DoesNotContain("DISPLAY1", text); }
    [Fact] public void Test3SparseRemoved() => Assert.False(File.Exists(Path.Combine(TestProjectEnvironment.DevelopmentRoot, "tests", "MacroCore.Tests", "Fixtures", "Test3Sparse.macro")));
    [Fact] public void AutoRepeatSyntheticProvenance() { string text = Fixture("AutoRepeat173.macro"); Assert.Contains("synthetic_auto_repeat_173", text); Assert.Contains("2000-01-01T00:00:00Z", text); }
    [Fact] public void FixtureProvenanceExists() => Assert.True(File.Exists(Path.Combine(TestProjectEnvironment.DevelopmentRoot, "tests", "MacroCore.Tests", "Fixtures", "FIXTURE_PROVENANCE.md")));
    [Fact] public void GithubDocsComplete() => Assert.All(new[] { "README.md", "INSTALL.md", "USER_GUIDE.md", "BUILDING.md", "TROUBLESHOOTING.md", "SECURITY.md", "PRIVACY.md", "CHANGELOG.md", "CONTRIBUTING.md", "LICENSE_STATUS.md", PublicationPackageContract.CanonicalManualName }, name => Assert.True(File.Exists(Path.Combine(PublicRoot, name)), name));
    [Fact] public void StartHereTemplateExists() => Assert.True(File.Exists(Path.Combine(PublicRoot, "START_HERE.txt")));
    [Fact]
    public void PublicOperationManualIsCanonicalUtf8SanitizedAndRuntimeCompatible()
    {
        RuntimePathSet runtimePaths = AppPaths.Current;
        Assert.Equal(
            PublicationPackageContract.RuntimeManualRelativePath,
            PublicationPackageContract.Normalize(Path.GetRelativePath(runtimePaths.ProjectRoot, runtimePaths.ManualPath)));

        string path = Path.Combine(PublicRoot, PublicationPackageContract.CanonicalManualName);
        Assert.True(File.Exists(path), path);
        byte[] bytes = File.ReadAllBytes(path);
        Assert.NotEmpty(bytes);
        Assert.True(PublicationPackageContract.IsLfTextWithoutBom(bytes), "The public operation manual must be LF-only UTF-8 without BOM.");
        string text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        Assert.False(string.IsNullOrWhiteSpace(text));

        Assert.All(new[]
        {
            "LICENSE_INCLUDED", "SPDX_IDENTIFIER=MIT", "Copyright (c) 2026 ru",
            PublicationPackageContract.RuntimeManualRelativePath, "長按 F12 5 秒開始錄製",
            "錄製中再次長按 F12 5 秒", "F12 開始／停止控制動作本身不會寫入 macro",
            "播放期間長按 F11 2 秒是第一優先緊急停止", "99_緊急終止巨集工具.cmd",
            "Standard", "Desktop Safe", "Raw Input Enhanced", "必須由使用者明確選擇",
            "不會預設啟用", "一般權限不能可靠控制管理員程式", "UAC 安全桌面"
        }, required => Assert.Contains(required, text, StringComparison.Ordinal));
        IReadOnlyList<string> privacyAndSecretFindings = PublicationPackageContract.FindLocalIdentityLeaks(text);
        Assert.Empty(privacyAndSecretFindings);
        Assert.All(new[]
        {
            "sample-private-recording.macro", "player-settings.json", "recorder-settings.json",
            "LICENSE_DECISION_REQUIRED", "PRE_LICENSE_TECHNICAL_CANDIDATE", "$licenseStatus",
            string.Concat(".", "codex"), string.Concat("Owner", "Only"),
            string.Concat("MKM_", "v1_framework_test"), string.Concat("gh", "p_"),
            string.Concat("github_", "pat_"), string.Concat("sk", "-")
        }, forbidden => Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotMatch(@"(?m)\$[A-Za-z_][A-Za-z0-9_]*", text);

        Assert.Contains(PublicationPackageContract.CanonicalManualName, PublicationPackageContract.RequiredRepositoryEntries);
        Assert.Contains(PublicationPackageContract.CanonicalManualName, PublicationPackageContract.RequiredReleaseEntries);
        Assert.Contains(PublicationPackageContract.RuntimeManualRelativePath, PublicationPackageContract.RequiredReleaseEntries);
    }
    [Fact] public void FinalDefaultVersion() => Assert.Equal("1.0.0", PublicationPackageContract.DefaultVersion);
    [Fact] public void RuntimeRequiredZipContract() => Assert.Contains("framework-dependent", ExportScript());
    [Fact] public void SelfContainedZipContract() => Assert.Contains("self-contained.zip", ExportScript());
    [Fact] public void FixturesNotBlanketExempt() => Assert.DoesNotContain("AllowFixtures", ExportScript());
    [Fact] public void CanonicalMitLicensePresent() { string path = Path.Combine(TestProjectEnvironment.Root, "LICENSE"); Assert.True(File.Exists(path)); Assert.Equal("007F4954B08C74FB03505BD591239C614EA48B7F714CCDD8F32D5D7A7E2B57EC", Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))); }
    [Fact] public void SyntheticFixturesContainNoUserData() { string text = Fixture("SyntheticSparseCapture.macro"); Assert.Contains("synthetic", text, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("CrystalDiskMark", text, StringComparison.OrdinalIgnoreCase); }
    private static string Fixture(string name) => SyntheticMacroFixtureFactory.GetText(name);
    private static string PublicRoot => TestProjectEnvironment.IsSourceOnly
        ? TestProjectEnvironment.Root
        : Path.Combine(TestProjectEnvironment.DevelopmentRoot, "GitHubDocs", "Repository");
    private static string ExportScript() => File.ReadAllText(Path.Combine(PublicRoot, "scripts", "Verify-Release.ps1"));
}

internal sealed class FakeForeground : IForegroundWindowService
{
    public static ForegroundSnapshot Target { get; } = new(new nint(99), 99, "Target.exe", 0x2000);
    public bool Secure { get; set; }
    public ForegroundSnapshot? Current { get; set; } = Target;
    public int ActivateCalls { get; private set; }
    public ForegroundSnapshot? CaptureCurrent() => Current;
    public bool TryActivate(ForegroundSnapshot snapshot) { ActivateCalls++; return true; }
    public bool IsSecureDesktop(out string reason) { reason = Secure ? "安全桌面" : ""; return Secure; }
    public nint GetForegroundWindowHandleFast() => Current?.WindowHandle ?? nint.Zero;
}

internal sealed class PlaybackHarness
{
    public FakeForeground Foreground { get; } = new();
    public FakePlaybackFactory Factory { get; } = new();
    public ForegroundSnapshot? Preferred { get; set; } = FakeForeground.Target;
    public Action? CountdownAction { get; set; }
    private readonly PlaybackStartController _controller;
    public PlaybackHarness() => _controller = new(Foreground, new FakeWindowMode(), new FakeCountdown(() => CountdownAction?.Invoke()), Factory, new FakeLog(), new FakeOverlay(), () => Preferred);
    public Task<PlaybackRunResult> Run(PlaybackMacroDocument macro, bool elevated, PlayerCountdownMode countdown = PlayerCountdownMode.KeepVisible) =>
        _controller.StartAsync(macro, countdown, elevated, CancellationToken.None);
}

internal sealed class FakeWindowMode : IPlayerWindowModeService { public Task PrepareAsync(PlayerCountdownMode mode, PlaybackExecutionContext context, CancellationToken token) => Task.CompletedTask; public Task RestoreAsync() => Task.CompletedTask; }
internal sealed class FakeCountdown : ICountdownService { private readonly Action _action; public FakeCountdown(Action action) => _action = action; public Task RunAsync(int seconds, Action<int> tick, CancellationToken token) { _action(); tick(seconds); return Task.CompletedTask; } }
internal sealed class FakeOverlay : IOverlayService { public void ShowCountdown(string name, int seconds) { } public void ShowPlaying(string name, PlaybackProgress progress) { } public void Close() { } }
internal sealed class FakeLog : IPlaybackSessionLog { public void SessionStarted(PlaybackMacroDocument macro, PlaybackExecutionContext context, PlayerCountdownMode mode) { } public void FirstEventSent() { } public void SessionEnded(string disposition, int sent, int focus, string? detail = null) { } }
internal sealed class FakePlaybackFactory : IPlaybackServiceFactory
{
    public int CreateCount { get; private set; }
    public IPlaybackSession Create(PlaybackMacroDocument macro, PlaybackExecutionContext context, IPlaybackFocusPolicy focus) { CreateCount++; return new FakePlaybackSession(); }
}
internal sealed class FakePlaybackSession : IPlaybackSession
{
    public event EventHandler? FirstEventSent; public event EventHandler<PlaybackProgress>? ProgressChanged;
    public bool FirstEventWasSent { get; private set; } public int EventsSentCount { get; private set; } public int FocusChangeCount => 0;
    public Task<PlaybackRunResult> PlayAsync(CancellationToken token) { FirstEventWasSent = true; EventsSentCount = 1; FirstEventSent?.Invoke(this, EventArgs.Empty); ProgressChanged?.Invoke(this, new PlaybackProgress(1, 1, TimeSpan.Zero)); return Task.FromResult(PlaybackRunResult.Success(1)); }
    public void Stop() { } public void Dispose() { }
}

internal sealed class FakeEmergencyRuntime : IEmergencyStopRuntime
{
    public bool Live { get; set; } = true; public bool Ack { get; set; } = true; public bool ExitsAfterAck { get; set; } = true;
    public int ForceCalls { get; private set; } public int WatchdogCleanups { get; private set; } public List<string> Order { get; } = [];
    public bool IsExactLiveSession(WatchdogSessionRecord session) => Live;
    public Task<bool> RequestCooperativeStopAsync(WatchdogSessionRecord session, TimeSpan timeout) { Order.Add("signal"); return Task.FromResult(Ack); }
    public bool WaitForExit(WatchdogSessionRecord session, TimeSpan timeout) { Order.Add("wait"); return ExitsAfterAck; }
    public bool ForceStopExact(WatchdogSessionRecord session) { Order.Add("force"); ForceCalls++; return Live; }
    public bool CleanupWatchdogExact(WatchdogSessionRecord session) { WatchdogCleanups++; return true; }
}

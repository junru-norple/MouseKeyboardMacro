using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using MacroCore.Diagnostics;
using MacroCore.Input;
using MacroCore.Models;
using MacroCore.Security;
using MacroLauncher;
using MacroPlayer;
using Xunit;

namespace MacroRecorder.Tests;

public sealed class DesktopOnlyRemovalGateTests
{
    [Fact] public void PlayerUiHasNoLockedWindowOption() => PlayerHarness.WithForm(form => Assert.False(form.CoreControls.ContainsKey("LockedWindowMode")));
    [Fact] public void PlayerUiHasNoTargetList() => PlayerHarness.WithForm(form => Assert.False(form.CoreControls.ContainsKey("TargetList")));
    [Fact, Trait("GateType", "STATIC_ONLY")]
    public void PlayerLayoutProbeDoesNotRequireRemovedTargetList()
    {
        FieldInfo requiredNames = typeof(PlayerLayoutProbe).GetField("RequiredNames", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new Xunit.Sdk.XunitException("PlayerLayoutProbe.RequiredNames was not found.");
        string[] required = Assert.IsType<string[]>(requiredNames.GetValue(null));
        Assert.DoesNotContain("TargetList", required);
    }
    [Fact] public void PlayerUiHasNoRefreshTarget() => PlayerHarness.WithForm(form => Assert.DoesNotContain(Descendants(form), control => control.Name == "RefreshTargets"));
    [Fact] public void PlayerUiShowsDesktopOnlyText() => PlayerHarness.WithForm(form => Assert.Contains("直接重播於目前桌面", form.CoreControls["DesktopScopeTitle"].Text));
    [Fact, Trait("GateType", "STATIC_ONLY")] public void ProductionHasNoTargetResolver() => Assert.Null(PlayerAssembly.GetType("MacroPlayer.TargetWindowResolver"));
    [Fact, Trait("GateType", "STATIC_ONLY")] public void ProductionHasNoLockedWindowFocusPolicy() => Assert.Null(PlayerAssembly.GetType("MacroPlayer.LockedWindowFocusPolicy"));
    [Fact, Trait("GateType", "STATIC_ONLY")] public void ProductionHasNoTargetLostDispositionPath() => Assert.DoesNotContain("TargetLost", Enum.GetNames<PlaybackDisposition>());
    [Fact] public void PlayerSettingsHasNoTargetMode() => Assert.Null(typeof(PlayerSettings).GetProperty("TargetMode"));
    [Fact] public void LegacyTargetModeSettingsMigrated()
    {
        string root = Path.Combine(ProjectLocalTestSandbox.Create(), "legacy-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "player-settings.json");
        File.WriteAllText(path, "{\"SettingsVersion\":2,\"CountdownMode\":0,\"TargetMode\":1,\"MouseReplayMode\":0}", Encoding.UTF8);
        PlayerSettingsStore store = new(path, _ => { });
        Assert.Equal(PlayerCountdownMode.KeepVisible, store.LoadValue().CountdownMode);
        string migrated = File.ReadAllText(path);
        Assert.DoesNotContain("TargetMode", migrated);
        Assert.DoesNotContain("MouseReplayMode", migrated);
        Directory.Delete(root, true);
    }
    [Fact] public void OldTargetModeCliIgnoredSafely()
    {
        PlayerLaunchOptions options = PlayerLaunchOptions.Parse(["--project-root", TestProjectEnvironment.Root, "--target-mode", "LockedWindow"]);
        Assert.True(options.LegacyTargetModeIgnored);
    }
    [Fact] public void ElevatedRelaunchHasNoTargetMode() => Assert.DoesNotContain("--target-mode", PlayerElevationRelaunchArguments.Build(TestProjectEnvironment.Root, null, PlayerCountdownMode.KeepVisible));
    [Fact, Trait("GateType", "STATIC_ONLY")] public void DocsContainNoLockedWindowInstructions()
    {
        string docs = TestProjectEnvironment.IsSourceOnly
            ? File.ReadAllText(Path.Combine(TestProjectEnvironment.Root, "USER_GUIDE.md")) + File.ReadAllText(Path.Combine(TestProjectEnvironment.Root, "SECURITY.md"))
            : File.ReadAllText(Path.Combine(TestProjectEnvironment.Root, "Program", "Docs", "README_操作手冊.txt")) +
              File.ReadAllText(Path.Combine(TestProjectEnvironment.Root, "Program", "Docs", "MANUAL_VALIDATION_CHECKLIST.txt"));
        Assert.DoesNotContain("鎖定指定視窗", docs);
        Assert.DoesNotContain("target lost", docs, StringComparison.OrdinalIgnoreCase);
    }

    private static Assembly PlayerAssembly => typeof(PlaybackStartController).Assembly;
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child)) yield return nested;
        }
    }
}

public sealed class TrueKeepVisibleGateTests
{
    [Fact] public void KeepVisibleMainFormVisible() => WithPrepared((form, _) => Assert.True(form.Visible));
    [Fact] public void KeepVisibleMainFormNotHidden() => WithPrepared((form, _) => Assert.True(form.Visible && form.IsHandleCreated));
    [Fact] public void KeepVisibleMainFormNotMinimized() => WithPrepared((form, _) => Assert.NotEqual(FormWindowState.Minimized, form.WindowState));
    [Fact] public void KeepVisibleBoundsUnchanged() => WithPrepared((form, service) => Assert.Equal(service.OriginalBoundsForTests, form.Bounds));
    [Fact] public void KeepVisibleHandleUnchanged() => WithPrepared((form, service) => Assert.Equal(service.CapturedHandle, form.Handle));
    [Fact] public void KeepVisibleNoActivateStyle() => WithPrepared((_, service) => Assert.NotEqual(0, service.CurrentExtendedStyle.ToInt64() & PlaybackOverlayWindowPolicy.NoActivate));
    [Fact] public void KeepVisibleClickThrough() => WithPrepared((_, service) => Assert.True(PlayerWindowClickThroughPolicy.ShouldReturnTransparent(PlayerWindowClickThroughPolicy.WindowNcHitTest, service.ClickThroughActive)));
    [Fact] public void KeepVisibleControlsDisabled() => WithPrepared((form, _) => Assert.True(form.Enabled));
    [Fact] public void KeepVisibleProgressUpdatesMainForm() => PlayerHarness.WithForm(form => { form.ApplyProgressForProbe(new PlaybackProgress(4, 10, TimeSpan.FromSeconds(1))); Assert.Contains("4 / 10", form.CoreControls["Status"].Text); });
    [Fact] public void KeepVisibleRestoresStyles() => PlayerHarness.WithPlainForm(form => { WinFormsPlayerWindowModeService service = new(form); nint style = service.CurrentExtendedStyle; service.PrepareAsync(PlayerCountdownMode.KeepVisible, PlaybackExecutionContext.Standard, CancellationToken.None).GetAwaiter().GetResult(); service.RestoreAsync().GetAwaiter().GetResult(); Assert.Equal(style, service.CurrentExtendedStyle); Assert.True(form.Enabled); });
    [Fact] public void KeepVisibleTwentySessionsStable() => PlayerHarness.WithPlainForm(form => { Rectangle bounds = form.Bounds; nint handle = form.Handle; for (int i = 0; i < 20; i++) { WinFormsPlayerWindowModeService service = new(form); service.PrepareAsync(PlayerCountdownMode.KeepVisible, PlaybackExecutionContext.Standard, CancellationToken.None).GetAwaiter().GetResult(); service.RestoreAsync().GetAwaiter().GetResult(); } Assert.Equal(bounds, form.Bounds); Assert.Equal(handle, form.Handle); });
    [Fact] public void KeepVisibleDoesNotTouchOtherWindows() => PlayerHarness.WithPlainForm(form => { RecordingWindowNativeApi native = new(); WinFormsPlayerWindowModeService service = new(form, native); service.PrepareAsync(PlayerCountdownMode.KeepVisible, PlaybackExecutionContext.Standard, CancellationToken.None).GetAwaiter().GetResult(); service.RestoreAsync().GetAwaiter().GetResult(); Assert.All(native.Handles, handle => Assert.Equal(form.Handle, handle)); });

    private static void WithPrepared(Action<Form, WinFormsPlayerWindowModeService> assertion) => PlayerHarness.WithPlainForm(form =>
    {
        WinFormsPlayerWindowModeService service = new(form);
        service.PrepareAsync(PlayerCountdownMode.KeepVisible, PlaybackExecutionContext.Standard, CancellationToken.None).GetAwaiter().GetResult();
        assertion(form, service);
        service.RestoreAsync().GetAwaiter().GetResult();
    });
}

public sealed class PlayerOnlyMinimizeGateTests
{
    [Fact] public void MinimizeOnlyPlayer() => PlayerHarness.WithPlainForm(form => { WinFormsPlayerWindowModeService service = new(form); service.PrepareAsync(PlayerCountdownMode.MinimizeBeforeCountdown, PlaybackExecutionContext.Standard, CancellationToken.None).GetAwaiter().GetResult(); Assert.Equal(FormWindowState.Minimized, form.WindowState); service.RestoreAsync().GetAwaiter().GetResult(); });
    [Fact] public void MinimizeNeverHidesPlayer() => PlayerHarness.WithPlainForm(form => { WinFormsPlayerWindowModeService service = new(form); service.PrepareAsync(PlayerCountdownMode.MinimizeBeforeCountdown, PlaybackExecutionContext.Standard, CancellationToken.None).GetAwaiter().GetResult(); Assert.True(form.Visible); service.RestoreAsync().GetAwaiter().GetResult(); });
    [Fact] public void MinimizeRestoresOnlyPlayer() => PlayerHarness.WithPlainForm(form => { Rectangle before = form.Bounds; WinFormsPlayerWindowModeService service = new(form); service.PrepareAsync(PlayerCountdownMode.MinimizeBeforeCountdown, PlaybackExecutionContext.Standard, CancellationToken.None).GetAwaiter().GetResult(); service.RestoreAsync().GetAwaiter().GetResult(); Assert.Equal(before, form.Bounds); });
    [Fact, Trait("GateType", "STATIC_ONLY")] public void MinimizeDoesNotEnumerateOtherWindows() => Assert.DoesNotContain(typeof(WindowsForegroundWindowService).GetMethods(), method => method.Name.Contains("Enumerate", StringComparison.Ordinal));
    [Fact, Trait("GateType", "STATIC_ONLY")] public void MinimizeDoesNotCallShowDesktop() => Assert.DoesNotContain("ShowDesktop", PlayerSource("WindowsPlayerServices.cs"), StringComparison.OrdinalIgnoreCase);
    private static string PlayerSource(string name) => File.ReadAllText(Path.Combine(TestProjectEnvironment.DevelopmentRoot, "src", "MacroPlayer", name));
}

public sealed class CountdownFreedomGateTests
{
    [Fact] public async Task UserMinimizesOtherWindowDuringCountdownStillStarts() => Assert.True((await RunWithCountdownChange(() => { })).Completed);
    [Fact] public async Task UserClosesWindowDuringCountdownStillStarts() => Assert.True((await RunWithCountdownChange(() => { })).Completed);
    [Fact] public async Task UserOpensWindowDuringCountdownStillStarts() => Assert.True((await RunWithCountdownChange(() => { })).Completed);
    [Fact] public async Task UserChangesForegroundDuringCountdownStillStarts() => Assert.True((await RunWithCountdownChange(() => { })).Completed);
    [Fact, Trait("GateType", "STATIC_ONLY")] public void NoPreFirstEventIsolationCancellation() => Assert.Null(typeof(PlaybackStartController).Assembly.GetType("MacroPlayer.PrePlaybackWindowIsolationAudit"));
    [Fact, Trait("GateType", "STATIC_ONLY")] public void RemovedErrorMessageAbsent() => Assert.DoesNotContain("首事件前被最小化", File.ReadAllText(Path.Combine(TestProjectEnvironment.DevelopmentRoot, "src", "MacroPlayer", "PlaybackWorkflow.cs")));
    [Fact] public async Task FirstEventSentAfterWindowChanges() { Fixture fixture = Fixture.Create(); fixture.Countdown.OnEnter = () => fixture.Foreground.Current = Fixture.OtherTarget(); PlaybackRunResult result = await fixture.Run(PlayerCountdownMode.KeepVisible); Assert.True(result.Completed); Assert.Equal(1, fixture.Log.FirstCount); }
    private static async Task<PlaybackRunResult> RunWithCountdownChange(Action change) { Fixture fixture = Fixture.Create(); fixture.Countdown.OnEnter = change; fixture.Countdown.ChangeForegroundBeforeFinal = true; return await fixture.Run(PlayerCountdownMode.KeepVisible); }
}

public sealed class DesktopFocusGateTests
{
    [Fact] public async Task NoTargetRequired() { Fixture fixture = Fixture.Create(); fixture.PreferredForeground = null; Assert.True((await fixture.Run(PlayerCountdownMode.KeepVisible)).Completed); }
    [Fact, Trait("GateType", "STATIC_ONLY")] public void NoFinalTargetCheck() => Assert.DoesNotContain(typeof(ICountdownService).GetMethod(nameof(ICountdownService.RunAsync))!.GetParameters(), parameter => parameter.ParameterType.Name.Contains("Func", StringComparison.Ordinal));
    [Fact, Trait("GateType", "STATIC_ONLY")] public void NoPerEventForegroundLock() => Assert.Null(typeof(IPlaybackFocusPolicy).GetMethod("ValidateBeforeEvent"));
    [Fact] public void ForegroundZeroAllowed() { FakeForeground foreground = new() { Current = null }; Assert.True(new FreeDesktopFocusPolicy(foreground).CheckPeriodicSafety().Safe); }
    [Fact] public async Task ExplorerForegroundAllowed() { Fixture fixture = Fixture.Create(); fixture.PreferredForeground = new ForegroundSnapshot(new nint(44), 44, "explorer.exe", 0x2000); Assert.True((await fixture.Run(PlayerCountdownMode.KeepVisible)).Completed); }
    [Fact] public async Task FirstMouseEventStarts() { Fixture fixture = Fixture.Create(); fixture.PreferredForeground = null; Assert.True((await fixture.Run(PlayerCountdownMode.KeepVisible, macro: Fixture.Macro(firstKind: PlaybackEventKind.MouseMove))).Completed); }
    [Fact] public async Task FirstKeyboardEventUsesLastNonToolWhenAvailable() { Fixture fixture = Fixture.Create(); Assert.True((await fixture.Run(PlayerCountdownMode.KeepVisible)).Completed); Assert.Equal(1, fixture.Foreground.ActivateCalls); }
    [Fact] public async Task FirstKeyboardNoLastWindowStillDoesNotBlock() { Fixture fixture = Fixture.Create(); fixture.PreferredForeground = null; Assert.True((await fixture.Run(PlayerCountdownMode.KeepVisible)).Completed); }
    [Fact] public void FocusChangesDuringPlaybackAllowed() { FakeForeground foreground = new(); FreeDesktopFocusPolicy policy = new(foreground); Assert.True(policy.CheckPeriodicSafety().Safe); foreground.Current = Fixture.OtherTarget(); Assert.True(policy.CheckPeriodicSafety().Safe); }
    [Fact] public async Task SecureDesktopStillStops() { Fixture fixture = Fixture.Create(); fixture.Foreground.SecureDesktop = true; Assert.Equal(PlaybackDisposition.SecureDesktop, (await fixture.Run(PlayerCountdownMode.KeepVisible)).Disposition); }
}

public sealed class ProductionPrivilegeGateTests
{
    [Fact] public void CurrentAdminMacroMediumProbeBlocked() => Assert.Equal(PlayerPrivilegeProbeResult.BlockedAdministratorRequired, PlayerPrivilegeGateProbe.Evaluate(AdminMacroPath, false));
    [Fact] public void CurrentAdminMacroUiDisabled() { PlaybackMacroDocument macro = PlaybackMacroDocument.Load(AdminMacroPath); PlayerPrivilegeUiDecision ui = PlayerPrivilegeUiPolicy.Resolve(macro, false); Assert.False(ui.StartEnabled); Assert.True(ui.ElevateVisible); }
    [Fact] public async Task CurrentAdminMacroControllerBlocked() { Fixture fixture = Fixture.Create(); Assert.Equal(PlaybackDisposition.PrivilegeRejected, (await fixture.Run(PlayerCountdownMode.KeepVisible, macro: PlaybackMacroDocument.Load(AdminMacroPath))).Disposition); Assert.Equal(0, fixture.Factory.CreateCount); }
    [Fact] public void CurrentAdminMacroFactoryBlocked() { FakeForeground foreground = new(); Assert.Throws<PlaybackPrivilegeRejectedException>(() => new SafePlaybackServiceFactory(foreground).Create(PlaybackMacroDocument.Load(AdminMacroPath), PlaybackExecutionContext.Standard, new FreeDesktopFocusPolicy(foreground))); }
    [Fact] public void OrdinaryMacroMediumAllowed() => Assert.Equal(PlayerPrivilegeProbeResult.Allowed, PlayerPrivilegeGateProbe.Evaluate(OrdinaryMacroPath, false));
    [Fact] public void AdminMacroHighAllowedAbstraction() => Assert.Equal(PlayerPrivilegeProbeResult.Allowed, PlayerPrivilegeGateProbe.Evaluate(AdminMacroPath, true));
    [Fact] public void AdminRecorderNewMacroRequiresElevation() => Assert.True(RecordingPrivilegeTracker.ResolveRequiresElevation("High", "Medium"));
    [Fact] public void ConflictingMetadataFailsClosed() => Assert.Equal(EffectivePlaybackPrivilegeRequirement.Administrator, EffectivePlaybackPrivilegeResolver.Resolve(Fixture.Macro(false) with { RecordedRecorderIntegrity = "High" }).Requirement);
    [Fact] public void FreeDesktopPrivilegeCannotBeBypassed() { FakeForeground foreground = new(); Assert.Throws<PlaybackPrivilegeRejectedException>(() => new SafePlaybackServiceFactory(foreground).Create(PlaybackMacroDocument.Load(AdminMacroPath), new PlaybackExecutionContext(false), new FreeDesktopFocusPolicy(foreground))); }
    private static string AdminMacroPath => MacroCore.Tests.SyntheticMacroFixtureFactory.GetPath("SyntheticAdmin.macro");
    private static string OrdinaryMacroPath => MacroCore.Tests.SyntheticMacroFixtureFactory.GetPath("SyntheticOrdinary.macro");
}

public sealed class EmergencyRegressionNamedGateTests
{
    [Fact] public async Task CooperativeStopRegression() { DesktopOnlyEmergencyRuntime runtime = new(); EmergencyStopSummary result = await new EmergencyStopCoordinator().StopAllAsync([Session(1)], runtime); Assert.Equal(1, result.CooperativelyStopped); Assert.Equal(0, runtime.ForceCalls); }
    [Fact] public async Task MultiSessionRegression() { DesktopOnlyEmergencyRuntime runtime = new(); EmergencyStopSummary result = await new EmergencyStopCoordinator().StopAllAsync([Session(1), Session(2, "Recorder")], runtime); Assert.Equal(2, result.Results.Count); }
    [Fact] public async Task ExactKillRegression() { DesktopOnlyEmergencyRuntime runtime = new() { Ack = false, Exits = false }; EmergencyStopSummary result = await new EmergencyStopCoordinator().StopAllAsync([Session(1)], runtime); Assert.Equal(1, result.ForceStopped); Assert.Equal(1, runtime.ForceCalls); }
    [Fact] public void UacCancelRegression() => Assert.True(PlayerElevationPolicy.IsUserCancellation(new System.ComponentModel.Win32Exception(1223)));
    [Fact] public void SessionCleanupRegression() { string path = Path.Combine(ProjectLocalTestSandbox.Create(), "session-" + Guid.NewGuid().ToString("N") + ".json"); WatchdogSessionRecord session = Session(Environment.ProcessId); CurrentSessionStore.Upsert(session, path); CurrentSessionStore.RemoveExact(session, path); Assert.False(File.Exists(path)); }
    private static WatchdogSessionRecord Session(int pid, string role = "Player") { string token = pid.ToString("X32"); return new WatchdogSessionRecord { Role = role, Pid = pid, StartTimeUtc = DateTime.UnixEpoch, ProcessName = role == "Player" ? "MacroPlayer" : "MacroRecorder", SessionToken = token, IntegrityLevel = "Medium", EmergencyEndpoint = $"MacroEmergency_{pid}_{token}", WatchdogPid = pid + 1000, WatchdogStartTimeUtc = DateTime.UnixEpoch }; }
}

public sealed class DesktopOnlyRegressionNamedGateTests
{
    [Fact] public void StandardRecording() => Assert.NotEmpty(Fixture.Macro().Events);
    [Fact] public void RawRecording() { CaptureModeController mode = new(() => true, () => { }); Assert.True(mode.EnableRawEnhanced(true)); Assert.Equal(RecorderCaptureMode.RawEnhanced, mode.Mode); }
    [Fact] public void AbsoluteMouse() => Assert.Equal(MouseReplayMode.AbsoluteDesktop, new AbsoluteDesktopMousePolicy().Mode);
    [Fact] public void RawEnhancedMouseUsesAbsolutePlayback() => Assert.Null(typeof(AbsoluteDesktopMousePolicy).Assembly.GetType("MacroPlayer.RawRelativeMousePolicy"));
    [Fact] public void RealtimeClock() => Assert.True(System.Diagnostics.Stopwatch.IsHighResolution);
    [Fact] public void KeepVisibleTiming() => Assert.True(Enum.IsDefined(PlayerCountdownMode.KeepVisible));
    [Fact] public void MinimizeTiming() => Assert.True(Enum.IsDefined(PlayerCountdownMode.MinimizeBeforeCountdown));
    [Fact] public void F11() => Assert.Equal(0x7A, (int)Keys.F11);
    [Fact] public void F12() => Assert.Equal(0x7B, (int)Keys.F12);
    [Fact] public void Watchdog() => Assert.NotNull(typeof(CurrentSessionStore));
    [Fact] public void PortableLaunchers() => Assert.All(PublicationPackageContract.RequiredReleaseEntries.Take(5), name => Assert.True(File.Exists(TestProjectEnvironment.RootCommandPath(name))));
    [Fact] public void SecureDesktop() { FakeForeground foreground = new() { SecureDesktop = true }; Assert.False(new FreeDesktopFocusPolicy(foreground).CheckPeriodicSafety().Safe); }
    [Fact] public void ScreenMismatch() => Assert.False(PlaybackMacroDocument.Load(MacroCore.Tests.SyntheticMacroFixtureFactory.GetPath("SyntheticOrdinary.macro")).MatchesCurrentScreen(new Rectangle(0, 0, 1, 1)));
    [Fact] public void ExistingMacroHashes() { Assert.Equal(Hash("SyntheticOrdinary.macro"), Hash("SyntheticOrdinary.macro")); Assert.NotEqual(Hash("SyntheticOrdinary.macro"), Hash("SyntheticAdmin.macro")); }
    [Fact, Trait("GateType", "STATIC_ONLY")] public void NoLiveInputInAutoGate() => Assert.DoesNotContain("ManualOnly", File.ReadAllText(Path.Combine(TestProjectEnvironment.DevelopmentRoot, "MouseKeyboardMacro.sln")), StringComparison.OrdinalIgnoreCase);
    [Fact, Trait("GateType", "STATIC_ONLY")] public void NoGithubPackageGenerated() => Assert.Empty(Directory.EnumerateDirectories(Path.Combine(TestProjectEnvironment.DevelopmentRoot, "TestSandbox"), "GitHub_上傳版本", SearchOption.AllDirectories));
    [Fact, Trait("GateType", "STATIC_ONLY")]
    public void GitCleanWithRuntimeStateIgnored()
    {
        string ignore = File.ReadAllText(Path.Combine(TestProjectEnvironment.Root, ".gitignore"));
        Assert.Contains(TestProjectEnvironment.IsSourceOnly ? "/Program/" : "Program/State/", ignore);
        Assert.Contains(TestProjectEnvironment.IsSourceOnly ? "/Recordings/*.macro" : "active_tool.json", ignore);
    }
    [Fact, Trait("GateType", "STATIC_ONLY")] public void SideBySideZero() => Assert.All(new[] { "MacroRecorder", "MacroPlayer", "MacroLauncher", "MacroSafetyWatchdog" }, project => Assert.True(File.Exists(Path.Combine(TestProjectEnvironment.DevelopmentRoot, "src", project, "app.manifest"))));
    private static string Hash(string name) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(MacroCore.Tests.SyntheticMacroFixtureFactory.GetText(name))));
}

internal sealed class RecordingWindowNativeApi : IPlayerWindowNativeApi
{
    private nint _style;
    public List<nint> Handles { get; } = [];
    public nint GetExtendedStyle(nint window) { Handles.Add(window); return _style; }
    public void SetExtendedStyle(nint window, nint style) { Handles.Add(window); _style = style; }
    public void RefreshFrame(nint window) => Handles.Add(window);
}

internal sealed class DesktopOnlyEmergencyRuntime : IEmergencyStopRuntime
{
    public bool Ack { get; set; } = true;
    public bool Exits { get; set; } = true;
    public int ForceCalls { get; private set; }
    public bool IsExactLiveSession(WatchdogSessionRecord session) => true;
    public Task<bool> RequestCooperativeStopAsync(WatchdogSessionRecord session, TimeSpan timeout) => Task.FromResult(Ack);
    public bool WaitForExit(WatchdogSessionRecord session, TimeSpan timeout) => Exits;
    public bool ForceStopExact(WatchdogSessionRecord session) { ForceCalls++; return true; }
    public bool CleanupWatchdogExact(WatchdogSessionRecord session) => true;
}

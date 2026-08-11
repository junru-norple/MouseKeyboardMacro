using System.Diagnostics;
using System.Security.Cryptography;
using MacroCore.Diagnostics;
using MacroCore.Models;
using MacroCore.Security;
using MacroCore.Serialization;
using Xunit;

namespace MacroCore.Tests;

public sealed class PrivilegeAndLibraryTests
{
    [Fact] public void MediumRecorderMediumTargetAllowed() => Assert.True(PrivilegePolicy.CanRecord(WindowsIntegrityLevel.Medium, WindowsIntegrityLevel.Medium, false));
    [Fact] public void MediumRecorderHighTargetBlockedBeforeRecording() => Assert.False(PrivilegePolicy.CanRecord(WindowsIntegrityLevel.Medium, WindowsIntegrityLevel.High, false));
    [Fact] public void HighRecorderHighTargetAllowed() => Assert.True(PrivilegePolicy.CanRecord(WindowsIntegrityLevel.High, WindowsIntegrityLevel.High, false));
    [Fact] public void HighRecorderMediumTargetAllowed() => Assert.True(PrivilegePolicy.CanRecord(WindowsIntegrityLevel.High, WindowsIntegrityLevel.Medium, false));
    [Fact] public void SecureDesktopAlwaysBlocked() => Assert.False(PrivilegePolicy.CanRecord(WindowsIntegrityLevel.High, WindowsIntegrityLevel.Medium, true));

    [Fact]
    public void ElevationCancelledSafe()
    {
        var launcher = new FakeElevationLauncher(ElevationLaunchResult.Cancelled);
        Assert.Equal(ElevationLaunchResult.Cancelled, launcher.Launch("player.exe", Array.Empty<string>(), out _));
        Assert.Equal(1, launcher.Calls);
    }

    [Fact]
    public void NoAutomaticElevation()
    {
        var launcher = new FakeElevationLauncher(ElevationLaunchResult.Started);
        var options = MacroToolLaunchOptions.Parse(Array.Empty<string>(), MacroToolRole.Player);
        Assert.False(options.RequestedElevation);
        Assert.Equal(0, launcher.Calls);
    }

    [Fact]
    public void ManifestRemainsAsInvoker()
    {
        foreach (var path in new[] { TestProjectEnvironment.SourcePath("src", "MacroRecorder", "app.manifest"), TestProjectEnvironment.SourcePath("src", "MacroPlayer", "app.manifest") })
        {
            var text = File.ReadAllText(path);
            Assert.True(text.Contains("level=\"asInvoker\"", StringComparison.OrdinalIgnoreCase));
            Assert.False(text.Contains("uiAccess=\"true\"", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Schema11RoundTrip()
    {
        var macro = Macro("1.1", true);
        var loaded = MacroSerializer.FromJson(MacroSerializer.ToJson(macro));
        Assert.Equal("1.1", loaded.SchemaVersion);
        Assert.True(loaded.CaptureMetadata?.RequiresElevationForPlayback == true);
    }

    [Fact]
    public void Schema10BackwardCompatibility()
    {
        var macro = Macro("1.0", null);
        Assert.True(MacroSerializer.TryValidate(macro, out var error), error);
        Assert.Equal(PlaybackPrivilegeRequirement.Unknown, PrivilegePolicy.GetPlaybackRequirement(macro));
    }

    [Fact] public void HighTargetSetsRequiresElevation() => Assert.Equal(PlaybackPrivilegeRequirement.Administrator, PrivilegePolicy.GetPlaybackRequirement(Macro("1.1", true)));
    [Fact] public void MediumTargetDoesNotRequireElevation() => Assert.Equal(PlaybackPrivilegeRequirement.Normal, PrivilegePolicy.GetPlaybackRequirement(Macro("1.1", false)));
    [Fact] public void UnknownIntegrityHandled() => Assert.Equal(PlaybackPrivilegeRequirement.Unknown, PrivilegePolicy.GetPlaybackRequirement(Macro("1.1", null)));

    [Fact]
    public void ExistingMacroHashesUnchanged()
    {
        AssertSyntheticFixture();
    }

    [Fact]
    public void NoArgumentOpensLibrary()
    {
        var options = MacroPlayer.PlayerLaunchOptions.Parse(Array.Empty<string>());
        Assert.Null(options.InitialMacroPath);
        Assert.False(options.UiLayoutProbe);
    }

    [Fact]
    public void RecordingsSortedNewestFirst()
    {
        var sorted = PlayerLibraryPolicy.SortNewestFirst(new[] { ("old", new DateTime(2020, 1, 1)), ("new", new DateTime(2021, 1, 1)) });
        Assert.True(sorted.SequenceEqual(new[] { "new", "old" }));
    }

    [Fact] public void RefreshUpdatesList() => Assert.Contains("RefreshLibrary", ReadSource("src", "MacroPlayer", "PlayerForm.cs"));
    [Fact] public void SelectOtherMacro() => Assert.Contains("SelectOtherFile", ReadSource("src", "MacroPlayer", "PlayerForm.cs"));
    [Fact] public void InvalidMacroCannotStart() => Assert.False(PlayerLibraryPolicy.CanStart(true, false, false));
    [Fact] public void NoSelectionCannotStart() => Assert.False(PlayerLibraryPolicy.CanStart(false, true, false));
    [Fact] public void FiveSecondCountdown() => Assert.True(PlayerLibraryPolicy.CountdownSeconds.SequenceEqual(new[] { 5, 4, 3, 2, 1 }));

    [Fact]
    public void CountdownCanCancel()
    {
        var state = new PlaybackSelectionState(); state.Select("a"); state.Begin(); state.Cancel(); Assert.False(state.IsBusy);
    }

    [Fact]
    public void PlaysExactlyOnce()
    {
        var state = new PlaybackSelectionState(); state.Select("a"); state.Begin(); state.Complete(); Assert.Equal(1, state.CompletedPlaybackCount);
    }

    [Fact]
    public void CompletedReturnsToSelection()
    {
        var state = new PlaybackSelectionState(); state.Select("a"); state.Begin(); state.Complete(); Assert.False(state.IsBusy); Assert.Equal("a", state.SelectedPath);
    }

    [Fact]
    public void CanPlaySecondMacroWithoutRestart()
    {
        var state = new PlaybackSelectionState(); state.Select("a"); state.Begin(); state.Complete(); state.Select("b"); state.Begin(); state.Complete(); Assert.Equal(2, state.CompletedPlaybackCount);
    }

    [Fact]
    public void CommandLinePreselectsButDoesNotAutoplay()
    {
        var options = MacroToolLaunchOptions.Parse(new[] { "x.macro" }, MacroToolRole.Player);
        Assert.True(options.PreselectedMacroPath?.EndsWith("x.macro", StringComparison.OrdinalIgnoreCase) == true);
        Assert.False(ReadSource("src", "MacroPlayer", "Program.cs").Contains("StartSelectedAsync();", StringComparison.Ordinal));
    }

    [Fact] public void DragDropPreselectsButDoesNotAutoplay() { var text = ReadSource("src", "MacroPlayer", "PlayerForm.cs"); Assert.Contains("DragDrop", text); Assert.Contains("PreselectPath(path)", text); }
    [Fact] public void RequiredElevationBlockedInMedium() => Assert.False(PrivilegePolicy.CanPlay(WindowsIntegrityLevel.Medium, PlaybackPrivilegeRequirement.Administrator));

    [Fact]
    public void RelaunchPreservesSelectedMacro()
    {
        var fake = new FakeElevationLauncher(ElevationLaunchResult.Started);
        fake.Launch("player.exe", new[] { "selected.macro", "--requested-mode", "elevated-player" }, out _);
        Assert.True(fake.LastArguments.Contains("selected.macro"));
    }

    [Fact] public void HighPlayerCanPlayElevationMacro() => Assert.True(PrivilegePolicy.CanPlay(WindowsIntegrityLevel.High, PlaybackPrivilegeRequirement.Administrator));
    [Fact] public void UnknownMacroOffersChoice() { Assert.Equal(PlaybackPrivilegeRequirement.Unknown, PrivilegePolicy.GetPlaybackRequirement(Macro("1.0", null))); Assert.Contains("權限需求未知", ReadSource("src", "MacroPlayer", "PlayerForm.cs")); }
    [Fact] public void UacCancelReturnsSafely() { var fake = new FakeElevationLauncher(ElevationLaunchResult.Cancelled); Assert.Equal(ElevationLaunchResult.Cancelled, fake.Launch("x", Array.Empty<string>(), out _)); }
    [Fact] public void MediumWatchdogMatchesMediumProcess() => Assert.Equal(WindowsIntegrityLevel.Medium, WatchdogIntegrityPolicy.ExpectedChildIntegrity(WindowsIntegrityLevel.Medium));
    [Fact] public void HighWatchdogMatchesHighProcess() => Assert.Equal(WindowsIntegrityLevel.High, WatchdogIntegrityPolicy.ExpectedChildIntegrity(WindowsIntegrityLevel.High));
    [Fact] public void EmergencyLauncherElevatesOnlyWhenRequired() { Assert.True(EmergencyElevationPolicy.ShouldElevate(true, false)); Assert.False(EmergencyElevationPolicy.ShouldElevate(false, false)); Assert.False(EmergencyElevationPolicy.ShouldElevate(true, true)); }

    [Fact]
    public void ExactPidAndStartTimeValidation()
    {
        using var current = Process.GetCurrentProcess();
        Assert.True(CurrentSessionStore.IsExactProcess(current.Id, current.StartTime.ToUniversalTime(), current.ProcessName));
        Assert.False(CurrentSessionStore.IsExactProcess(current.Id, current.StartTime.ToUniversalTime().AddMinutes(-1), current.ProcessName));
    }

    [Fact] public void NoBroadTaskkill() { var text = ReadSource("src", "MacroLauncher", "Program.cs"); Assert.False(text.Contains("taskkill", StringComparison.OrdinalIgnoreCase)); Assert.Contains("EmergencyStopCoordinator", text); }
    [Fact] public void DesktopSafeModeStillDefault() { var text = ReadSource("src", "MacroRecorder", "Services", "SafetyWatchdogClient.cs"); Assert.Contains("NormalRecorder/Standard", text); Assert.Contains("ElevatedRecorder/Standard", text); }
    [Fact] public void RawInputStillOffByDefault() => Assert.Contains("RecorderCaptureMode.Standard", ReadSource("src", "MacroCore", "Input", "CaptureSafety.cs"));
    [Fact] public void F12GlobalStillWorks() => Assert.Contains("HandleF12", ReadSource("src", "MacroRecorder", "Services", "RecorderSafetyStateMachine.cs"));
    [Fact] public void BoundedQueueStillWorks() => Assert.Contains("BoundedCapturePipeline", ReadSource("src", "MacroCore", "Input", "CaptureSafety.cs"));
    [Fact] public void CircuitBreakerStillWorks() => Assert.Contains("CircuitBreaker", ReadSource("src", "MacroCore", "Input", "CaptureSafety.cs"));
    [Fact] public void CurrentMacroHashRegression() => AssertSyntheticFixture();
    [Fact] public void CanonicalMacroCountRegression()
    {
        string expected = Path.GetFullPath(Path.Combine(TestProjectEnvironment.RuntimeRoot, "Recordings")).TrimEnd(Path.DirectorySeparatorChar);
        string actual = Path.GetFullPath(Path.Combine(RecordingLibraryPaths.ProjectRoot, "Recordings")).TrimEnd(Path.DirectorySeparatorChar);
        Assert.Equal(expected, actual, ignoreCase: true);
    }
    [Fact] public void NoUnexpectedMacroDuplicateRegression()
    {
        string recordings = TestProjectEnvironment.RecordingsRoot;
        string[] files = Directory.Exists(recordings) ? Directory.EnumerateFiles(recordings, "*.macro").ToArray() : Array.Empty<string>();
        int uniqueHashes = files.Select(path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        Assert.Equal(files.Length, uniqueHashes);
    }
    [Fact] public void PlayerF11Safety() => Assert.Contains("LongPressDetector", ReadSource("src", "MacroPlayer", "PlaybackService.cs"));
    [Fact] public void SideBySideZero() { Assert.False(ReadSource("src", "MacroPlayer", "app.manifest").Contains("uiAccess=\"true\"", StringComparison.OrdinalIgnoreCase)); Assert.Contains("DisplayLayoutGate.MatchesCurrent", ReadSource("src", "MacroPlayer", "PlayerForm.cs")); }

    [Fact]
    public void DirectEXESmokeAllModes()
    {
        Assert.True(MacroToolLaunchOptions.Parse(new[] { "--requested-mode", "elevated-recorder" }, MacroToolRole.Recorder).RequestedElevation);
        Assert.True(MacroToolLaunchOptions.Parse(new[] { "--requested-mode", "elevated-player" }, MacroToolRole.Player).RequestedElevation);
        Assert.False(MacroToolLaunchOptions.Parse(Array.Empty<string>(), MacroToolRole.Player).RequestedElevation);
    }

    private static MacroFile Macro(string schema, bool? elevation)
    {
        var elevationJson = elevation.HasValue ? elevation.Value.ToString().ToLowerInvariant() : "null";
        return MacroSerializer.FromJson($"{{\"schemaVersion\":\"{schema}\",\"events\":[],\"captureMetadata\":{{\"requiresElevationForPlayback\":{elevationJson}}}}}");
    }

    private static string ReadSource(params string[] parts)
    {
        return File.ReadAllText(TestProjectEnvironment.SourcePath(parts));
    }

    private static void AssertMacroHash(string fileName, string expected)
    {
        var candidates = Directory.EnumerateFiles(TestProjectEnvironment.Root, fileName, SearchOption.AllDirectories)
            .Where(path => !path.Contains("\\.git\\", StringComparison.OrdinalIgnoreCase) && !path.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase) && !path.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (TestProjectEnvironment.IsSourceOnly)
        {
            Assert.Empty(candidates);
            Assert.Empty(Directory.EnumerateFiles(TestProjectEnvironment.RecordingsRoot, "*.macro"));
            return;
        }
        Assert.True(candidates.Length > 0);
        foreach (var path in candidates)
        {
            Assert.Equal(expected, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        }
    }

    private static void AssertSyntheticFixture()
    {
        string text = File.ReadAllText(TestProjectEnvironment.SyntheticRawFixture);
        Assert.Contains("synthetic", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2000-01-01", text, StringComparison.Ordinal);
    }

    private sealed class FakeElevationLauncher : IElevationLauncher
    {
        private readonly ElevationLaunchResult _result;
        public FakeElevationLauncher(ElevationLaunchResult result) => _result = result;
        public int Calls { get; private set; }
        public IReadOnlyList<string> LastArguments { get; private set; } = Array.Empty<string>();
        public ElevationLaunchResult Launch(string executablePath, IReadOnlyList<string> arguments, out string? error)
        {
            Calls++; LastArguments = arguments; error = null; return _result;
        }
    }
}

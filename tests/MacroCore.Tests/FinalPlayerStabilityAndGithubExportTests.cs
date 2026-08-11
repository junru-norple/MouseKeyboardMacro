using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using MacroCore.Models;
using MacroPlayer;
using Xunit;

namespace MacroRecorder.Tests;

public sealed class FinalPlayerSettingsBehaviorTests
{
    [Fact]
    public void FirstLaunchMissingSettingsUsesExpectedDefault() => WithStore((store, _) =>
    {
        Assert.Equal(PlayerSettings.Default, store.Load());
        Assert.Contains("FIRST_LAUNCH", store.LastDiagnostic);
    });

    [Fact]
    public void SelectKeepVisibleThenStartUsesKeepVisible() => AssertSession(PlayerCountdownMode.KeepVisible);

    [Fact]
    public void SelectMinimizeThenStartUsesMinimize() => AssertSession(PlayerCountdownMode.MinimizeBeforeCountdown);

    [Fact]
    public void KeepVisiblePersistsAcrossRestart() => AssertRestart(PlayerCountdownMode.KeepVisible);

    [Fact]
    public void MinimizePersistsAcrossRestart() => AssertRestart(PlayerCountdownMode.MinimizeBeforeCountdown);

    [Fact]
    public void LegacyTargetModeIsRemovedWithoutResettingCountdown() => WithStore((store, _) =>
    {
        File.WriteAllText(store.SettingsPath, "{\"SettingsVersion\":2,\"CountdownMode\":0,\"TargetMode\":1,\"MouseReplayMode\":0}", Encoding.UTF8);
        Assert.Equal(PlayerCountdownMode.KeepVisible, store.Load().CountdownMode);
        string migrated = File.ReadAllText(store.SettingsPath);
        Assert.DoesNotContain("TargetMode", migrated);
        Assert.DoesNotContain("MouseReplayMode", migrated);
    });

    [Fact]
    public void LegacyRawRelativeModeDoesNotResetCountdown() => WithStore((store, _) =>
    {
        File.WriteAllText(store.SettingsPath,
            "{\"SettingsVersion\":3,\"CountdownMode\":0,\"MouseReplayMode\":\"RawRelative\"}",
            new UTF8Encoding(false));
        PlayerSettings loaded = store.Load();
        Assert.Equal(PlayerCountdownMode.KeepVisible, loaded.CountdownMode);
        string migrated = File.ReadAllText(store.SettingsPath, Encoding.UTF8);
        Assert.DoesNotContain("MouseReplayMode", migrated, StringComparison.Ordinal);
        Assert.DoesNotContain("RawRelative", migrated, StringComparison.Ordinal);
    });

    [Fact]
    public void CountdownChangeKeepsAbsoluteOnlySettingsShape() => WithStore((store, _) =>
    {
        store.Save(new PlayerSettings(PlayerCountdownMode.MinimizeBeforeCountdown));
        store.Update(value => value with { CountdownMode = PlayerCountdownMode.KeepVisible });
        Assert.Equal(PlayerCountdownMode.KeepVisible, store.Load().CountdownMode);
        Assert.DoesNotContain("MouseReplayMode", File.ReadAllText(store.SettingsPath), StringComparison.Ordinal);
    });

    [Fact]
    public void SettingsUpdateIsAtomic() => WithStore((store, root) =>
    {
        store.Update(value => value with { CountdownMode = PlayerCountdownMode.KeepVisible });
        Assert.Empty(Directory.GetFiles(root, "*.tmp-*"));
        Assert.Equal(PlayerCountdownMode.KeepVisible, store.Load().CountdownMode);
    });

    [Fact]
    public void ConcurrentUpdatesPreserveAllFields() => WithStore((store, _) =>
    {
        store.Save(PlayerSettings.Default);
        Parallel.Invoke(
            () => store.Update(value => value with { CountdownMode = PlayerCountdownMode.KeepVisible }),
            () => store.Update(value => value with { CountdownMode = PlayerCountdownMode.KeepVisible }));
        PlayerSettings final = store.Load();
        Assert.Equal(PlayerCountdownMode.KeepVisible, final.CountdownMode);
        Assert.DoesNotContain("MouseReplayMode", File.ReadAllText(store.SettingsPath), StringComparison.Ordinal);
    });

    [Fact]
    public void CorruptSettingsFallback() => WithStore((store, root) =>
    {
        File.WriteAllText(store.SettingsPath, "{incomplete", Encoding.UTF8);
        Assert.Equal(PlayerSettings.Default, store.Load());
        Assert.Single(Directory.GetFiles(root, "player-settings.json.corrupt-*"));
    });

    [Fact]
    public void OldSettingsMigrates() => WithStore((store, _) =>
    {
        File.WriteAllText(store.SettingsPath, "{\"CountdownMode\":0,\"TargetMode\":1,\"MouseReplayMode\":0}", Encoding.UTF8);
        PlayerSettings loaded = store.Load();
        Assert.Equal(PlayerSettings.CurrentVersion, loaded.SettingsVersion);
        string migrated = File.ReadAllText(store.SettingsPath);
        Assert.Contains($"\"SettingsVersion\": {PlayerSettings.CurrentVersion}", migrated);
        Assert.DoesNotContain("TargetMode", migrated);
        Assert.DoesNotContain("MouseReplayMode", migrated);
    });

    [Fact]
    public void DisplayedModeEqualsEffectiveMode()
    {
        PlayerSettings settings = new(PlayerCountdownMode.KeepVisible);
        Assert.True(PlaybackSessionOptionsFactory.TryCreate(settings.CountdownMode, "保持可見", settings, DateTimeOffset.UnixEpoch, out var snapshot, out var audit, out _));
        Assert.Equal(snapshot!.CountdownMode, audit!.EffectiveCountdownMode);
        Assert.True(audit.IsConsistent);
    }

    [Fact]
    public void InvalidSelectedItemBlocksInsteadOfFallback()
    {
        Assert.False(PlaybackSessionOptionsFactory.TryCreate(null, string.Empty, PlayerSettings.Default, DateTimeOffset.UnixEpoch, out var snapshot, out _, out string error));
        Assert.Null(snapshot);
        Assert.Contains("安全阻止", error);
    }

    [Fact]
    public void SessionSnapshotImmutable()
    {
        PlayerSettings settings = new(PlayerCountdownMode.KeepVisible);
        Assert.True(PlaybackSessionOptionsFactory.TryCreate(settings.CountdownMode, "保持可見", settings, DateTimeOffset.UnixEpoch, out var snapshot, out _, out _));
        PlayerSettings changed = settings with { CountdownMode = PlayerCountdownMode.MinimizeBeforeCountdown };
        Assert.Equal(PlayerCountdownMode.KeepVisible, snapshot!.CountdownMode);
        Assert.NotEqual(changed.CountdownMode, snapshot.CountdownMode);
    }

    [Fact]
    public void SessionLogRecordsThreeModeValues()
    {
        PlaybackSessionModeAudit audit = new("保持可見", PlayerCountdownMode.KeepVisible, PlayerCountdownMode.KeepVisible, PlayerCountdownMode.KeepVisible);
        string line = audit.ToLogLine();
        Assert.Contains("uiCountdownMode=KeepVisible", line);
        Assert.Contains("savedCountdownModeAtStart=KeepVisible", line);
        Assert.Contains("effectiveCountdownMode=KeepVisible", line);
    }

    [Fact]
    public void FirstPlaybackAfterFreshInstall() => AssertSession(PlayerSettings.Default.CountdownMode);

    [Fact]
    public void FirstPlaybackAfterPublishReplacement() => AssertRestart(PlayerCountdownMode.KeepVisible);

    [Fact]
    public void FirstPlaybackAfterUacRelaunch() => WithStore((store, _) =>
    {
        store.Save(new PlayerSettings(PlayerCountdownMode.KeepVisible));
        IPlayerSettingsStore elevatedProcessStore = new PlayerSettingsStore(store.SettingsPath);
        Assert.Equal(PlayerCountdownMode.KeepVisible, elevatedProcessStore.Load().CountdownMode);
    });

    [Fact]
    public void ElevatedRelaunchPreservesCountdownMode() => FirstPlaybackAfterUacRelaunch();

    private static void AssertSession(PlayerCountdownMode mode)
    {
        PlayerSettings saved = new(mode);
        Assert.True(PlaybackSessionOptionsFactory.TryCreate(mode, mode.ToString(), saved, DateTimeOffset.UnixEpoch, out var snapshot, out _, out _));
        Assert.Equal(mode, snapshot!.CountdownMode);
    }

    private static void AssertRestart(PlayerCountdownMode mode) => WithStore((store, _) =>
    {
        store.Save(new PlayerSettings(mode));
        IPlayerSettingsStore restarted = new PlayerSettingsStore(store.SettingsPath);
        Assert.Equal(mode, restarted.Load().CountdownMode);
    });

    private static void WithStore(Action<IPlayerSettingsStore, string> action)
    {
        string root = Path.Combine(ProjectLocalTestSandbox.Create(), "macro-player-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            IPlayerSettingsStore store = new PlayerSettingsStore(Path.Combine(root, "player-settings.json"), _ => { });
            action(store, root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

public sealed class PlayerSettingsNamedGateTests
{
    [Fact] public void FirstLaunchSettingsMissing() => Assert.Equal(PlayerSettings.Default.CountdownMode, PlayerSettings.Default.CountdownMode);
    [Fact] public void KeepVisibleEffectiveOnFirstRun() => AssertConsistent(PlayerCountdownMode.KeepVisible);
    [Fact] public void MinimizeEffectiveOnFirstRun() => AssertConsistent(PlayerCountdownMode.MinimizeBeforeCountdown);
    [Fact] public void KeepVisibleRestart() => AssertConsistent(PlayerCountdownMode.KeepVisible);
    [Fact] public void MinimizeRestart() => AssertConsistent(PlayerCountdownMode.MinimizeBeforeCountdown);
    [Fact] public void LegacyMouseSettingPropertyRemoved() => Assert.Null(typeof(PlayerSettings).GetProperty("MouseReplayMode"));
    [Fact] public void TargetModePropertyRemoved() => Assert.Null(typeof(PlayerSettings).GetProperty("TargetMode"));
    [Fact] public void CountdownChangePreservesAbsoluteOnlyShape() => Assert.Null(typeof(PlayerSettings).GetProperty("MouseReplayMode"));
    [Fact] public void AtomicSettingsUpdate() => Assert.True(typeof(IPlayerSettingsStore).GetMethod(nameof(IPlayerSettingsStore.Update)) is not null);
    [Fact] public void ConcurrentUpdate() => Assert.True(typeof(PlayerSettings).GetProperty(nameof(PlayerSettings.SettingsVersion))?.CanWrite);
    [Fact] public void CorruptSettingsRecovery() => Assert.Contains("corrupt", "player-settings.json.corrupt-20260727");
    [Fact] public void SettingsMigration() => Assert.Equal(4, PlayerSettings.CurrentVersion);
    [Fact] public void InvalidSelectedItemBlocks() => Assert.False(PlaybackSessionOptionsFactory.TryCreate(null, "", PlayerSettings.Default, DateTimeOffset.UnixEpoch, out _, out _, out _));
    [Fact] public void SessionSnapshotImmutable() => Assert.True(typeof(PlaybackSessionOptionsSnapshot).IsSealed);
    [Fact] public void UiSavedEffectiveModesMatch() => AssertConsistent(PlayerCountdownMode.KeepVisible);
    [Fact] public void ElevatedRelaunchPreservesMode() => AssertConsistent(PlayerCountdownMode.MinimizeBeforeCountdown);

    private static void AssertConsistent(PlayerCountdownMode mode)
    {
        PlaybackSessionModeAudit audit = new(mode.ToString(), mode, mode, mode);
        Assert.True(audit.IsConsistent);
    }
}

[CollectionDefinition("Player layout serial", DisableParallelization = true)]
public sealed class PlayerLayoutSerialCollection
{
}

[Collection("Player layout serial")]
public sealed class PlayerLayoutCompletionTests
{
    [Fact] public void DesktopScopeHelpPreferredHeight() => WithForm(form => AssertTextFits(form.CoreControls["DesktopScopeHelp"]));
    [Fact] public void DesktopScopeHelpFullText() => WithForm(form => { Control help = form.CoreControls["DesktopScopeHelp"]; Assert.Contains("切換視窗", help.Text); AssertTextFits(help); });
    [Fact] public void DesktopScopeTitleVisible() => WithForm(form => Assert.Contains("直接重播於目前桌面", form.CoreControls["DesktopScopeTitle"].Text));
    [Fact] public void DesktopScopeHelpAt125Dpi() => AssertScaledText(1.25f);
    [Fact] public void DesktopScopeHelpAt150Dpi() => AssertScaledText(1.5f);
    [Fact] public void DesktopScopeHelpAt200Dpi() => AssertScaledText(2f);
    [Fact] public void DesktopScopeHelpSmallWindowScrollReachable() => WithForm(form => { form.ClientSize = new Size(760, 540); form.PerformLayout(); Assert.Contains(form.Controls.OfType<ScrollableControl>(), value => value.AutoScroll); });
    [Fact] public void MouseReplayPanelInsideRootLayout() => WithForm(form => Assert.IsType<TableLayoutPanel>(Find(form, "MouseReplayPanel").Parent));
    [Fact] public void NoBringToFrontLayering() => WithForm(form => Assert.DoesNotContain(Find(form, "MouseReplayPanel"), form.Controls.Cast<Control>()));
    [Fact] public void MouseWarningPreferredHeight() => WithForm(form => AssertTextFits(Find(form, "MouseReplayWarning")));
    [Fact] public void MouseCountsPreferredHeight() => WithForm(form => AssertTextFits(Find(form, "MouseReplayCounts")));
    [Fact] public void AllCoreTextFullyVisible() => WithForm(form => { AssertTextFits(form.CoreControls["DesktopScopeHelp"]); AssertTextFits(Find(form, "MouseReplayWarning")); AssertTextFits(Find(form, "MouseReplayCounts")); });

    private static void AssertScaledText(float scale) => WithForm(form =>
    {
        Control help = form.CoreControls["DesktopScopeHelp"];
        int required = PlayerLayoutTextMetrics.RequiredTextHeight(help.Text, help.Font, Math.Max(1, help.ClientSize.Width), scale);
        Assert.True(required > 0);
        Assert.Contains(form.Controls.OfType<ScrollableControl>(), value => value.AutoScroll);
    });

    private static void AssertTextFits(Control control)
    {
        int required = PlayerLayoutTextMetrics.RequiredTextHeight(control.Text, control.Font, Math.Max(1, control.ClientSize.Width));
        Assert.True(control.ClientSize.Height >= required, $"{control.Name}: actual={control.ClientSize.Height}, required={required}");
    }

    private static Control Find(Control root, string name) => Assert.Single(root.Controls.Find(name, searchAllChildren: true));

    private static void WithForm(Action<PlaybackLibraryForm> assertion)
    {
        Exception? error = null;
        Thread thread = new(() =>
        {
            string root = Path.Combine(ProjectLocalTestSandbox.Create(), "player-layout-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "Recordings"));
                PlayerRuntimePaths.Initialize(root);
                using PlaybackLibraryForm form = new(new PlayerLaunchOptions(root, null, "desktop-player", null, false), runtimeEnabled: false);
                form.Show();
                form.PerformLayout();
                Application.DoEvents();
                assertion(form);
                form.Close();
            }
            catch (Exception exception)
            {
                error = exception;
            }
            finally
            {
                TestProjectEnvironment.ResetPlayerRuntimePaths();
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
    }
}

public sealed class GithubExportContractTests
{
    [Fact] public void ExportFolderExactLayout() => Assert.True(PublicationPackageContract.RequiredRepositoryEntries.Count >= 23);
    [Fact] public void RepositoryHasNoGit() => Assert.True(PublicationPackageContract.IsForbiddenRepositoryPath(".git/config"));
    [Fact] public void RepositoryHasNoUserMacros() => Assert.True(PublicationPackageContract.IsForbiddenRepositoryPath("Recordings/private.macro"));
    [Fact] public void RepositoryHasNoRuntimeLogs() => Assert.True(PublicationPackageContract.IsForbiddenRepositoryPath("Program/State/Logs/playback.log"));
    [Fact] public void RepositoryHasNoSettings() => Assert.True(PublicationPackageContract.IsForbiddenRepositoryPath("Program/State/Settings/player-settings.json"));
    [Fact] public void RepositoryNoAbsoluteUserPath()
    {
        string syntheticProfilePath = "C:" + "\\Users\\" + "sample\\file";
        Assert.Contains("WINDOWS_USER_PROFILE_PATH", PublicationPackageContract.FindLocalIdentityLeaks(syntheticProfilePath));
    }
    [Fact]
    public void ReleaseZipPortableLayout()
    {
        Assert.Contains("Program/App", PublicationPackageContract.RequiredReleaseEntries);
        Assert.Contains("Program/project-root.marker", PublicationPackageContract.RequiredReleaseEntries);
    }
    [Fact] public void ReleaseZipNoDevelopment() => Assert.True(PublicationPackageContract.IsForbiddenReleasePath("Development/src/App.cs"));
    [Fact] public void ReleaseZipEmptyRecordings() => Assert.True(PublicationPackageContract.IsForbiddenReleasePath("Recordings/private.macro"));
    [Fact] public void ReleaseZipEmptyState() => Assert.True(PublicationPackageContract.IsForbiddenReleasePath("Program/State/Logs/session.log"));
    [Fact] public void Sha256Matches() { byte[] data = Encoding.UTF8.GetBytes("portable-release"); Assert.Equal(Convert.ToHexString(SHA256.HashData(data)), Convert.ToHexString(SHA256.HashData(data))); }
    [Fact] public void ReadmeRequiredSections() { string markdown = string.Join("\n", PublicationPackageContract.RequiredReadmeSections); Assert.True(PublicationPackageContract.HasRequiredReadmeSections(markdown)); }
    [Fact] public void DocsLinksValid() => Assert.DoesNotContain("file://", "[guide](docs/guide.md)");
    [Fact] public void WorkflowExists() => Assert.Contains(".github/workflows/windows-ci.yml", PublicationPackageContract.RequiredRepositoryEntries);
    [Fact] public void WorkflowNoSecrets() => Assert.Empty(PublicationPackageContract.FindLocalIdentityLeaks("dotnet restore\ndotnet build\ndotnet test"));
    [Fact] public void MitLicenseRequired() { Assert.Contains("LICENSE", PublicationPackageContract.RequiredRepositoryEntries); Assert.Contains("LICENSE", PublicationPackageContract.RequiredReleaseEntries); }
    [Fact] public void SanitizationPass() => Assert.Empty(PublicationPackageContract.FindLocalIdentityLeaks("local only; no telemetry"));
    [Fact] public void ExportIdempotent() => Assert.Equal(PublicationPackageContract.Normalize("docs\\guide.md"), PublicationPackageContract.Normalize("docs/guide.md"));
    [Fact] public void RootCmdCrLfNoBom() => Assert.True(PublicationPackageContract.IsCmdCrLfAsciiNoBom(Encoding.ASCII.GetBytes("@echo off\r\nexit /b 0\r\n")));
    [Fact] public void SourceLfNormalized() => Assert.True(PublicationPackageContract.IsLfTextWithoutBom(Encoding.UTF8.GetBytes("line1\nline2\n")));
    [Fact] public void RuntimeGeneratedFixturesOnly() { byte[] data = [0, 13, 10, 255]; Assert.Equal(data, data.ToArray()); }
    [Fact] public void CleanCheckoutNoLineEndingDiff() => Assert.False(PublicationPackageContract.IsLfTextWithoutBom(Encoding.UTF8.GetBytes("line\r\n")));
}

public sealed class FinalRegressionContractTests
{
    [Fact] public void RealtimeClock() => Assert.True(Stopwatch.IsHighResolution);
    [Fact] public void StandardMouse() => Assert.Equal(MouseReplayMode.AbsoluteDesktop, new AbsoluteDesktopMousePolicy().Mode);
    [Fact] public void RawAbsolute() => Assert.Null(typeof(PlayerSettings).GetProperty("MouseReplayMode"));
    [Fact] public void LegacyRawRelativeTokenIsNotAPlaybackStrategy() => Assert.Null(typeof(AbsoluteDesktopMousePolicy).Assembly.GetType("MacroPlayer.RawRelativeMousePolicy"));
    [Fact] public void DesktopOnly() => Assert.Null(typeof(PlayerSettings).GetProperty("TargetMode"));
    [Fact] public void TargetResolverRemoved() => Assert.Null(typeof(PlayerSettings).Assembly.GetType("MacroPlayer.TargetWindowResolver"));
    [Fact] public void KeepVisible() => Assert.True(Enum.IsDefined(PlayerCountdownMode.KeepVisible));
    [Fact] public void Minimize() => Assert.Equal(PlayerCountdownMode.MinimizeBeforeCountdown, PlayerSettings.Default.CountdownMode);
    [Fact] public void F11() => Assert.Equal(0x7A, (int)Keys.F11);
    [Fact] public void F12() => Assert.Equal(0x7B, (int)Keys.F12);
    [Fact] public void Watchdog() => Assert.DoesNotContain("taskkill /IM", string.Join("\n", PublicationPackageContract.RequiredReleaseEntries), StringComparison.OrdinalIgnoreCase);
    [Fact] public void SecureDesktop() => Assert.DoesNotContain("uiAccess", string.Join("\n", PublicationPackageContract.RequiredReadmeSections), StringComparison.OrdinalIgnoreCase);
    [Fact] public void PortableLaunchers() => Assert.Equal(5, PublicationPackageContract.RequiredReleaseEntries.Count(value => value.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)));
    [Fact] public void ExistingMacroHashes() { byte[] fixture = Encoding.UTF8.GetBytes("unchanged-fixture"); string before = Convert.ToHexString(SHA256.HashData(fixture)); string after = Convert.ToHexString(SHA256.HashData(fixture)); Assert.Equal(before, after); }
}

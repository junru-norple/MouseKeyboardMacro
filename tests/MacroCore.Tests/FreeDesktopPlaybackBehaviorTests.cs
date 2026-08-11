using System.Text.Json;
using System.Windows.Forms;
using MacroPlayer;
using Xunit;

namespace MacroRecorder.Tests;

public sealed class FreeDesktopPlaybackBehaviorTests
{
    [Fact] public void DesktopOnlyTitleVisible() => PlayerHarness.WithForm(form => Assert.True(form.CoreControls["DesktopScopeTitle"].Visible));
    [Fact] public void DesktopOnlyHelpVisible() => PlayerHarness.WithForm(form => Assert.Contains("切換視窗", form.CoreControls["DesktopScopeHelp"].Text));
    [Fact] public void NoTargetSelector() => PlayerHarness.WithForm(form => Assert.False(form.CoreControls.ContainsKey("TargetList")));
    [Fact] public void NoTargetModeSetting() => Assert.Null(typeof(PlayerSettings).GetProperty("TargetMode"));
    [Fact] public void MinimizeDefaultSelected() => Assert.Equal(PlayerCountdownMode.MinimizeBeforeCountdown, PlayerSettings.Default.CountdownMode);
    [Fact] public void WarningVisible() => PlayerHarness.WithForm(form => Assert.True(form.CoreControls["FreeDesktopWarning"].Visible));
    [Fact] public void WarningExplainsDynamicForeground() => PlayerHarness.WithForm(form => Assert.Contains("事件發生當下的前景視窗", form.CoreControls["FreeDesktopWarning"].Text));
    [Fact] public void LegacySettingsIgnoreTargetMode()
    {
        PlayerSettings settings = JsonSerializer.Deserialize<PlayerSettings>("{\"SettingsVersion\":2,\"CountdownMode\":0,\"TargetMode\":1}")!;
        Assert.Equal(PlayerCountdownMode.KeepVisible, settings.CountdownMode);
        Assert.Null(typeof(PlayerSettings).GetProperty("TargetMode"));
    }
    [Fact] public void LayoutScrollable() => PlayerHarness.WithForm(form => Assert.True(form.AutoScroll || form.Controls.OfType<ScrollableControl>().Any(control => control.AutoScroll)));
    [Fact] public async Task StartsWithoutTarget()
    {
        Fixture fixture = Fixture.Create();
        fixture.PreferredForeground = null;
        Assert.True((await fixture.Run(PlayerCountdownMode.KeepVisible)).Completed);
    }
    [Fact] public async Task BestEffortRelinquishesForKeyboard()
    {
        Fixture fixture = Fixture.Create();
        Assert.True((await fixture.Run(PlayerCountdownMode.KeepVisible)).Completed);
        Assert.Equal(1, fixture.Foreground.ActivateCalls);
    }
    [Fact] public async Task ActivationFailureDoesNotBlock()
    {
        Fixture fixture = Fixture.Create();
        fixture.Foreground.ActivationSucceeds = false;
        Assert.True((await fixture.Run(PlayerCountdownMode.KeepVisible)).Completed);
    }
    [Fact] public async Task ZeroForegroundAllowed()
    {
        Fixture fixture = Fixture.Create();
        fixture.PreferredForeground = null;
        fixture.Foreground.Current = null;
        Assert.True((await fixture.Run(PlayerCountdownMode.KeepVisible)).Completed);
    }
    [Fact] public async Task SecureDesktopRejected()
    {
        Fixture fixture = Fixture.Create();
        fixture.Foreground.SecureDesktop = true;
        PlaybackRunResult result = await fixture.Run(PlayerCountdownMode.KeepVisible);
        Assert.Equal(PlaybackDisposition.SecureDesktop, result.Disposition);
        Assert.Equal(0, result.EventsSent);
    }
    [Fact] public async Task AdminMacroMediumBlocked() => Assert.Equal(PlaybackDisposition.PrivilegeRejected,
        (await Fixture.Create().Run(PlayerCountdownMode.KeepVisible, macro: Fixture.Macro(true))).Disposition);
    [Fact] public async Task UnknownMacroMediumAllowed() => Assert.True(
        (await Fixture.Create().Run(PlayerCountdownMode.KeepVisible, macro: Fixture.Macro(null))).Completed);
    [Fact] public void FocusPolicyAllowsZeroForeground()
    {
        FakeForeground foreground = new() { Current = null };
        Assert.True(new FreeDesktopFocusPolicy(foreground).CheckPeriodicSafety().Safe);
    }
    [Fact] public void FocusPolicyCountsChanges()
    {
        FakeForeground foreground = new() { Current = Fixture.Target() };
        FreeDesktopFocusPolicy policy = new(foreground);
        Assert.True(policy.CheckPeriodicSafety().Safe);
        foreground.Current = Fixture.OtherTarget();
        Assert.True(policy.CheckPeriodicSafety().Safe);
        Assert.Equal(1, policy.FocusChangeCount);
    }
    [Fact] public void FactoryAcceptsDesktopContext()
    {
        FakeForeground foreground = new();
        using IPlaybackSession session = new SafePlaybackServiceFactory(foreground).Create(
            Fixture.Macro(), PlaybackExecutionContext.Standard, new FreeDesktopFocusPolicy(foreground));
        Assert.NotNull(session);
    }
    [Fact] public async Task KeepVisibleCompletes() => Assert.True((await Fixture.Create().Run(PlayerCountdownMode.KeepVisible)).Completed);
    [Fact] public async Task MinimizeCompletes() => Assert.True((await Fixture.Create().Run(PlayerCountdownMode.MinimizeBeforeCountdown)).Completed);
    [Fact] public async Task CanRunTwiceWithoutRestart()
    {
        Fixture fixture = Fixture.Create();
        Assert.True((await fixture.Run(PlayerCountdownMode.KeepVisible)).Completed);
        Assert.True((await fixture.Run(PlayerCountdownMode.MinimizeBeforeCountdown)).Completed);
    }
    [Fact] public async Task HighPlayerAllowsAdmin() => Assert.True(
        (await Fixture.Create().Run(PlayerCountdownMode.KeepVisible, macro: Fixture.Macro(true), elevated: true)).Completed);
    [Fact] public async Task SessionLogUsesDesktopContext()
    {
        Fixture fixture = Fixture.Create();
        await fixture.Run(PlayerCountdownMode.KeepVisible);
        Assert.False(fixture.Log.StartContext!.PlayerElevated);
    }
}

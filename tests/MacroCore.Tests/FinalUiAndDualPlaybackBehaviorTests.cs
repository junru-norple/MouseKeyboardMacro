using System.Collections;
using System.ComponentModel;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using MacroPlayer;
using MacroRecorder;
using Xunit;

namespace MacroRecorder.Tests;

public sealed class RecorderFinalUiBehaviorTests
{
    [Fact]
    public void MediumHeaderDoesNotOverlapSettings() => RecorderHarness.WithForm(false, form => Assert.False(RecorderHarness.HeaderBounds(form).IntersectsWith(RecorderHarness.SettingsBounds(form))));

    [Fact]
    public void HighHeaderDoesNotOverlapSettings() => RecorderHarness.WithForm(true, form => Assert.False(RecorderHarness.HeaderBounds(form).IntersectsWith(RecorderHarness.SettingsBounds(form))));

    [Fact]
    public void InputModeVisible() => RecorderHarness.WithForm(false, form => Assert.True(RecorderHarness.Named(form, "InputMode").Visible));

    [Fact]
    public void RawOptionVisibleAndSelectable() => RecorderHarness.WithForm(false, form =>
    {
        ComboBox input = Assert.IsType<ComboBox>(RecorderHarness.Named(form, "InputMode"));
        int rawIndex = Enumerable.Range(0, input.Items.Count).First(index => input.Items[index]?.ToString()?.Contains("Raw", StringComparison.OrdinalIgnoreCase) == true);
        input.SelectedIndex = rawIndex;
        Assert.Equal(rawIndex, input.SelectedIndex);
    });

    [Fact]
    public void WindowBehaviorVisible() => RecorderHarness.WithForm(false, form => Assert.True(RecorderHarness.Named(form, "WindowBehavior").Visible));

    [Fact]
    public void MonitorToggleVisible() => RecorderHarness.WithForm(false, form => Assert.True(RecorderHarness.Named(form, "ShowMonitor").Visible));

    [Fact]
    public void RestoreDefaultsVisible() => RecorderHarness.WithForm(false, form => Assert.True(RecorderHarness.Named(form, "RestoreDefaults").Visible));

    [Fact]
    public void FooterVisible() => RecorderHarness.WithForm(false, form => Assert.True(RecorderHarness.Named(form, "Footer").Visible));

    [Fact]
    public void MonitorCollapseRelayouts() => RecorderHarness.WithForm(false, form =>
    {
        CheckBox toggle = Assert.IsType<CheckBox>(RecorderHarness.Named(form, "ShowMonitor"));
        toggle.Checked = true;
        form.PerformLayout();
        int expanded = form.PreferredSize.Height;
        toggle.Checked = false;
        form.PerformLayout();
        Assert.True(form.PreferredSize.Height <= expanded);
    });

    [Fact]
    public void CoreControlsInsideClientArea() => RecorderHarness.WithForm(false, form =>
    {
        foreach (Control control in RecorderHarness.CoreControls(form))
        {
            Assert.True(form.RectangleToScreen(form.ClientRectangle).Contains(control.RectangleToScreen(control.ClientRectangle)), control.Name);
        }
    });

    [Fact]
    public void NoControlOverlap() => RecorderHarness.WithForm(true, form => RecorderHarness.AssertNoCoreOverlap(form));

    [Fact]
    public void AutoScrollWorks() => RecorderHarness.WithForm(false, form =>
    {
        Assert.True(RecorderHarness.Descendants(form).OfType<ScrollableControl>().Any(control => control.AutoScroll), "Recorder has no AutoScroll viewport.");
        foreach (Size size in new[] { new Size(1280, 720), new Size(1024, 650), new Size(760, 540) })
        {
            form.ClientSize = size;
            form.PerformLayout();
            Assert.All(RecorderHarness.CoreControls(form).Where(control => control.Visible), control =>
                Assert.True(control.Width > 0 && control.Height > 0, $"{size.Width}x{size.Height}: {control.Name}={control.Size}"));
        }
    });
}

public sealed class PlayerFinalUiBehaviorTests
{
    [Fact]
    public void DesktopOnlyScopeVisible() => PlayerHarness.WithForm(form =>
    {
        Assert.True(form.CoreControls["DesktopScopeTitle"].Visible);
        Assert.Contains("直接重播於目前桌面", form.CoreControls["DesktopScopeTitle"].Text);
        Assert.False(form.CoreControls.ContainsKey("TargetList"));
    });

    [Fact]
    public void CountdownOptionsVisible() => PlayerHarness.WithForm(form =>
    {
        ComboBox modes = Assert.IsType<ComboBox>(form.CoreControls["CountdownMode"]);
        Assert.True(modes.Visible);
        Assert.Equal(2, modes.Items.Count);
    });

    [Fact]
    public void DetailsReadable() => PlayerHarness.WithForm(form =>
    {
        TextBox details = Assert.IsType<TextBox>(form.CoreControls["DetailsText"]);
        Assert.True(details.Multiline);
        Assert.True(details.ReadOnly);
        Assert.True(details.Height >= 80);
    });

    [Fact]
    public void StartButtonVisible() => PlayerHarness.WithForm(form => Assert.True(form.CoreControls["StartButton"].Visible));

    [Fact]
    public void ElevationButtonInitiallyHidden() => PlayerHarness.WithForm(form =>
    {
        Control elevation = RecorderHarness.Named(form, "ElevateButton");
        Assert.False(elevation.Visible);
    });

    [Fact]
    public void StatusVisible() => PlayerHarness.WithForm(form => Assert.True(form.CoreControls["Status"].Visible));

    [Fact]
    public void CoreControlsInsideClientArea() => PlayerHarness.WithForm(form =>
    {
        Rectangle client = form.RectangleToScreen(form.ClientRectangle);
        Assert.All(form.CoreControls.Values, control => Assert.True(client.Contains(control.RectangleToScreen(control.ClientRectangle)), control.Name));
    });

    [Fact]
    public void NoControlOverlap() => PlayerHarness.WithForm(form =>
    {
        Control[] controls = form.CoreControls.Values.ToArray();
        for (int left = 0; left < controls.Length; left++)
        {
            Rectangle a = form.RectangleToClient(controls[left].RectangleToScreen(controls[left].ClientRectangle));
            for (int right = left + 1; right < controls.Length; right++)
            {
                if (IsAncestor(controls[left], controls[right]) || IsAncestor(controls[right], controls[left])) continue;
                Rectangle b = form.RectangleToClient(controls[right].RectangleToScreen(controls[right].ClientRectangle));
                Assert.False(Rectangle.Intersect(a, b) is { Width: > 2, Height: > 2 }, $"{controls[left].Name}/{controls[right].Name}");
            }
        }
    });

    private static bool IsAncestor(Control candidate, Control control)
    {
        for (Control? parent = control.Parent; parent is not null; parent = parent.Parent)
        {
            if (ReferenceEquals(parent, candidate)) return true;
        }
        return false;
    }
}

public sealed class DesktopPlaybackModeBehaviorTests
{
    [Fact]
    public async Task KeepVisibleRelinquishesKeyboardFocus()
    {
        Fixture fixture = Fixture.Create();
        await fixture.Run(PlayerCountdownMode.KeepVisible);
        Assert.Equal(1, fixture.Foreground.ActivateCalls);
        Assert.Equal(PlayerCountdownMode.KeepVisible, fixture.Window.PreparedMode);
    }

    [Fact]
    public void KeepVisibleMainFormRemainsVisible() => PlayerHarness.WithPlainForm(form =>
    {
        Rectangle bounds = form.Bounds;
        nint handle = form.Handle;
        WinFormsPlayerWindowModeService service = new(form);
        service.PrepareAsync(PlayerCountdownMode.KeepVisible, PlaybackExecutionContext.Standard, CancellationToken.None).GetAwaiter().GetResult();
        Assert.True(form.Visible);
        Assert.NotEqual(FormWindowState.Minimized, form.WindowState);
        Assert.Equal(bounds, form.Bounds);
        Assert.Equal(handle, form.Handle);
        Assert.True(form.Enabled);
        Assert.True(service.ClickThroughActive);
        service.RestoreAsync().GetAwaiter().GetResult();
        Assert.True(form.Enabled);
    });

    [Fact]
    public void KeepVisibleNoActivateAndTransparent() => PlayerHarness.WithPlainForm(form =>
    {
        WinFormsPlayerWindowModeService service = new(form);
        service.PrepareAsync(PlayerCountdownMode.KeepVisible, PlaybackExecutionContext.Standard, CancellationToken.None).GetAwaiter().GetResult();
        long style = service.CurrentExtendedStyle.ToInt64();
        Assert.NotEqual(0, style & PlaybackOverlayWindowPolicy.NoActivate);
        Assert.NotEqual(0, style & PlaybackOverlayWindowPolicy.Transparent);
        Assert.True(PlayerWindowClickThroughPolicy.ShouldReturnTransparent(PlayerWindowClickThroughPolicy.WindowNcHitTest, service.ClickThroughActive));
        service.RestoreAsync().GetAwaiter().GetResult();
    });

    [Fact]
    public async Task CountdownForegroundChangesDoNotBlock()
    {
        Fixture fixture = Fixture.Create();
        fixture.PreferredForeground = null;
        fixture.Countdown.ChangeForegroundBeforeFinal = true;
        Assert.True((await fixture.Run(PlayerCountdownMode.KeepVisible)).Completed);
    }

    [Fact]
    public async Task KeepVisibleFirstEventSent()
    {
        Fixture fixture = Fixture.Create();
        await fixture.Run(PlayerCountdownMode.KeepVisible);
        Assert.Equal(1, fixture.Log.FirstCount);
    }

    [Fact]
    public async Task KeepVisibleCompletes() => Assert.True((await Fixture.Create().Run(PlayerCountdownMode.KeepVisible)).Completed);

    [Fact]
    public async Task KeepVisibleRestoresInteractiveStyle()
    {
        Fixture fixture = Fixture.Create();
        await fixture.Run(PlayerCountdownMode.KeepVisible);
        Assert.True(fixture.Window.Restored);
    }

    [Fact]
    public async Task FirstMouseDoesNotForceForeground()
    {
        Fixture fixture = Fixture.Create();
        await fixture.Run(PlayerCountdownMode.KeepVisible, macro: Fixture.Macro(firstKind: PlaybackEventKind.MouseMove));
        Assert.Equal(0, fixture.Foreground.ActivateCalls);
    }

    [Fact]
    public async Task FirstKeyboardWithoutPreferredWindowStillStarts()
    {
        Fixture fixture = Fixture.Create();
        fixture.PreferredForeground = null;
        Assert.True((await fixture.Run(PlayerCountdownMode.KeepVisible)).Completed);
    }

    [Fact]
    public async Task MinimizeHappensBeforeCountdown()
    {
        Fixture fixture = Fixture.Create();
        await fixture.Run(PlayerCountdownMode.MinimizeBeforeCountdown);
        Assert.True(fixture.Order.IndexOf("prepare") < fixture.Order.IndexOf("countdown"));
    }

    [Fact]
    public void MinimizeOnlyPlayer() => PlayerHarness.WithPlainForm(form =>
    {
        WinFormsPlayerWindowModeService service = new(form);
        service.PrepareAsync(PlayerCountdownMode.MinimizeBeforeCountdown, PlaybackExecutionContext.Standard, CancellationToken.None).GetAwaiter().GetResult();
        Assert.True(form.Visible);
        Assert.Equal(FormWindowState.Minimized, form.WindowState);
        service.RestoreAsync().GetAwaiter().GetResult();
        Assert.Equal(FormWindowState.Normal, form.WindowState);
    });

    [Fact]
    public async Task MinimizeFirstEventSent()
    {
        Fixture fixture = Fixture.Create();
        await fixture.Run(PlayerCountdownMode.MinimizeBeforeCountdown);
        Assert.Equal(1, fixture.Log.FirstCount);
    }

    [Fact]
    public async Task MinimizeCompletes() => Assert.True((await Fixture.Create().Run(PlayerCountdownMode.MinimizeBeforeCountdown)).Completed);

    [Fact]
    public async Task DuplicateStartIgnored()
    {
        Fixture fixture = Fixture.Create();
        fixture.Countdown.Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<PlaybackRunResult> first = fixture.Run(PlayerCountdownMode.KeepVisible);
        await fixture.Countdown.Entered.Task;
        PlaybackRunResult duplicate = await fixture.Run(PlayerCountdownMode.KeepVisible);
        Assert.Contains("已有", duplicate.Message);
        fixture.Countdown.Block.SetResult();
        await first;
    }

    [Fact]
    public async Task CancelDuringCountdown()
    {
        Fixture fixture = Fixture.Create();
        using CancellationTokenSource cancellation = new();
        fixture.Countdown.OnEnter = cancellation.Cancel;
        Assert.True((await fixture.Run(PlayerCountdownMode.KeepVisible, cancellation.Token)).Cancelled);
    }

    [Fact]
    public async Task F11DuringPlayback()
    {
        Fixture fixture = Fixture.Create();
        fixture.Factory.Session.WaitForStop = true;
        using CancellationTokenSource f11 = new();
        Task<PlaybackRunResult> run = fixture.Run(PlayerCountdownMode.KeepVisible, f11.Token);
        await fixture.Factory.Session.Entered.Task;
        f11.Cancel();
        Assert.True((await run).Cancelled);
    }

    [Fact]
    public async Task ExceptionReleasesInputs()
    {
        Fixture fixture = Fixture.Create();
        fixture.Factory.Session.Throw = true;
        Assert.False((await fixture.Run(PlayerCountdownMode.KeepVisible)).Completed);
        Assert.True(fixture.Factory.Session.Disposed);
    }

    [Fact]
    public async Task CompletionCanPlayAgain()
    {
        Fixture fixture = Fixture.Create();
        Assert.True((await fixture.Run(PlayerCountdownMode.KeepVisible)).Completed);
        Assert.True((await fixture.Run(PlayerCountdownMode.MinimizeBeforeCountdown)).Completed);
        Assert.Equal(2, fixture.Factory.CreateCount);
    }

    [Fact]
    public async Task CloseDuringPlaybackSafe()
    {
        Fixture fixture = Fixture.Create();
        fixture.Factory.Session.WaitForStop = true;
        using CancellationTokenSource closing = new();
        Task<PlaybackRunResult> run = fixture.Run(PlayerCountdownMode.KeepVisible, closing.Token);
        await fixture.Factory.Session.Entered.Task;
        closing.Cancel();
        Assert.True((await run).Cancelled);
        Assert.True(fixture.Window.Restored);
    }

    [Fact]
    public async Task PlaybackLogRequiresFirstEvent()
    {
        Fixture fixture = Fixture.Create();
        fixture.Factory.Session.EmitFirst = false;
        PlaybackRunResult result = await fixture.Run(PlayerCountdownMode.KeepVisible);
        Assert.False(result.Completed);
        Assert.Contains("第一個事件", result.Message);
    }

    [Fact]
    public async Task GeneralPlayerBlocksAdminMacro()
    {
        Fixture fixture = Fixture.Create();
        PlaybackRunResult result = await fixture.Run(PlayerCountdownMode.KeepVisible, macro: Fixture.Macro(requiresElevation: true), elevated: false);
        Assert.False(result.Completed);
        Assert.Equal(0, fixture.Factory.CreateCount);
    }

    [Fact]
    public async Task HighPlayerAllowsAdminMacro()
    {
        Fixture fixture = Fixture.Create();
        PlaybackMacroDocument macro = Fixture.LoadAdminMacroOrFallback();
        fixture.Factory.Session.EventsToSend = macro.Events.Count;
        PlaybackRunResult result = await fixture.Run(PlayerCountdownMode.KeepVisible, macro: macro, elevated: true);
        Assert.True(result.Completed);
        Assert.Equal(macro.Events.Count, result.EventsSent);
    }

    [Fact]
    public void UacCancelPreservesNormalPlayer()
    {
        Assert.True(PlayerElevationPolicy.IsUserCancellation(new Win32Exception(1223)));
        Assert.False(PlayerElevationPolicy.IsUserCancellation(new Win32Exception(5)));
    }
}

internal static class RecorderHarness
{
    public static IEnumerable<Control> Descendants(Control root)
    {
        yield return root;
        foreach (Control child in root.Controls)
        {
            foreach (Control descendant in Descendants(child)) yield return descendant;
        }
    }

    public static void WithForm(bool high, Action<Form> assertion) => Sta.Run(() =>
    {
        using Form form = Create(high);
        form.ClientSize = new Size(980, 720);
        form.Show();
        form.PerformLayout();
        Application.DoEvents();
        assertion(form);
        form.Close();
    });

    public static Control Named(Control root, string name) =>
        Find(root, control => control.Name.Equals(name, StringComparison.Ordinal)) ?? throw new Xunit.Sdk.XunitException("Missing control: " + name);

    public static Rectangle HeaderBounds(Form form) => Bounds(form, Find(form, control => control.Name.Contains("Privilege", StringComparison.OrdinalIgnoreCase) || (control is Label && control.Text.Contains("Integrity", StringComparison.OrdinalIgnoreCase)))!);
    public static Rectangle SettingsBounds(Form form) => Bounds(form, Find(form, control => control is GroupBox && control.Text.Contains("設定", StringComparison.Ordinal))!);

    public static IReadOnlyList<Control> CoreControls(Form form)
    {
        PropertyInfo? property = form.GetType().GetProperty("CoreControls", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property?.GetValue(form) is IEnumerable entries)
        {
            List<Control> controls = new();
            foreach (object entry in entries)
            {
                if (entry.GetType().GetProperty("Value")?.GetValue(entry) is Control control)
                {
                    controls.Add(control);
                }
            }
            if (controls.Count > 0)
            {
                return controls.Distinct().ToArray();
            }
        }

        return new[] { Named(form, "InputMode"), Named(form, "WindowBehavior"), Named(form, "ShowMonitor"), Named(form, "RestoreDefaults"), Named(form, "Footer") };
    }

    public static void AssertNoCoreOverlap(Form form)
    {
        Control[] controls = CoreControls(form).Where(control => control.Visible).ToArray();
        for (int left = 0; left < controls.Length; left++)
        {
            Rectangle a = Bounds(form, controls[left]);
            for (int right = left + 1; right < controls.Length; right++)
            {
                if (IsAncestor(controls[left], controls[right]) || IsAncestor(controls[right], controls[left])) continue;
                Rectangle b = Bounds(form, controls[right]);
                Assert.False(Rectangle.Intersect(a, b) is { Width: > 2, Height: > 2 }, $"{controls[left].Name}/{controls[right].Name}");
            }
        }
    }

    private static bool IsAncestor(Control candidate, Control control)
    {
        for (Control? parent = control.Parent; parent is not null; parent = parent.Parent)
        {
            if (ReferenceEquals(parent, candidate)) return true;
        }
        return false;
    }

    private static Form Create(bool high)
    {
        Assembly assembly = typeof(RecorderPrivilegeDisplayModel).Assembly;
        Type modelType = typeof(RecorderPrivilegeDisplayModel);
        MethodInfo factory = modelType.GetMethod("ForProbe", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(modelType.FullName, "ForProbe");
        object?[] modelArguments = factory.GetParameters().Select(parameter =>
            parameter.ParameterType == typeof(bool) ? (object)high :
            parameter.ParameterType == typeof(string) ? (high ? "high" : "medium") :
            parameter.ParameterType.IsEnum ? Enum.Parse(parameter.ParameterType, high ? "High" : "Medium", true) :
            Activator.CreateInstance(parameter.ParameterType)).ToArray();
        object model = factory.Invoke(null, modelArguments)!;
        Type formType = assembly.GetType("MacroRecorder.MainForm", true)!;
        foreach (ConstructorInfo constructor in formType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                     .Where(item => item.GetParameters().Any(parameter => parameter.ParameterType.IsInstanceOfType(model)))
                     .OrderByDescending(item => item.GetParameters().Length))
        {
            try
            {
                object?[] arguments = constructor.GetParameters().Select(parameter =>
                {
                    if (parameter.ParameterType.IsInstanceOfType(model)) return model;
                    if (parameter.ParameterType == typeof(bool)) return (object)false;
                    if (parameter.HasDefaultValue) return parameter.DefaultValue;
                    return parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null;
                }).ToArray();
                return (Form)constructor.Invoke(arguments);
            }
            catch (TargetInvocationException)
            {
            }
        }

        throw new Xunit.Sdk.XunitException("No runtime-disabled Recorder form constructor could be invoked.");
    }

    private static Control? Find(Control root, Func<Control, bool> predicate)
    {
        if (predicate(root)) return root;
        foreach (Control child in root.Controls)
        {
            Control? found = Find(child, predicate);
            if (found is not null) return found;
        }
        return null;
    }

    private static Rectangle Bounds(Form form, Control control) => form.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
}

internal static class PlayerHarness
{
    public static void WithForm(Action<PlaybackLibraryForm> assertion) => Sta.Run(() =>
    {
        string root = Path.Combine(ProjectLocalTestSandbox.Create(), "player-ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            MacroCore.Runtime.RootMarker.Ensure(root);
            PlayerRuntimePaths.Initialize(root);
            using PlaybackLibraryForm form = new(new PlayerLaunchOptions(root, null, "desktop-player", null, false), false);
            form.Show();
            form.PerformLayout();
            Application.DoEvents();
            assertion(form);
            form.Close();
        }
        finally
        {
            TestProjectEnvironment.ResetPlayerRuntimePaths();
        }
    });

    public static void WithPlainForm(Action<Form> assertion) => Sta.Run(() =>
    {
        using Form form = new() { ClientSize = new Size(480, 240) };
        form.Show();
        Application.DoEvents();
        assertion(form);
        form.Close();
    });
}

internal static class Sta
{
    public static void Run(Action action)
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "STA UI test timed out.");
        if (failure is not null) throw failure;
    }
}

internal sealed class Fixture
{
    public List<string> Order { get; } = new();
    public FakeForeground Foreground { get; } = new();
    public FakeWindowMode Window { get; }
    public FakeCountdown Countdown { get; }
    public FakeFactory Factory { get; } = new();
    public FakeLog Log { get; } = new();
    public PlaybackStartController Controller { get; }
    public ForegroundSnapshot? PreferredForeground { get; set; } = Target();

    private Fixture()
    {
        Window = new FakeWindowMode(Order);
        Countdown = new FakeCountdown(Order, Foreground);
        Controller = new PlaybackStartController(Foreground, Window, Countdown, Factory, Log, new FakeOverlay(), () => PreferredForeground);
    }

    public static Fixture Create() => new();

    public Task<PlaybackRunResult> Run(
        PlayerCountdownMode mode,
        CancellationToken cancellationToken = default,
        PlaybackMacroDocument? macro = null,
        bool elevated = false) =>
        Controller.StartAsync(
            macro ?? Macro(),
            mode,
            elevated,
            cancellationToken);

    public static ForegroundSnapshot Target() => new(new nint(101), 2001, "target.exe", 0x2000);
    public static ForegroundSnapshot OtherTarget() => new(new nint(102), 2002, "other.exe", 0x2000);

    public static PlaybackMacroDocument Macro(bool? requiresElevation = false, string processName = "target.exe", PlaybackEventKind firstKind = PlaybackEventKind.KeyDown) => new(
        "fixture.macro", "1.1", "Fixture", DateTimeOffset.UtcNow, 20, requiresElevation, "DesktopSafe",
        processName, "Target Window", "1920 x 1080", new[]
        {
            new PlaybackMacroEvent(0, firstKind, 65, 30, false, 0, 0, string.Empty, 0),
            new PlaybackMacroEvent(20, PlaybackEventKind.KeyUp, 65, 30, false, 0, 0, string.Empty, 0)
        });

    public static PlaybackMacroDocument LoadAdminMacroOrFallback()
    {
        return Macro(true);
    }

    public static string FindProjectRoot()
    {
        return TestProjectEnvironment.Root;
    }
}

internal sealed class FakeForeground : IForegroundWindowService
{
    public ForegroundSnapshot? Current { get; set; } = Fixture.Target();
    public bool SecureDesktop { get; set; }
    public bool ActivationSucceeds { get; set; } = true;
    public int ActivateCalls { get; private set; }
    public int CaptureCalls { get; private set; }
    public ForegroundSnapshot? CaptureCurrent() { CaptureCalls++; return Current; }
    public bool TryActivate(ForegroundSnapshot snapshot) { ActivateCalls++; return ActivationSucceeds && !SecureDesktop; }
    public bool IsSecureDesktop(out string reason)
    {
        reason = SecureDesktop ? "安全桌面禁止" : string.Empty;
        return SecureDesktop;
    }
    public nint GetForegroundWindowHandleFast() => Current?.WindowHandle ?? nint.Zero;
}

internal sealed class FakeWindowMode : IPlayerWindowModeService
{
    private readonly List<string> _order;
    public FakeWindowMode(List<string> order) => _order = order;
    public PlayerCountdownMode? PreparedMode { get; private set; }
    public int PrepareCount { get; private set; }
    public bool Restored { get; private set; }
    public PlaybackExecutionContext? Context { get; private set; }
    public Task PrepareAsync(PlayerCountdownMode mode, PlaybackExecutionContext context, CancellationToken cancellationToken)
    {
        PrepareCount++; PreparedMode = mode; Context = context; _order.Add("prepare"); return Task.CompletedTask;
    }
    public Task RestoreAsync() { Restored = true; _order.Add("restore"); return Task.CompletedTask; }
}

internal sealed class FakeCountdown : ICountdownService
{
    private readonly List<string> _order;
    private readonly FakeForeground _foreground;
    public FakeCountdown(List<string> order, FakeForeground foreground) { _order = order; _foreground = foreground; }
    public bool ChangeForegroundBeforeFinal { get; set; }
    public int RunCount { get; private set; }
    public Action? OnEnter { get; set; }
    public TaskCompletionSource? Block { get; set; }
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task RunAsync(int seconds, Action<int> tick, CancellationToken cancellationToken)
    {
        RunCount++; _order.Add("countdown"); OnEnter?.Invoke(); Entered.TrySetResult();
        if (Block is not null) await Block.Task.WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        for (int value = seconds; value >= 1; value--) tick(value);
        if (ChangeForegroundBeforeFinal) _foreground.Current = null;
    }
}

internal sealed class FakeFactory : IPlaybackServiceFactory
{
    public int CreateCount { get; private set; }
    public PlaybackExecutionContext? LastContext { get; private set; }
    public IPlaybackFocusPolicy? LastPolicy { get; private set; }
    public FakeSession Session { get; } = new();
    public IPlaybackSession Create(PlaybackMacroDocument macro, PlaybackExecutionContext context, IPlaybackFocusPolicy focusPolicy)
    {
        CreateCount++; LastContext = context; LastPolicy = focusPolicy;
        if (Session.EventsToSend == 0) Session.EventsToSend = macro.Events.Count;
        return Session;
    }
}

internal sealed class FakeSession : IPlaybackSession
{
    private readonly CancellationTokenSource _stop = new();
    public event EventHandler? FirstEventSent;
    public event EventHandler<PlaybackProgress>? ProgressChanged;
    public bool FirstEventWasSent { get; private set; }
    public int EventsSentCount { get; private set; }
    public int FocusChangeCount { get; set; }
    public bool EmitFirst { get; set; } = true;
    public bool WaitForStop { get; set; }
    public bool Throw { get; set; }
    public bool Disposed { get; private set; }
    public int EventsToSend { get; set; }
    public PlaybackRunResult? ForcedResult { get; set; }
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task<PlaybackRunResult> PlayAsync(CancellationToken cancellationToken)
    {
        Entered.TrySetResult();
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        if (WaitForStop)
        {
            try { await Task.Delay(Timeout.Infinite, linked.Token); }
            catch (OperationCanceledException) { return PlaybackRunResult.Stopped(EventsSentCount, "播放已停止"); }
        }
        if (Throw) throw new InvalidOperationException("fixture exception");
        EventsSentCount = EventsToSend;
        if (EmitFirst)
        {
            FirstEventWasSent = true;
            FirstEventSent?.Invoke(this, EventArgs.Empty);
        }
        ProgressChanged?.Invoke(this, new PlaybackProgress(EventsSentCount, EventsSentCount, TimeSpan.Zero));
        return ForcedResult ?? PlaybackRunResult.Success(EventsSentCount, FocusChangeCount);
    }
    public void Stop() => _stop.Cancel();
    public void Dispose() { Disposed = true; _stop.Cancel(); }
}

internal sealed class FakeLog : IPlaybackSessionLog
{
    public int FirstCount { get; private set; }
    public List<string> Phases { get; } = new();
    public PlaybackExecutionContext? StartContext { get; private set; }
    public string? EndDisposition { get; private set; }
    public int EndFocusChangeCount { get; private set; }
    public void SessionStarted(PlaybackMacroDocument macro, PlaybackExecutionContext context, PlayerCountdownMode mode) => StartContext = context;
    public void Phase(string phase) => Phases.Add(phase);
    public void FirstEventSent() => FirstCount++;
    public void SessionEnded(string disposition, int sentCount, int focusChangeCount, string? detail = null)
    {
        EndDisposition = disposition;
        EndFocusChangeCount = focusChangeCount;
    }
}

internal sealed class FakeOverlay : IOverlayService
{
    public void ShowCountdown(string macroName, int seconds) { }
    public void ShowPlaying(string macroName, PlaybackProgress progress) { }
    public void Close() { }
}

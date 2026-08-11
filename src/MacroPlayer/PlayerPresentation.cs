using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using MacroCore.Diagnostics;

namespace MacroPlayer;

public readonly record struct PlayerNativeWindowPlacement(bool IsMinimized, Rectangle NormalBounds);

public sealed record PlayerWindowStateSnapshot(
    nint Handle,
    bool Visible,
    FormWindowState WindowState,
    Rectangle Bounds,
    bool TopMost,
    bool Enabled,
    nint Style,
    nint ExtendedStyle,
    nint Owner,
    IReadOnlyDictionary<Control, bool> ControlEnabledStates);

public interface IPlayerPresentationService : IDisposable
{
    PlayerWindowStateSnapshot? Snapshot { get; }
    bool IsActive { get; }
    int SelfCommandBlockedCount { get; }
    int InvariantRepairCount { get; }
    bool OverlayUsed { get; }
    event Action<string>? HealthFailed;
    Task PrepareAsync(CancellationToken cancellationToken);
    Task RestoreAsync();
}

public sealed class PlayerWindowSelfProtection : NativeWindow, IDisposable
{
    public const int WindowNcHitTest = 0x0084;
    public const int WindowSystemCommand = 0x0112;
    public const int SystemCommandMinimize = 0xF020;
    public const int SystemCommandMaximize = 0xF030;
    public const int SystemCommandClose = 0xF060;
    public const int HitTestTransparent = -1;

    private static readonly ConditionalWeakTable<Form, PlayerWindowSelfProtection> Registry = new();
    private readonly Form _form;
    private readonly Action<string> _diagnostic;
    private int _allowInternalClose;
    private int _disposed;

    public PlayerWindowSelfProtection(Form form, Action<string> diagnostic)
    {
        _form = form;
        _diagnostic = diagnostic;
        Registry.Remove(form);
        Registry.Add(form, this);
        _form.FormClosing += OnFormClosing;
        AssignHandle(form.Handle);
    }

    public bool Active { get; set; }
    public int BlockedCommandCount { get; private set; }

    public static void AllowNextInternalClose(Form form)
    {
        if (Registry.TryGetValue(form, out PlayerWindowSelfProtection? protection))
        {
            protection._allowInternalClose = 1;
        }
    }

    public bool DispatchForTest(int message, nint wParam, out nint result)
    {
        result = nint.Zero;
        if (!Active)
        {
            return false;
        }
        if (message == WindowNcHitTest)
        {
            result = new nint(HitTestTransparent);
            return true;
        }
        if (message == WindowSystemCommand && IsBlockedSystemCommand(wParam.ToInt32() & 0xFFF0))
        {
            BlockedCommandCount++;
            _diagnostic($"KEEP_VISIBLE_SELF_COMMAND_BLOCKED command=0x{(wParam.ToInt32() & 0xFFF0):X4}");
            return true;
        }
        return false;
    }

    protected override void WndProc(ref Message message)
    {
        if (DispatchForTest(message.Msg, message.WParam, out nint result))
        {
            message.Result = result;
            return;
        }
        base.WndProc(ref message);
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs args)
    {
        if (!Active || args.CloseReason != CloseReason.UserClosing)
        {
            return;
        }
        if (Interlocked.Exchange(ref _allowInternalClose, 0) != 0)
        {
            return;
        }
        args.Cancel = true;
        BlockedCommandCount++;
        _diagnostic("KEEP_VISIBLE_SELF_COMMAND_BLOCKED command=FORM_USER_CLOSE");
    }

    private static bool IsBlockedSystemCommand(int command) =>
        command is SystemCommandMinimize or SystemCommandMaximize or SystemCommandClose;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        Active = false;
        _form.FormClosing -= OnFormClosing;
        Registry.Remove(_form);
        if (Handle != nint.Zero)
        {
            ReleaseHandle();
        }
        GC.SuppressFinalize(this);
    }
}

public sealed class PlayerPresentationHealthMonitor : IDisposable
{
    private readonly Form _form;
    private readonly Func<bool> _checkAndRepair;
    private readonly Action<string> _failure;
    private readonly System.Threading.Timer _timer;
    private int _checking;
    private int _disposed;

    public PlayerPresentationHealthMonitor(
        Form form,
        Func<bool> checkAndRepair,
        Action<string> failure,
        TimeSpan? interval = null,
        bool startImmediately = true)
    {
        _form = form;
        _checkAndRepair = checkAndRepair;
        _failure = failure;
        TimeSpan period = interval ?? TimeSpan.FromMilliseconds(150);
        _timer = new System.Threading.Timer(_ => QueueCheck(), null,
            startImmediately ? period : Timeout.InfiniteTimeSpan,
            startImmediately ? period : Timeout.InfiniteTimeSpan);
    }

    public bool CheckNow()
    {
        try
        {
            return _checkAndRepair();
        }
        catch (Exception exception)
        {
            _failure(exception.Message);
            return false;
        }
    }

    private void QueueCheck()
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _checking, 1) != 0)
        {
            return;
        }
        try
        {
            if (_form.IsDisposed || !_form.IsHandleCreated)
            {
                _failure("Player 主視窗 handle 已失效。");
                return;
            }
            _form.BeginInvoke((MethodInvoker)(() =>
            {
                try { CheckNow(); }
                finally { Interlocked.Exchange(ref _checking, 0); }
            }));
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _checking, 0);
            _failure(exception.Message);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _timer.Dispose();
        }
    }
}

public sealed class KeepVisiblePresentation : IPlayerPresentationService
{
    public const long NoActivateStyle = 0x08000000L;
    public const long TransparentStyle = 0x00000020L;
    public const long LayeredStyle = 0x00080000L;

    private readonly Form _form;
    private readonly IPlayerWindowNativeApi _native;
    private PlayerWindowSelfProtection? _selfProtection;
    private PlayerPresentationHealthMonitor? _healthMonitor;
    private int _restored;

    public KeepVisiblePresentation(Form form, IPlayerWindowNativeApi native)
    {
        _form = form;
        _native = native;
    }

    public PlayerWindowStateSnapshot? Snapshot { get; private set; }
    public bool IsActive { get; private set; }
    public int SelfCommandBlockedCount => _selfProtection?.BlockedCommandCount ?? 0;
    public int InvariantRepairCount { get; private set; }
    public bool OverlayUsed => false;
    public bool ClickThroughActive => _selfProtection?.Active == true;
    public PlayerWindowSelfProtection? SelfProtectionForTests => _selfProtection;
    public event Action<string>? HealthFailed;

    public Task PrepareAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsActive)
        {
            throw new InvalidOperationException("KeepVisible presentation 已在使用中。");
        }

        nint handle = _form.Handle;
        Dictionary<Control, bool> controls = Descendants(_form).ToDictionary(control => control, control => control.Enabled);
        Snapshot = new PlayerWindowStateSnapshot(
            handle,
            _form.Visible,
            _form.WindowState,
            _form.Bounds,
            _form.TopMost,
            _form.Enabled,
            _native.GetStyle(handle),
            _native.GetExtendedStyle(handle),
            _native.GetOwner(handle),
            new ReadOnlyDictionary<Control, bool>(controls));

        _selfProtection = new PlayerWindowSelfProtection(_form, PlayerPresentationDiagnostics.Write) { Active = true };
        DisableInteractiveControls(controls.Keys);
        _form.Enabled = true;
        if (!_form.Visible)
        {
            _form.Visible = true;
        }
        if (_form.WindowState == FormWindowState.Minimized)
        {
            _form.WindowState = FormWindowState.Normal;
        }
        if (_form.Handle != handle)
        {
            throw new InvalidOperationException("KeepVisible 不得重建 Player 主視窗 handle。");
        }

        ApplyRequiredNativeState(handle, Snapshot.ExtendedStyle);
        IsActive = true;
        PlayerPresentationDiagnostics.Write(
            $"KEEP_VISIBLE_BEGIN requestedPresentation=KeepVisible appliedPresentation=KeepVisible mainHwnd=0x{handle.ToInt64():X} " +
            $"visibleAtStart={Snapshot.Visible} visibleDuringPlayback={_form.Visible} windowStateAtStart={Snapshot.WindowState} " +
            $"windowStateDuringPlayback={_form.WindowState} topMostDuringPlayback={_native.IsTopMost(handle)} " +
            "noActivateActive=True transparentActive=True layeredActive=True overlayUsed=False");
        PlayerPresentationDiagnostics.Write($"KEEP_VISIBLE_TOPMOST_APPLIED mainHwnd=0x{handle.ToInt64():X}");
        _healthMonitor = new PlayerPresentationHealthMonitor(_form, CheckAndRepair, OnHealthFailure);
        return Task.CompletedTask;
    }

    public bool CheckHealthNow() => _healthMonitor?.CheckNow() ?? CheckAndRepair();

    private bool CheckAndRepair()
    {
        if (!IsActive || Snapshot is null)
        {
            return true;
        }
        nint handle = Snapshot.Handle;
        if (_form.IsDisposed || !_form.IsHandleCreated || _form.Handle != handle || !_native.IsWindow(handle))
        {
            OnHealthFailure("Player 主視窗 handle 已毀損或改變。");
            return false;
        }

        bool repaired = false;
        long required = NoActivateStyle | TransparentStyle | LayeredStyle;
        nint currentStyle = _native.GetExtendedStyle(handle);
        if ((currentStyle.ToInt64() & required) != required)
        {
            _native.SetExtendedStyle(handle, new nint(currentStyle.ToInt64() | required));
            _native.RefreshFrame(handle);
            _native.SetLayeredOpaque(handle);
            repaired = true;
        }
        if (!_native.IsWindowVisible(handle) || !_form.Visible)
        {
            _form.Visible = true;
            repaired = true;
        }
        if (_native.IsIconic(handle) || _form.WindowState == FormWindowState.Minimized)
        {
            _form.WindowState = Snapshot.WindowState == FormWindowState.Maximized
                ? FormWindowState.Maximized
                : FormWindowState.Normal;
            repaired = true;
        }
        Rectangle nativeBounds = _native.GetWindowRectangle(handle);
        if (nativeBounds != Rectangle.Empty && nativeBounds != Snapshot.Bounds)
        {
            _form.WindowState = FormWindowState.Normal;
            _form.Bounds = Snapshot.Bounds;
            if (Snapshot.WindowState == FormWindowState.Maximized)
            {
                _form.WindowState = FormWindowState.Maximized;
            }
            repaired = true;
        }
        if (!_native.IsTopMost(handle))
        {
            _native.SetTopMost(handle, true);
            repaired = true;
        }
        if (repaired)
        {
            InvariantRepairCount++;
            PlayerPresentationDiagnostics.Write($"KEEP_VISIBLE_INVARIANT_REPAIRED mainHwnd=0x{handle.ToInt64():X} repairCount={InvariantRepairCount}");
        }
        return true;
    }

    private void ApplyRequiredNativeState(nint handle, nint originalExtendedStyle)
    {
        nint playbackStyle = new(originalExtendedStyle.ToInt64() | NoActivateStyle | TransparentStyle | LayeredStyle);
        _native.SetExtendedStyle(handle, playbackStyle);
        _native.SetLayeredOpaque(handle);
        _native.RefreshFrame(handle);
        _native.SetTopMost(handle, true);
    }

    private void OnHealthFailure(string reason)
    {
        PlayerPresentationDiagnostics.Write("KEEP_VISIBLE_HEALTH_FAILED reason=" + Sanitize(reason));
        HealthFailed?.Invoke(reason);
    }

    public Task RestoreAsync()
    {
        if (Interlocked.Exchange(ref _restored, 1) != 0 || Snapshot is null)
        {
            return Task.CompletedTask;
        }
        _healthMonitor?.Dispose();
        _healthMonitor = null;
        _selfProtection?.Dispose();
        _selfProtection = null;
        IsActive = false;

        if (!_form.IsDisposed && _form.IsHandleCreated && _form.Handle == Snapshot.Handle)
        {
            _native.SetStyle(Snapshot.Handle, Snapshot.Style);
            _native.SetExtendedStyle(Snapshot.Handle, Snapshot.ExtendedStyle);
            _native.RefreshFrame(Snapshot.Handle);
            _native.SetTopMost(Snapshot.Handle, Snapshot.TopMost);
            foreach ((Control control, bool enabled) in Snapshot.ControlEnabledStates)
            {
                if (!control.IsDisposed) control.Enabled = enabled;
            }
            _form.Enabled = Snapshot.Enabled;
            _form.WindowState = FormWindowState.Normal;
            _form.Bounds = Snapshot.Bounds;
            _form.WindowState = Snapshot.WindowState;
            _form.Visible = Snapshot.Visible;
        }
        PlayerPresentationDiagnostics.Write(
            $"KEEP_VISIBLE_END mainHwnd=0x{Snapshot.Handle.ToInt64():X} selfCommandBlockedCount={SelfCommandBlockedCount} " +
            $"invariantRepairCount={InvariantRepairCount} overlayUsed=False restored=True");
        return Task.CompletedTask;
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child)) yield return nested;
        }
    }

    private static void DisableInteractiveControls(IEnumerable<Control> controls)
    {
        foreach (Control control in controls)
        {
            if (control is ButtonBase or ComboBox or ListBox or TextBoxBase or NumericUpDown or TrackBar or LinkLabel)
            {
                control.Enabled = false;
            }
        }
    }

    private static string Sanitize(string value) => value.Replace('\r', ' ').Replace('\n', ' ');
    public void Dispose() => RestoreAsync().GetAwaiter().GetResult();
}

public sealed class MinimizePresentation : IPlayerPresentationService
{
    private readonly Form _form;
    private readonly IPlayerWindowNativeApi _native;
    private int _restored;

    public MinimizePresentation(Form form, IPlayerWindowNativeApi native)
    {
        _form = form;
        _native = native;
    }

    public PlayerWindowStateSnapshot? Snapshot { get; private set; }
    public bool IsActive { get; private set; }
    public int SelfCommandBlockedCount => 0;
    public int InvariantRepairCount => 0;
    public bool OverlayUsed => true;
    public event Action<string>? HealthFailed { add { } remove { } }

    public Task PrepareAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        nint handle = _form.Handle;
        Snapshot = new PlayerWindowStateSnapshot(
            handle, _form.Visible, _form.WindowState, _form.Bounds, _form.TopMost, _form.Enabled,
            _native.GetStyle(handle), _native.GetExtendedStyle(handle), _native.GetOwner(handle),
            new ReadOnlyDictionary<Control, bool>(new Dictionary<Control, bool>()));
        _form.WindowState = FormWindowState.Minimized;
        IsActive = true;
        PlayerPresentationDiagnostics.Write(
            $"MINIMIZE_BEGIN requestedPresentation=Minimize appliedPresentation=Minimize mainHwnd=0x{handle.ToInt64():X} " +
            $"visibleAtStart={Snapshot.Visible} windowStateAtStart={Snapshot.WindowState} windowStateDuringPlayback={_form.WindowState} overlayUsed=True");
        return Task.CompletedTask;
    }

    public Task RestoreAsync()
    {
        if (Interlocked.Exchange(ref _restored, 1) != 0 || Snapshot is null)
        {
            return Task.CompletedTask;
        }
        IsActive = false;
        if (!_form.IsDisposed)
        {
            _form.WindowState = FormWindowState.Normal;
            _form.Bounds = Snapshot.Bounds;
            _form.TopMost = Snapshot.TopMost;
            _form.Enabled = Snapshot.Enabled;
            _form.WindowState = Snapshot.WindowState;
            _form.Visible = Snapshot.Visible;
        }
        PlayerPresentationDiagnostics.Write($"MINIMIZE_END mainHwnd=0x{Snapshot.Handle.ToInt64():X} restored=True");
        return Task.CompletedTask;
    }

    public void Dispose() => RestoreAsync().GetAwaiter().GetResult();
}

public static class PlayerPresentationDiagnostics
{
    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(PlayerRuntimePaths.Logs);
            RotatingLog.Write(Path.Combine(PlayerRuntimePaths.Logs, "playback_session.log"),
                $"{DateTimeOffset.Now:O} {message.Replace('\r', ' ').Replace('\n', ' ')}");
        }
        catch
        {
        }
    }
}

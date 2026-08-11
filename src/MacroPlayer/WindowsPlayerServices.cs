using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using MacroCore.Timing;

namespace MacroPlayer;

public sealed class WindowsForegroundWindowService : IForegroundWindowService, IDisposable
{
    private static readonly HashSet<string> RejectedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "dwm.exe", "MacroPlayer.exe", "MacroRecorder.exe", "MacroLauncher.exe", "MacroSafetyWatchdog.exe"
    };
    private readonly WinEventDelegate _winEventDelegate;
    private nint _foregroundHook;

    public WindowsForegroundWindowService() => _winEventDelegate = OnForegroundChanged;

    public event Action<ForegroundSnapshot>? NonToolForegroundChanged;

    public void StartForegroundTracking()
    {
        if (_foregroundHook == nint.Zero)
        {
            _foregroundHook = SetWinEventHook(3, 3, nint.Zero, _winEventDelegate, 0, 0, 0x0000 | 0x0002);
        }
    }

    public ForegroundSnapshot? CaptureWindow(nint window) => CreateSnapshot(window);

    public ForegroundSnapshot? CaptureCurrent() => CreateSnapshot(GetForegroundWindow());

    public nint GetForegroundWindowHandleFast() => GetForegroundWindow();

    public bool TryActivate(ForegroundSnapshot snapshot)
    {
        if (!IsDefaultDesktop() || snapshot.WindowHandle == nint.Zero ||
            !IsWindow(snapshot.WindowHandle) || !IsWindowVisible(snapshot.WindowHandle))
        {
            return false;
        }

        _ = GetWindowThreadProcessId(snapshot.WindowHandle, out uint processId);
        if (processId != snapshot.ProcessId || processId == Environment.ProcessId ||
            RejectedProcesses.Contains(snapshot.ProcessName))
        {
            return false;
        }

        return SetForegroundWindow(snapshot.WindowHandle);
    }

    public bool IsSecureDesktop(out string reason)
    {
        if (IsDefaultDesktop())
        {
            reason = string.Empty;
            return false;
        }

        reason = "目前不是可操作的 Default desktop；安全桌面一律禁止播放。";
        return true;
    }

    public void Dispose()
    {
        nint hook = Interlocked.Exchange(ref _foregroundHook, nint.Zero);
        if (hook != nint.Zero)
        {
            _ = UnhookWinEvent(hook);
        }
        GC.SuppressFinalize(this);
    }

    private void OnForegroundChanged(nint hook, uint eventType, nint window, int objectId, int childId, uint threadId, uint eventTime)
    {
        if (window == nint.Zero || objectId != 0 || childId != 0)
        {
            return;
        }

        ForegroundSnapshot? snapshot = CreateSnapshot(window);
        if (snapshot is not null)
        {
            NonToolForegroundChanged?.Invoke(snapshot);
        }
    }

    private static ForegroundSnapshot? CreateSnapshot(nint window)
    {
        if (window == nint.Zero || !IsWindow(window) || !IsWindowVisible(window))
        {
            return null;
        }

        _ = GetWindowThreadProcessId(window, out uint processId);
        if (processId == 0 || processId == Environment.ProcessId)
        {
            return null;
        }

        try
        {
            using Process process = Process.GetProcessById((int)processId);
            if (process.HasExited)
            {
                return null;
            }
            string processName = process.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? process.ProcessName
                : process.ProcessName + ".exe";
            if (RejectedProcesses.Contains(processName))
            {
                return null;
            }
            return new ForegroundSnapshot(window, (int)processId, processName,
                WindowsPlayerPrivilege.GetProcessIntegrityRid((int)processId));
        }
        catch
        {
            return null;
        }
    }

    private static bool IsDefaultDesktop()
    {
        const uint DesktopReadObjects = 0x0001;
        nint desktop = OpenInputDesktop(0, false, DesktopReadObjects);
        if (desktop == nint.Zero)
        {
            return false;
        }

        try
        {
            _ = GetUserObjectInformationW(desktop, 2, nint.Zero, 0, out uint required);
            if (required == 0)
            {
                return false;
            }

            nint buffer = Marshal.AllocHGlobal((int)required);
            try
            {
                if (!GetUserObjectInformationW(desktop, 2, buffer, required, out _))
                {
                    return false;
                }
                string name = Marshal.PtrToStringUni(buffer) ?? string.Empty;
                return name.Equals("Default", StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            _ = CloseDesktop(desktop);
        }
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool GetUserObjectInformationW(nint obj, int index, nint information, uint length, out uint needed);

    [DllImport("user32.dll")]
    private static extern bool CloseDesktop(nint desktop);

    private delegate void WinEventDelegate(nint hook, uint eventType, nint window, int objectId, int childId, uint threadId, uint eventTime);

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module, WinEventDelegate callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(nint hook);
}

public interface IPlayerWindowNativeApi
{
    nint GetExtendedStyle(nint window);
    void SetExtendedStyle(nint window, nint style);
    void RefreshFrame(nint window);
    nint GetStyle(nint window) => nint.Zero;
    void SetStyle(nint window, nint style) { }
    nint GetOwner(nint window) => nint.Zero;
    bool IsWindow(nint window) => window != nint.Zero;
    bool IsWindowVisible(nint window) => true;
    bool IsIconic(nint window) => false;
    Rectangle GetWindowRectangle(nint window) => Rectangle.Empty;
    PlayerNativeWindowPlacement GetWindowPlacement(nint window) => new(false, Rectangle.Empty);
    bool IsTopMost(nint window) => true;
    void SetTopMost(nint window, bool enabled) { }
    void SetLayeredOpaque(nint window) { }
}

public sealed class WindowsPlayerWindowNativeApi : IPlayerWindowNativeApi
{
    private const int StyleIndex = -16;
    private const int ExtendedStyleIndex = -20;
    private const int OwnerIndex = -8;
    private const long TopMostStyle = 0x00000008L;
    private const uint LayeredAlpha = 0x00000002;
    private const uint NoSize = 0x0001;
    private const uint NoMove = 0x0002;
    private const uint NoZOrder = 0x0004;
    private const uint NoActivate = 0x0010;
    private const uint FrameChanged = 0x0020;

    public nint GetExtendedStyle(nint window) => nint.Size == 8
        ? GetWindowLongPtr64(window, ExtendedStyleIndex)
        : new nint(GetWindowLong32(window, ExtendedStyleIndex));

    public nint GetStyle(nint window) => GetWindowLong(window, StyleIndex);
    public nint GetOwner(nint window) => GetWindowLong(window, OwnerIndex);

    public void SetExtendedStyle(nint window, nint style)
    {
        Marshal.SetLastPInvokeError(0);
        nint previous = nint.Size == 8
            ? SetWindowLongPtr64(window, ExtendedStyleIndex, style)
            : new nint(SetWindowLong32(window, ExtendedStyleIndex, style.ToInt32()));
        if (previous == nint.Zero && Marshal.GetLastPInvokeError() != 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "無法更新 Player 視窗延伸樣式。");
        }
    }

    public void SetStyle(nint window, nint style) => SetWindowLong(window, StyleIndex, style);

    public bool IsWindow(nint window) => NativeIsWindow(window);
    public bool IsWindowVisible(nint window) => NativeIsWindowVisible(window);
    public bool IsIconic(nint window) => NativeIsIconic(window);
    public bool IsTopMost(nint window) => (GetExtendedStyle(window).ToInt64() & TopMostStyle) != 0;

    public Rectangle GetWindowRectangle(nint window)
    {
        return GetWindowRect(window, out NativeRect rect)
            ? Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom)
            : Rectangle.Empty;
    }

    public PlayerNativeWindowPlacement GetWindowPlacement(nint window)
    {
        NativeWindowPlacement placement = new() { Length = Marshal.SizeOf<NativeWindowPlacement>() };
        return NativeGetWindowPlacement(window, ref placement)
            ? new PlayerNativeWindowPlacement(placement.ShowCommand == 2,
                Rectangle.FromLTRB(placement.NormalPosition.Left, placement.NormalPosition.Top,
                    placement.NormalPosition.Right, placement.NormalPosition.Bottom))
            : new PlayerNativeWindowPlacement(IsIconic(window), Rectangle.Empty);
    }

    public void SetTopMost(nint window, bool enabled)
    {
        nint insertAfter = enabled ? new nint(-1) : new nint(-2);
        if (!SetWindowPos(window, insertAfter, 0, 0, 0, 0, NoSize | NoMove | NoActivate | FrameChanged))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "無法更新 Player 暫時最上層狀態。");
        }
    }

    public void SetLayeredOpaque(nint window)
    {
        if (!SetLayeredWindowAttributes(window, 0, 255, LayeredAlpha))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "無法設定 Player 滑鼠穿透視窗層。");
        }
    }

    public void RefreshFrame(nint window)
    {
        if (!SetWindowPos(window, nint.Zero, 0, 0, 0, 0,
                NoSize | NoMove | NoZOrder | NoActivate | FrameChanged))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "無法重新套用 Player 視窗樣式。");
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static extern int GetWindowLong32(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern nint GetWindowLongPtr64(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong32(nint window, int index, int value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern nint SetWindowLongPtr64(nint window, int index, nint value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    private static nint GetWindowLong(nint window, int index) => nint.Size == 8
        ? GetWindowLongPtr64(window, index)
        : new nint(GetWindowLong32(window, index));

    private static void SetWindowLong(nint window, int index, nint value)
    {
        Marshal.SetLastPInvokeError(0);
        nint previous = nint.Size == 8
            ? SetWindowLongPtr64(window, index, value)
            : new nint(SetWindowLong32(window, index, value.ToInt32()));
        if (previous == nint.Zero && Marshal.GetLastPInvokeError() != 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "無法還原 Player 視窗樣式。");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeWindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCommand;
        public Point MinimumPosition;
        public Point MaximumPosition;
        public NativeRect NormalPosition;
    }

    [DllImport("user32.dll", EntryPoint = "IsWindow")]
    private static extern bool NativeIsWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
    private static extern bool NativeIsWindowVisible(nint window);
    [DllImport("user32.dll", EntryPoint = "IsIconic")]
    private static extern bool NativeIsIconic(nint window);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowPlacement", SetLastError = true)]
    private static extern bool NativeGetWindowPlacement(nint window, ref NativeWindowPlacement placement);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(nint window, uint colorKey, byte alpha, uint flags);
}

public static class PlayerWindowClickThroughPolicy
{
    public const int WindowNcHitTest = 0x0084;
    public const int HitTestTransparent = -1;
    public static bool ShouldReturnTransparent(int message, bool active) => active && message == WindowNcHitTest;
}

internal sealed class PlayerClickThroughNativeWindow : NativeWindow
{
    public bool Active { get; set; }

    protected override void WndProc(ref Message message)
    {
        if (PlayerWindowClickThroughPolicy.ShouldReturnTransparent(message.Msg, Active))
        {
            message.Result = new nint(PlayerWindowClickThroughPolicy.HitTestTransparent);
            return;
        }
        base.WndProc(ref message);
    }
}

public sealed class WinFormsPlayerWindowModeService : IPlayerWindowModeService, IDisposable
{
    private readonly Form _form;
    private readonly IPlayerWindowNativeApi _native;
    private IPlayerPresentationService? _active;
    private Action<string>? _failureHandler;

    public WinFormsPlayerWindowModeService(Form form, IPlayerWindowNativeApi? native = null)
    {
        _form = form;
        _native = native ?? new WindowsPlayerWindowNativeApi();
    }

    public PlayerCountdownMode? AppliedMode { get; private set; }
    public PlayerWindowStateSnapshot? Snapshot => _active?.Snapshot;
    public nint CapturedHandle => Snapshot?.Handle ?? nint.Zero;
    public nint OriginalExtendedStyle => Snapshot?.ExtendedStyle ?? nint.Zero;
    public nint CurrentExtendedStyle => _form.IsHandleCreated ? _native.GetExtendedStyle(_form.Handle) : nint.Zero;
    public Rectangle OriginalBoundsForTests => Snapshot?.Bounds ?? Rectangle.Empty;
    public bool ClickThroughActive => _active is KeepVisiblePresentation { ClickThroughActive: true };
    public int SelfCommandBlockedCount => _active?.SelfCommandBlockedCount ?? 0;
    public int InvariantRepairCount => _active?.InvariantRepairCount ?? 0;

    public async Task PrepareAsync(PlayerCountdownMode mode, PlaybackExecutionContext context, CancellationToken cancellationToken)
    {
        _ = context;
        if (_active is not null)
        {
            throw new InvalidOperationException("Player presentation 已在使用中。");
        }

        IPlayerPresentationService presentation = mode == PlayerCountdownMode.KeepVisible
            ? new KeepVisiblePresentation(_form, _native)
            : new MinimizePresentation(_form, _native);
        presentation.HealthFailed += OnHealthFailed;
        _active = presentation;
        AppliedMode = mode;
        try
        {
            await presentation.PrepareAsync(cancellationToken).ConfigureAwait(true);
        }
        catch
        {
            presentation.HealthFailed -= OnHealthFailed;
            presentation.Dispose();
            _active = null;
            AppliedMode = null;
            throw;
        }
    }

    public async Task RestoreAsync()
    {
        IPlayerPresentationService? presentation = _active;
        if (presentation is null)
        {
            AppliedMode = null;
            return;
        }
        try
        {
            await presentation.RestoreAsync().ConfigureAwait(true);
        }
        finally
        {
            presentation.HealthFailed -= OnHealthFailed;
            presentation.Dispose();
            _active = null;
            AppliedMode = null;
        }
    }

    public void SetFailureHandler(Action<string>? handler) => _failureHandler = handler;
    public void AllowInternalClose() => PlayerWindowSelfProtection.AllowNextInternalClose(_form);
    private void OnHealthFailed(string reason) => _failureHandler?.Invoke(reason);
    public void Dispose() => RestoreAsync().GetAwaiter().GetResult();
}
public sealed class SystemCountdownService : ICountdownService
{
    public async Task RunAsync(int seconds, Action<int> tick, CancellationToken cancellationToken)
    {
        if (seconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds));
        }
        for (int remaining = seconds; remaining >= 1; remaining--)
        {
            tick(remaining);
            await Task.Delay(1000, cancellationToken).ConfigureAwait(true);
        }
    }
}

public sealed class PlaybackSessionLog : IPlaybackSessionLog
{
    private const long MaximumBytes = 1024 * 1024;
    private const int RetainedFiles = 5;
    private readonly string _path = Path.Combine(PlayerRuntimePaths.Logs, "playback_session.log");
    private readonly object _gate = new();

    public void SessionStarted(PlaybackMacroDocument macro, PlaybackExecutionContext context, PlayerCountdownMode mode)
    {
        long eventTimeline = macro.Events.Count == 0 ? 0 : macro.Events[^1].OffsetMilliseconds;
        EffectivePlaybackPrivilegeResolution privilege = EffectivePlaybackPrivilegeResolver.Resolve(macro);
        Write($"SESSION_START file={Path.GetFileName(macro.FilePath)} schema={macro.SchemaVersion} events={macro.Events.Count} recordedSessionDurationMs={macro.DurationMilliseconds} eventTimelineDurationMs={eventTimeline} playerIntegrity={WindowsPlayerPrivilege.Label} privilegeRequired={privilege.Requirement} privilegeConsistency={privilege.Consistency} playbackScope=DesktopOnly playerElevated={context.PlayerElevated} countdownMode={mode}");
    }

    public void Phase(string phase) => Write(Sanitize(phase));
    public void FirstEventSent() => Write("FIRST_EVENT_SENT");

    public void Timing(PlaybackTimingMetrics metrics, PlaybackRuntimeCounters counters)
    {
        static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        Write(
            $"TIMING recordedSessionDurationMs={metrics.RecordedSessionDurationMilliseconds} " +
            $"eventTimelineDurationMs={metrics.EventTimelineDurationMilliseconds} " +
            $"timelinePositionMs={metrics.TimelinePositionMilliseconds} " +
            $"wallPlaybackDurationMs={F(metrics.WallPlaybackDurationMilliseconds)} " +
            $"speedRatio={F(metrics.SpeedRatio)} finalDriftMs={F(metrics.FinalDriftMilliseconds)} " +
            $"averageLatenessMs={F(metrics.AverageLatenessMilliseconds)} " +
            $"p95LatenessMs={F(metrics.P95LatenessMilliseconds)} maxLatenessMs={F(metrics.MaximumLatenessMilliseconds)} " +
            $"lateEventCount={metrics.LateEventCount} coalescedMouseMoves={metrics.CoalescedMouseMoves} " +
            $"focusFastCount={counters.FocusFastProbeCount} focusFullCount={counters.FocusFullResolutionCount} " +
            $"progressUpdateCount={counters.ProgressUpdateCount} sendInputCalls={counters.SendInputCallCount} " +
            $"nativeInputs={counters.NativeInputCount} safetyStops={counters.SafetyStopCount}");
    }

    public void SessionEnded(string disposition, int sentCount, int focusChangeCount, string? detail = null) =>
        Write($"SESSION_END disposition={Sanitize(disposition)} eventsSent={sentCount} focusChangeCount={focusChangeCount} detail={Sanitize(detail ?? string.Empty)}");

    private void Write(string message)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            RotateIfNeeded();
            File.AppendAllText(_path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
    }

    private void RotateIfNeeded()
    {
        if (!File.Exists(_path) || new FileInfo(_path).Length < MaximumBytes)
        {
            return;
        }
        string oldest = _path + "." + RetainedFiles;
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }
        for (int index = RetainedFiles - 1; index >= 1; index--)
        {
            string source = _path + "." + index;
            if (File.Exists(source))
            {
                File.Move(source, _path + "." + (index + 1), true);
            }
        }
        File.Move(_path, _path + ".1", true);
    }

    private static string Sanitize(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Replace('=', ':');
}

public sealed class CountdownOverlayService : IOverlayService, IDisposable
{
    private CountdownOverlayForm? _form;

    public void ShowCountdown(string macroName, int seconds) => Update($"{macroName}\r\n即將播放：{seconds}\r\n長按 F11 2 秒緊急停止");
    public void ShowPlaying(string macroName, PlaybackProgress progress) =>
        Update($"{macroName}\r\n播放中 {progress.EventsSent} / {progress.TotalEvents}\r\n長按 F11 2 秒緊急停止");

    public void Close()
    {
        if (_form is null)
        {
            return;
        }
        if (_form.InvokeRequired)
        {
            _form.BeginInvoke((MethodInvoker)Close);
            return;
        }
        _form.Close();
        _form.Dispose();
        _form = null;
    }

    public void Dispose() => Close();

    private void Update(string text)
    {
        if (_form is null || _form.IsDisposed)
        {
            _form = new CountdownOverlayForm();
            _form.Show();
        }
        if (_form.InvokeRequired)
        {
            _form.BeginInvoke((MethodInvoker)(() => _form.SetText(text)));
        }
        else
        {
            _form.SetText(text);
        }
    }
}

internal sealed class CountdownOverlayForm : Form
{
    private readonly Label _label;

    public CountdownOverlayForm()
    {
        Text = "巨集播放狀態";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(340, 122);
        BackColor = Color.FromArgb(255, 249, 230);
        _label = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Microsoft JhengHei UI", 11F, FontStyle.Bold),
            ForeColor = Color.FromArgb(70, 52, 16)
        };
        Controls.Add(_label);
        Rectangle working = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(working.Right - Width - 24, working.Top + 24);
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= PlaybackOverlayWindowPolicy.RequiredExtendedStyle;
            return parameters;
        }
    }

    public void SetText(string text) => _label.Text = text;
}

public static class PlaybackOverlayWindowPolicy
{
    public const int NoActivate = 0x08000000;
    public const int Transparent = 0x00000020;
    public const int ToolWindow = 0x00000080;
    public const int RequiredExtendedStyle = NoActivate | Transparent | ToolWindow;
}

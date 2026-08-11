using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using MacroCore.Input;
using MacroCore.Models;
using MacroCore.Runtime;
using MacroCore.Security;
using MacroCore.Serialization;
using MacroRecorder.Services;

namespace MacroRecorder;

public sealed class MainForm : Form
{
    public const string StandardModeText = "標準模式（Low-Level Hook）";
    public const string RawModeText = "Raw Input 增強模式（Low-Level Hook + Raw Input）";
    public const string KeepWindowText = "保持視窗在原位";
    public const string MinimizeWindowText = "錄製開始後最小化到工作列";

    private readonly RecorderPrivilegeDisplayModel _privilegeDisplay;
    private readonly IWindowsPrivilegeService _privilegeService;
    private readonly RecorderService _service;
    private readonly RecorderSettingsStore _settingsStore;
    private readonly bool _runtimeEnabled;
    private readonly ComboBox _inputMode = new() { Name = "InputMode", DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _windowBehavior = new() { Name = "WindowBehavior", DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _showMonitor = new() { Name = "ShowMonitor", Text = "顯示即時輸入監看", AutoSize = true };
    private readonly Button _restoreDefaults = new() { Name = "RestoreDefaults", Text = "恢復安全預設值", AutoSize = true };
    private readonly Button _manualButton = new() { Name = "ManualButton", Text = "開啟操作手冊", AutoSize = true };
    private readonly Button _clearMonitor = new() { Text = "清除監看", AutoSize = true };
    private readonly Label _modeLabel = new() { Name = "ModeBanner" };
    private readonly Label _stateLabel = new() { Name = "RecorderState" };
    private readonly Label _countdownLabel = new() { Name = "RecorderCountdown" };
    private readonly Label _healthLabel = new() { Name = "CaptureHealth" };
    private readonly Label _warningLabel = new() { Name = "SafetyWarning" };
    private readonly Label _heldInputsLabel = new();
    private readonly Label _monitorStatsLabel = new();
    private readonly ListBox _recentEvents = new();
    private readonly GroupBox _monitorGroup = new() { Name = "LiveMonitor", Text = "即時輸入監看（僅記憶體，不寫入 log）" };
    private readonly Panel _viewport = new() { Name = "ScrollableViewport", Dock = DockStyle.Fill, AutoScroll = true };
    private readonly TableLayoutPanel _rootLayout = new() { Name = "RootLayout" };
    private readonly TableLayoutPanel _settingsLayout = new() { Name = "RecorderSettingsLayout" };
    private readonly Panel _footer = new() { Name = "Footer" };
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 75 };
    private readonly System.Windows.Forms.Timer _privilegeObserver = new() { Interval = 250 };
    private readonly string _recordingsFolder;

    private RecorderSettings _settings;
    private RecorderUiState _lastState = RecorderUiState.Armed;
    private long _lastUiVersion = -1;
    private bool _loadingSettings;
    private bool _autoMinimized;
    private bool _closing;

    public MainForm()
        : this(RecorderPrivilegeDisplayModel.ForProbe(false))
    {
    }

    public MainForm(
        RecorderPrivilegeDisplayModel privilegeDisplay,
        bool runtimeEnabled = true,
        RecorderService? service = null,
        RecorderSettingsStore? settingsStore = null,
        IWindowsPrivilegeService? privilegeService = null)
    {
        _privilegeDisplay = privilegeDisplay ?? throw new ArgumentNullException(nameof(privilegeDisplay));
        _runtimeEnabled = runtimeEnabled;
        _privilegeService = privilegeService ?? new WindowsPrivilegeService();
        _service = service ?? new RecorderService();
        _settingsStore = settingsStore ?? new RecorderSettingsStore();
        _recordingsFolder = RecordingLibraryPaths.CanonicalRecordingsDirectory;
        _settings = _settingsStore.Load();

        Text = _privilegeDisplay.WindowTitle;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScroll = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = false;
        ShowInTaskbar = true;
        Font = new Font("Microsoft JhengHei UI", 10F);
        ApplySafeInitialBounds();
        BuildUi();
        ApplyLoadedSettings();
        WireEvents();
    }

    public IReadOnlyDictionary<string, Control> CoreControls => new Dictionary<string, Control>(StringComparer.Ordinal)
    {
        ["InputMode"] = _inputMode,
        ["WindowBehavior"] = _windowBehavior,
        ["ShowMonitor"] = _showMonitor,
        ["RestoreDefaults"] = _restoreDefaults,
        ["ManualButton"] = _manualButton,
        ["Footer"] = _footer
    };

    public bool RawChoiceAvailable => _inputMode.Items.Cast<object>().Any(item => string.Equals(item?.ToString(), RawModeText, StringComparison.Ordinal));

    public void SetMonitorVisibleForProbe(bool visible)
    {
        _showMonitor.Checked = visible;
        ApplyMonitorVisibility(visible);
    }

    private void ApplySafeInitialBounds()
    {
        var working = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        var width = Math.Min(980, Math.Max(720, working.Width - 48));
        var height = Math.Min(840, Math.Max(560, working.Height - 48));
        ClientSize = new Size(width, height);
        MinimumSize = new Size(Math.Min(720, working.Width), Math.Min(560, working.Height));
    }

    private void BuildUi()
    {
        _inputMode.Items.AddRange([StandardModeText, RawModeText]);
        _windowBehavior.Items.AddRange([KeepWindowText, MinimizeWindowText]);
        _inputMode.Dock = DockStyle.Fill;
        _windowBehavior.Dock = DockStyle.Fill;
        _inputMode.TabIndex = 0;
        _windowBehavior.TabIndex = 1;
        _showMonitor.TabIndex = 2;
        _restoreDefaults.TabIndex = 3;
        _manualButton.TabIndex = 4;
        _inputMode.AccessibleName = "輸入模式";
        _windowBehavior.AccessibleName = "開始錄製時視窗行為";
        _showMonitor.AccessibleName = "顯示即時輸入監看";
        _restoreDefaults.AccessibleName = "恢復安全預設值";
        _manualButton.AccessibleName = "開啟操作手冊";

        var privilegeHeader = BuildPrivilegeHeader();
        var settingsGroup = BuildSettingsGroup();
        ConfigureStatusLabel(_modeLabel, 14F);
        ConfigureStatusLabel(_stateLabel, 17F);
        ConfigureStatusLabel(_countdownLabel, 12F);
        _healthLabel.AutoSize = true;
        _healthLabel.Dock = DockStyle.Top;
        _healthLabel.BorderStyle = BorderStyle.FixedSingle;
        _healthLabel.Padding = new Padding(10);
        _warningLabel.AutoSize = true;
        _warningLabel.Dock = DockStyle.Top;
        _warningLabel.Padding = new Padding(8);
        _warningLabel.ForeColor = Color.DarkRed;
        BuildMonitorUi();
        BuildFooter();

        _rootLayout.Dock = DockStyle.Top;
        _rootLayout.AutoSize = true;
        _rootLayout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _rootLayout.Padding = new Padding(14);
        _rootLayout.ColumnCount = 1;
        _rootLayout.RowCount = 8;
        _rootLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var index = 0; index < 6; index++)
        {
            _rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }
        _rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 240));
        _rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _rootLayout.Controls.Add(privilegeHeader, 0, 0);
        _rootLayout.Controls.Add(settingsGroup, 0, 1);
        _rootLayout.Controls.Add(_modeLabel, 0, 2);
        var statePanel = BuildStatePanel();
        _rootLayout.Controls.Add(statePanel, 0, 3);
        _rootLayout.Controls.Add(_healthLabel, 0, 4);
        _rootLayout.Controls.Add(_warningLabel, 0, 5);
        _rootLayout.Controls.Add(_monitorGroup, 0, 6);
        _rootLayout.Controls.Add(_footer, 0, 7);
        _viewport.Controls.Add(_rootLayout);
        Controls.Add(_viewport);
        UpdateResponsiveLayout();
    }

    private Control BuildPrivilegeHeader()
    {
        var panel = new TableLayoutPanel
        {
            Name = "PrivilegeHeader",
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(12, 8, 12, 8),
            BackColor = _privilegeDisplay.Background,
            Margin = new Padding(0, 0, 0, 8)
        };
        var title = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            Font = new Font(Font.FontFamily, 12F, FontStyle.Bold),
            ForeColor = _privilegeDisplay.Foreground,
            Text = _privilegeDisplay.HeaderTitle + "  |  輸入模式請在下方明確選擇"
        };
        var description = new Label { AutoSize = true, Dock = DockStyle.Top, Text = _privilegeDisplay.Description };
        panel.Controls.Add(title, 0, 0);
        panel.Controls.Add(description, 0, 1);
        return panel;
    }

    private Control BuildSettingsGroup()
    {
        var group = new GroupBox
        {
            Name = "RecorderSettings",
            Text = "錄製設定",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(12),
            Margin = new Padding(0, 0, 0, 8)
        };
        _settingsLayout.Dock = DockStyle.Top;
        _settingsLayout.AutoSize = true;
        _settingsLayout.ColumnCount = 2;
        _settingsLayout.RowCount = 3;
        _settingsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        _settingsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _settingsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _settingsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _settingsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _settingsLayout.Controls.Add(NewCaption("輸入模式"), 0, 0);
        _settingsLayout.Controls.Add(_inputMode, 1, 0);
        _settingsLayout.Controls.Add(NewCaption("開始錄製時視窗"), 0, 1);
        _settingsLayout.Controls.Add(_windowBehavior, 1, 1);
        var options = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        options.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        options.Controls.Add(_showMonitor, 0, 0);
        options.Controls.Add(_restoreDefaults, 1, 0);
        _settingsLayout.Controls.Add(options, 1, 2);
        group.Controls.Add(_settingsLayout);
        return group;
    }

    private Control BuildStatePanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 2 };
        panel.Controls.Add(_stateLabel, 0, 0);
        panel.Controls.Add(_countdownLabel, 0, 1);
        return panel;
    }

    private void BuildMonitorUi()
    {
        _monitorGroup.Dock = DockStyle.Fill;
        _monitorGroup.Padding = new Padding(10);
        _heldInputsLabel.Dock = DockStyle.Fill;
        _heldInputsLabel.Font = new Font(Font.FontFamily, 10F, FontStyle.Bold);
        _heldInputsLabel.Text = "目前按住：無";
        _recentEvents.Dock = DockStyle.Fill;
        _recentEvents.HorizontalScrollbar = true;
        _recentEvents.IntegralHeight = false;
        _recentEvents.Font = new Font("Cascadia Mono", 9F);
        _monitorStatsLabel.Dock = DockStyle.Fill;
        _monitorStatsLabel.AutoEllipsis = true;
        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.Controls.Add(_clearMonitor, 0, 0);
        bottom.Controls.Add(new Label { AutoSize = true, Padding = new Padding(10, 7, 0, 0), Text = "最近 25 筆；顯示來源與處置，不保存按鍵內容。" }, 1, 0);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(_heldInputsLabel, 0, 0);
        layout.Controls.Add(_recentEvents, 0, 1);
        layout.Controls.Add(_monitorStatsLabel, 0, 2);
        layout.Controls.Add(bottom, 0, 3);
        _monitorGroup.Controls.Add(layout);
    }

    private void BuildFooter()
    {
        _footer.Dock = DockStyle.Top;
        _footer.AutoSize = true;
        _footer.Padding = new Padding(0, 8, 0, 0);
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.Controls.Add(_manualButton, 0, 0);
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 7, 0, 0),
            Text = "F12：長按 5 秒開始／停止。F11 僅供播放器緊急停止，不會寫入巨集。"
        }, 1, 0);
        _footer.Controls.Add(layout);
    }

    private void WireEvents()
    {
        _inputMode.SelectedIndexChanged += OnInputModeChanged;
        _windowBehavior.SelectedIndexChanged += OnWindowBehaviorChanged;
        _showMonitor.CheckedChanged += OnShowMonitorChanged;
        _restoreDefaults.Click += (_, _) => RestoreSafeDefaults();
        _clearMonitor.Click += (_, _) => _service.ClearInputMonitor();
        _manualButton.Click += (_, _) => OpenManual();
        _uiTimer.Tick += (_, _) => RefreshAuthoritativeState();
        _privilegeObserver.Tick += (_, _) => RecordingPrivilegeTracker.ObserveForeground(_privilegeService);
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        DpiChanged += (_, _) => BeginInvoke(UpdateResponsiveLayout);
        Shown += OnShown;
        FormClosing += OnFormClosing;
        if (_runtimeEnabled)
        {
            _service.StateSnapshotChanged += OnStateSnapshotChanged;
            _service.RecordingReady += OnRecordingReady;
            _service.Error += OnServiceError;
            _service.EmergencyShutdownRequested += OnEmergencyShutdownRequested;
            _service.ReplacementShutdownRequested += OnReplacementShutdownRequested;
        }
    }

    private async void OnShown(object? sender, EventArgs e)
    {
        UpdateResponsiveLayout();
        if (!_runtimeEnabled)
        {
            return;
        }
        _uiTimer.Start();
        _privilegeObserver.Start();
        RefreshAuthoritativeState();
        await _service.StartAsync();
        if (_closing)
        {
            return;
        }
        if (_settings.InputMode == RecorderInputModeSetting.RawEnhanced)
        {
            _service.SetRawEnhancedMode(true, explicitlyConfirmed: true);
        }
        RefreshAuthoritativeState();
        LaunchReadiness.SignalApplicationReady();
    }

    private void ApplyLoadedSettings()
    {
        _loadingSettings = true;
        try
        {
            _inputMode.SelectedIndex = _settings.InputMode == RecorderInputModeSetting.RawEnhanced ? 1 : 0;
            _windowBehavior.SelectedIndex = _settings.WindowBehavior == RecordingWindowBehavior.MinimizeToTaskbar ? 1 : 0;
            _showMonitor.Checked = _settings.ShowLiveMonitor;
            ApplyMonitorVisibility(_settings.ShowLiveMonitor);
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    private void OnInputModeChanged(object? sender, EventArgs e)
    {
        if (_loadingSettings || _closing || !_runtimeEnabled || _service.CurrentState != RecorderUiState.Armed)
        {
            return;
        }
        if (_inputMode.SelectedIndex == 1)
        {
            var result = MessageBox.Show(this,
                "Raw Input 增強模式會同時使用 Low-Level Hook 與 Raw Input，適合標準模式無法完整捕捉的特定程式。\r\n\r\n它不會自動切換、不使用 NOLEGACY/CAPTUREMOUSE，且仍受 bounded queue 與 circuit breaker 保護。是否啟用？",
                "啟用 Raw Input 增強模式", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (result != DialogResult.Yes || !_service.SetRawEnhancedMode(true, explicitlyConfirmed: true))
            {
                SetModeSelectionWithoutEvent(0);
                _service.SetRawEnhancedMode(false, explicitlyConfirmed: false);
                return;
            }
            _settings.InputMode = RecorderInputModeSetting.RawEnhanced;
        }
        else
        {
            _service.SetRawEnhancedMode(false, explicitlyConfirmed: false);
            _settings.InputMode = RecorderInputModeSetting.Standard;
        }
        SaveSettings();
        RefreshAuthoritativeState();
    }

    private void OnWindowBehaviorChanged(object? sender, EventArgs e)
    {
        if (_loadingSettings || _closing)
        {
            return;
        }
        _settings.WindowBehavior = _windowBehavior.SelectedIndex == 1 ? RecordingWindowBehavior.MinimizeToTaskbar : RecordingWindowBehavior.KeepWindow;
        SaveSettings();
    }

    private void OnShowMonitorChanged(object? sender, EventArgs e)
    {
        if (_loadingSettings || _closing)
        {
            return;
        }
        ApplyMonitorVisibility(_showMonitor.Checked);
        _settings.ShowLiveMonitor = _showMonitor.Checked;
        SaveSettings();
    }

    private void ApplyMonitorVisibility(bool visible)
    {
        _monitorGroup.Visible = visible;
        _rootLayout.RowStyles[6].SizeType = SizeType.Absolute;
        _rootLayout.RowStyles[6].Height = visible ? CalculateMonitorHeight() : 0;
        _monitorGroup.Margin = visible ? new Padding(0, 6, 0, 6) : Padding.Empty;
        _rootLayout.PerformLayout();
    }

    private int CalculateMonitorHeight()
    {
        var fixedHeight = 510;
        return Math.Max(210, _viewport.ClientSize.Height - fixedHeight);
    }

    private void UpdateResponsiveLayout()
    {
        if (_viewport.ClientSize.Width <= 0)
        {
            return;
        }
        _rootLayout.Width = Math.Max(640, _viewport.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4);
        _healthLabel.MaximumSize = new Size(Math.Max(580, _rootLayout.Width - 28), 0);
        _warningLabel.MaximumSize = new Size(Math.Max(580, _rootLayout.Width - 28), 0);
        if (_showMonitor.Checked)
        {
            _rootLayout.RowStyles[6].Height = CalculateMonitorHeight();
        }
        _rootLayout.PerformLayout();
    }

    private void RestoreSafeDefaults()
    {
        if (_runtimeEnabled && _service.CurrentState != RecorderUiState.Armed)
        {
            return;
        }
        if (_runtimeEnabled)
        {
            _service.SetRawEnhancedMode(false, explicitlyConfirmed: false);
        }
        _settings = new RecorderSettings();
        ApplyLoadedSettings();
        SaveSettings();
        if (_runtimeEnabled)
        {
            RefreshAuthoritativeState();
        }
    }

    private void SaveSettings()
    {
        if (!_runtimeEnabled)
        {
            return;
        }
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception exception)
        {
            _warningLabel.Text = "無法保存設定，程式仍以目前安全狀態運作：" + exception.Message;
        }
    }

    private void SetModeSelectionWithoutEvent(int index)
    {
        _loadingSettings = true;
        try { _inputMode.SelectedIndex = index; }
        finally { _loadingSettings = false; }
    }

    private void OnStateSnapshotChanged(RecorderStateSnapshot snapshot)
    {
        if (!IsHandleCreated || _closing) return;
        BeginInvoke(() => ApplySnapshot(snapshot));
    }

    private void ApplySnapshot(RecorderStateSnapshot snapshot)
    {
        if (snapshot.Version < _lastUiVersion) return;
        _lastUiVersion = snapshot.Version;
        _stateLabel.Text = snapshot.State switch
        {
            RecorderUiState.Armed => "ARMED - 長按 F12 5 秒開始",
            RecorderUiState.StartHolding => "START_HOLDING",
            RecorderUiState.Recording => "RECORDING - 放開 F12 後可再次長按停止",
            RecorderUiState.StopHolding => "STOP_HOLDING",
            RecorderUiState.Finalizing => "FINALIZING - 已停止接收新事件",
            RecorderUiState.Saving => "SAVING - 請選擇儲存位置",
            RecorderUiState.ErrorSafe => "ERROR SAFE - 輸入擷取已解除",
            _ => "DISPOSED"
        };
        _countdownLabel.Text = snapshot.State is RecorderUiState.StartHolding or RecorderUiState.StopHolding
            ? $"剩餘 {snapshot.HoldRemainingMs / 1000.0:0.0} 秒" : string.Empty;
        var armed = snapshot.State == RecorderUiState.Armed;
        _inputMode.Enabled = armed;
        _windowBehavior.Enabled = armed;
        _showMonitor.Enabled = armed;
        _restoreDefaults.Enabled = armed;
        if (_lastState != RecorderUiState.Recording && snapshot.State == RecorderUiState.Recording &&
            _settings.WindowBehavior == RecordingWindowBehavior.MinimizeToTaskbar)
        {
            WindowState = FormWindowState.Minimized;
            _autoMinimized = true;
        }
        else if (_autoMinimized && snapshot.State != RecorderUiState.Recording)
        {
            RestoreRecorderWindow();
        }
        _lastState = snapshot.State;
    }

    private void RefreshAuthoritativeState()
    {
        ApplySnapshot(_service.CurrentStateSnapshot);
        var health = _service.CaptureHealth;
        var queue = _service.QueueStats;
        var target = _service.TargetDiagnostic;
        var raw = _service.CaptureMode == RecorderCaptureMode.RawEnhanced;
        _modeLabel.Text = raw ? "RAW INPUT 增強模式（LL + Raw）" : "標準模式（Low-Level Hook）";
        _modeLabel.BackColor = raw ? Color.FromArgb(255, 194, 102) : Color.DarkSeaGreen;
        _modeLabel.ForeColor = Color.Black;
        _healthLabel.Text =
            $"Watchdog: {_service.WatchdogStatus}\r\n" +
            $"LL Hook: keyboard={Status(health.LowLevelKeyboardRegistered)}, mouse={Status(health.LowLevelMouseRegistered)}\r\n" +
            $"Raw Input: keyboard={Status(health.RawKeyboardRegistered)}, mouse={Status(health.RawMouseRegistered)}\r\n" +
            $"Queue: {queue.QueueDepth}/{queue.Capacity} ({queue.UsagePercent}%), rate={queue.EventsPerSecond}/s, dropped-move={queue.DroppedMoveEvents}\r\n" +
            $"Target: {Safe(target.ProcessName)}, integrity Recorder={target.RecorderIntegrity}, Target={target.ProcessIntegrity}  |  Elapsed={_service.RecordingElapsedMs / 1000.0:0.0}s";
        _warningLabel.Text = _service.CurrentState == RecorderUiState.ErrorSafe
            ? "安全保護已觸發。輸入擷取已解除，不會保存本次不完整錄製；請提供 Program\\State\\Logs。"
            : raw
                ? "Raw Input 增強模式已明確啟用，不會自動切換。只在標準模式捕捉不足時使用；F11/F12 不寫入巨集。"
                : "安全預設：只使用 Low-Level Hook。ESC、修飾鍵、滑鼠按鍵、滾輪與拖曳均可記錄；請勿錄製敏感資料。";
        RefreshMonitor();
    }

    private void RefreshMonitor()
    {
        if (!_showMonitor.Checked) return;
        var snapshot = (object)_service.MonitorSnapshot;
        var held = ReadEnumerableProperty(snapshot, "HeldInputs", "HeldKeys", "HeldNames").Select(FormatMonitorValue).ToArray();
        _heldInputsLabel.Text = held.Length == 0 ? "目前按住：無" : "目前按住：" + string.Join(" + ", held);
        var recent = ReadEnumerableProperty(snapshot, "RecentEvents", "Recent", "Events").Select(FormatMonitorEntry).TakeLast(25).ToArray();
        _recentEvents.BeginUpdate();
        try
        {
            _recentEvents.Items.Clear();
            _recentEvents.Items.AddRange(recent.Cast<object>().ToArray());
            if (_recentEvents.Items.Count > 0) _recentEvents.TopIndex = _recentEvents.Items.Count - 1;
        }
        finally { _recentEvents.EndUpdate(); }
        _monitorStatsLabel.Text = BuildMonitorStats(snapshot);
    }

    private static IEnumerable<object> ReadEnumerableProperty(object source, params string[] names)
    {
        foreach (var name in names)
        {
            if (source.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(source) is not IEnumerable values) continue;
            foreach (var value in values) if (value is not null) yield return value;
            yield break;
        }
    }

    private static string FormatMonitorEntry(object entry)
    {
        var time = ReadProperty(entry, "Timestamp", "Time", "ObservedAt");
        var input = ReadProperty(entry, "InputName", "Name", "Input", "DisplayName");
        var action = ReadProperty(entry, "Action");
        var source = ReadProperty(entry, "Source");
        var disposition = ReadProperty(entry, "Disposition", "Outcome");
        var timeText = time is DateTimeOffset dto ? dto.ToLocalTime().ToString("HH:mm:ss.fff") : "--:--:--.---";
        return $"{timeText}  {input ?? entry} {action}  [{Convert.ToString(source ?? "Unknown")?.ToUpperInvariant()}]  {Convert.ToString(disposition ?? "Observed")?.ToUpperInvariant()}";
    }

    private static string BuildMonitorStats(object snapshot)
    {
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["LowLevelKeyboardObserved"] = "LL Keyboard", ["RawKeyboardObserved"] = "Raw Keyboard",
            ["KeyboardOutput"] = "Keyboard output", ["KeyboardDuplicate"] = "Keyboard duplicate",
            ["LowLevelMouseObserved"] = "LL Mouse", ["RawMouseObserved"] = "Raw Mouse",
            ["MouseOutput"] = "Mouse output", ["DroppedMove"] = "Dropped move",
            ["QueueUsage"] = "Queue %", ["EventsPerSecond"] = "Events/s"
        };
        var values = new List<string>();
        foreach (var property in snapshot.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            if (labels.TryGetValue(property.Name, out var label)) values.Add($"{label}={property.GetValue(snapshot) ?? 0}");
        return values.Count == 0 ? "監看統計等待輸入" : string.Join("  |  ", values);
    }

    private static object? ReadProperty(object source, params string[] names)
    {
        foreach (var name in names)
        {
            var value = source.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(source);
            if (value is not null) return value;
        }
        return null;
    }

    private static string FormatMonitorValue(object value) => Convert.ToString(value) ?? "Unknown";

    private void OnRecordingReady(MacroFile macro)
    {
        if (!IsHandleCreated || _closing) return;
        BeginInvoke(() =>
        {
            RestoreRecorderWindow();
            using var dialog = new SaveFileDialog
            {
                Title = "儲存巨集", Filter = "Macro 檔案 (*.macro)|*.macro", FileName = macro.MacroName + ".macro", InitialDirectory = _recordingsFolder
            };
            try
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    Directory.CreateDirectory(_recordingsFolder);
                    var path = Path.GetExtension(dialog.FileName).Equals(".macro", StringComparison.OrdinalIgnoreCase) ? dialog.FileName : dialog.FileName + ".macro";
                    MacroSerializer.SaveAtomically(macro, path);
                }
            }
            catch (Exception exception) { MessageBox.Show(exception.Message, "儲存失敗", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally { _service.SaveCompleted(); }
        });
    }

    private void OnServiceError(string message)
    {
        if (!IsHandleCreated || _closing) return;
        BeginInvoke(() => { RestoreRecorderWindow(); MessageBox.Show(message, "MacroRecorder 安全訊息", MessageBoxButtons.OK, MessageBoxIcon.Warning); });
    }

    private void OnEmergencyShutdownRequested()
    {
        if (!IsHandleCreated || _closing) return;
        BeginInvoke(() => Close());
    }

    private void OnReplacementShutdownRequested()
    {
        if (!IsHandleCreated || _closing) return;
        BeginInvoke(() =>
        {
            bool discarded = _service.PrepareReplacementShutdown();
            RecorderDiagnosticsLog.HookHealth(
                $"replacement=ui_close discarded_active_recording={discarded} save_dialog=false");
            _closing = true;
            Close();
        });
    }

    private void RestoreRecorderWindow()
    {
        if (!_autoMinimized) return;
        WindowState = FormWindowState.Normal;
        ShowInTaskbar = true;
        _autoMinimized = false;
        Activate();
    }

    private void OpenManual()
    {
        try { Process.Start(new ProcessStartInfo(RuntimeFolders.Manual) { UseShellExecute = true }); }
        catch (Exception exception) { MessageBox.Show(this, $"無法開啟操作手冊：{exception.Message}", "操作手冊", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        _closing = true;
        _uiTimer.Stop();
        _privilegeObserver.Stop();
        if (_runtimeEnabled)
        {
            _service.EmergencyShutdownRequested -= OnEmergencyShutdownRequested;
            _service.ReplacementShutdownRequested -= OnReplacementShutdownRequested;
            _service.Dispose();
        }
    }

    private void ConfigureStatusLabel(Label label, float size)
    {
        label.AutoSize = true;
        label.Dock = DockStyle.Top;
        label.TextAlign = ContentAlignment.MiddleCenter;
        label.Font = new Font(Font.FontFamily, size, FontStyle.Bold);
        label.Padding = new Padding(6);
    }

    private static Label NewCaption(string text) => new()
    {
        Text = text + "：", AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(4, 7, 0, 0), Font = new Font("Microsoft JhengHei UI", 9F, FontStyle.Bold)
    };

    private static string Status(bool value) => value ? "OK" : "OFF";
    private static string Safe(string value) => string.IsNullOrWhiteSpace(value) ? "--" : value;
}

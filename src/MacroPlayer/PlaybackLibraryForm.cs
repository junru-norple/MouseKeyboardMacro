using System.Diagnostics;

namespace MacroPlayer;

public sealed partial class PlaybackLibraryForm : Form
{
    private const string FreeDesktopWarningText =
        "Player 不鎖定指定視窗。鍵盤會送到事件發生當下的前景視窗；滑鼠會作用於錄製座標目前最上層的內容。巨集可以開啟、關閉或切換視窗。";

    private readonly PlayerLaunchOptions _options;
    private readonly WindowsForegroundWindowService _foreground = new();
    private readonly ListBox _recordingsList = new();
    private readonly TextBox _details = new();
    private readonly Label _desktopScopeTitle = new();
    private readonly Label _desktopScopeHelp = new();
    private readonly Label _freeDesktopWarning = new();
    private readonly ComboBox _countdownMode = new();
    private readonly Button _startButton = new();
    private readonly Button _stopButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _openFolderButton = new();
    private readonly Button _chooseButton = new();
    private readonly Button _elevateButton = new();
    private readonly Button _closeButton = new();
    private readonly Label _status = new();
    private readonly Label _mode = new();
    private readonly System.Windows.Forms.Timer _foregroundTimer = new() { Interval = 350 };
    private PlaybackStartController? _controller;
    private CountdownOverlayService? _overlay;
    private WinFormsPlayerWindowModeService? _windowModeService;
    private PlaybackMacroDocument? _selectedMacro;
    private ForegroundSnapshot? _lastNonToolForeground;
    private CancellationTokenSource? _playbackCancellation;
    private Stopwatch? _f11Held;
    private readonly bool _runtimeEnabled;
    private readonly PlayerSafetySession? _safetySession;
    private bool _emergencyExitRequested;
    private bool _replacementExitRequested;
    private bool _internalCloseRequested;
    private string? _presentationFailureMessage;

    public PlaybackLibraryForm(PlayerLaunchOptions options, bool runtimeEnabled = true, PlayerSafetySession? safetySession = null)
    {
        _options = options;
        _runtimeEnabled = runtimeEnabled;
        _safetySession = safetySession;
        _startupSettings = options.ApplyOverrides(PlayerSettingsStore.Load());
        InitializeWindow();
        BuildLayout();
        InitializeMouseReplayModeUi();
        WireEvents();

        if (runtimeEnabled)
        {
            InitializeRuntime();
            InitializeForegroundTracking();
            RefreshRecordings();
            if (!string.IsNullOrWhiteSpace(options.InitialMacroPath))
            {
                SelectExternalMacro(options.InitialMacroPath);
            }

            _foregroundTimer.Start();
        }
    }

    public IReadOnlyDictionary<string, Control> CoreControls => new Dictionary<string, Control>(StringComparer.Ordinal)
    {
        ["ModeHeader"] = _mode,
        ["RecordingsList"] = _recordingsList,
        ["DetailsText"] = _details,
        ["DesktopScopeTitle"] = _desktopScopeTitle,
        ["DesktopScopeHelp"] = _desktopScopeHelp,
        ["FreeDesktopWarning"] = _freeDesktopWarning,
        ["CountdownMode"] = _countdownMode,
        ["StartButton"] = _startButton,
        ["StopButton"] = _stopButton,
        ["Status"] = _status,
        ["CloseButton"] = _closeButton
    };

    private void InitializeWindow()
    {
        Text = "巨集重播";
        Name = "PlaybackLibraryForm";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScroll = true;
        ClientSize = new Size(1020, 900);
        MinimumSize = new Size(800, 650);
        Font = new Font("Microsoft JhengHei UI", 10F);
        BackColor = Color.FromArgb(246, 248, 244);
        AllowDrop = true;
        TopMost = false;
    }

    private void BuildLayout()
    {
        TableLayoutPanel root = new()
        {
            Name = "PlayerRootLayout",
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(18),
            ColumnCount = 1,
            RowCount = 9,
            BackColor = BackColor
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 156));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 156));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 8));

        _mode.Name = "ModeHeader";
        _mode.Text = WindowsPlayerPrivilege.IsElevated
            ? "管理員重播模式\r\n播放器 Integrity：High"
            : "一般重播模式\r\n播放器 Integrity：Medium";
        _mode.Dock = DockStyle.Fill;
        _mode.Padding = new Padding(14, 8, 8, 8);
        _mode.Font = new Font(Font.FontFamily, 12F, FontStyle.Bold);
        _mode.ForeColor = WindowsPlayerPrivilege.IsElevated ? Color.FromArgb(137, 69, 8) : Color.FromArgb(24, 74, 50);
        _mode.BackColor = WindowsPlayerPrivilege.IsElevated ? Color.FromArgb(255, 235, 204) : Color.FromArgb(220, 239, 228);

        GroupBox libraryGroup = new() { Text = "已錄製巨集（新到舊）", Dock = DockStyle.Fill, Padding = new Padding(10) };
        _recordingsList.Name = "RecordingsList";
        _recordingsList.Dock = DockStyle.Fill;
        _recordingsList.IntegralHeight = false;
        _recordingsList.HorizontalScrollbar = true;
        _recordingsList.AccessibleName = "已錄製巨集清單";
        _recordingsList.TabIndex = 0;
        libraryGroup.Controls.Add(_recordingsList);

        GroupBox detailGroup = new() { Text = "巨集詳細資料", Dock = DockStyle.Fill, Padding = new Padding(10), MinimumSize = new Size(0, 120) };
        _details.Name = "DetailsText";
        _details.Dock = DockStyle.Fill;
        _details.Multiline = true;
        _details.ReadOnly = true;
        _details.ScrollBars = ScrollBars.Vertical;
        _details.BackColor = Color.White;
        _details.WordWrap = true;
        _details.AccessibleName = "巨集詳細資料";
        _details.TabStop = false;
        detailGroup.Controls.Add(_details);

        GroupBox rangeGroup = new() { Text = "播放範圍", Dock = DockStyle.Fill, Padding = new Padding(10, 6, 10, 8), MinimumSize = new Size(0, 96) };
        TableLayoutPanel rangeLayout = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        rangeLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rangeLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _desktopScopeTitle.Name = "DesktopScopeTitle";
        _desktopScopeTitle.Text = "直接重播於目前桌面";
        _desktopScopeTitle.AutoSize = true;
        _desktopScopeTitle.Dock = DockStyle.Fill;
        _desktopScopeTitle.Font = new Font(Font.FontFamily, 11F, FontStyle.Bold);
        _desktopScopeTitle.ForeColor = Color.FromArgb(24, 74, 50);
        _desktopScopeHelp.Name = "DesktopScopeHelp";
        _desktopScopeHelp.Text = "不鎖定指定視窗；播放期間可以切換視窗或開啟程式。";
        _desktopScopeHelp.AutoSize = true;
        _desktopScopeHelp.Dock = DockStyle.Fill;
        _desktopScopeHelp.ForeColor = Color.FromArgb(65, 65, 65);
        rangeLayout.Controls.Add(_desktopScopeTitle, 0, 0);
        rangeLayout.Controls.Add(_desktopScopeHelp, 0, 1);
        rangeGroup.Controls.Add(rangeLayout);

        GroupBox behaviorGroup = new() { Text = "倒數期間播放器視窗", Dock = DockStyle.Fill, Padding = new Padding(10, 6, 10, 8) };
        TableLayoutPanel behaviorLayout = TwoColumnLayout(300);
        _countdownMode.Name = "CountdownMode";
        _countdownMode.Dock = DockStyle.Fill;
        _countdownMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _countdownMode.AccessibleName = "播放器倒數顯示模式";
        _countdownMode.TabIndex = 5;
        _countdownMode.Items.Add(new ModeChoice(PlayerCountdownMode.MinimizeBeforeCountdown, "倒數前最小化（預設）"));
        _countdownMode.Items.Add(new ModeChoice(PlayerCountdownMode.KeepVisible, "保持可見但不取得焦點"));
        Label safety = new()
        {
            Text = "固定倒數 5 秒；每次只播放一次；長按 F11 2 秒緊急停止。",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(75, 75, 75)
        };
        behaviorLayout.Controls.Add(_countdownMode, 0, 0);
        behaviorLayout.Controls.Add(safety, 1, 0);
        behaviorGroup.Controls.Add(behaviorLayout);

        _freeDesktopWarning.Name = "FreeDesktopWarning";
        _freeDesktopWarning.Text = FreeDesktopWarningText;
        _freeDesktopWarning.Dock = DockStyle.Fill;
        _freeDesktopWarning.Padding = new Padding(12, 8, 12, 8);
        _freeDesktopWarning.BorderStyle = BorderStyle.FixedSingle;
        _freeDesktopWarning.TextAlign = ContentAlignment.MiddleLeft;
        _freeDesktopWarning.BackColor = Color.FromArgb(255, 244, 196);
        _freeDesktopWarning.ForeColor = Color.FromArgb(105, 70, 0);

        _status.Name = "Status";
        _status.Text = "請選擇巨集。";
        _status.Dock = DockStyle.Fill;
        _status.Padding = new Padding(12, 8, 12, 8);
        _status.BorderStyle = BorderStyle.FixedSingle;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.BackColor = Color.White;

        FlowLayoutPanel buttons = new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(0, 4, 0, 0)
        };
        ConfigureButton(_startButton, "StartButton", "開始播放", 112);
        ConfigureButton(_stopButton, "StopButton", "取消／停止", 108);
        ConfigureButton(_refreshButton, "RefreshButton", "重新整理", 96);
        ConfigureButton(_openFolderButton, "OpenFolderButton", "開啟資料夾", 108);
        ConfigureButton(_chooseButton, "ChooseButton", "選擇其他 .macro", 142);
        ConfigureButton(_elevateButton, "ElevateButton", "以管理員模式重新開啟", 190);
        ConfigureButton(_closeButton, "CloseButton", "關閉", 76);
        buttons.Controls.AddRange(new Control[]
        {
            _startButton, _stopButton, _refreshButton, _openFolderButton, _chooseButton, _elevateButton, _closeButton
        });

        root.Controls.Add(_mode, 0, 0);
        root.Controls.Add(libraryGroup, 0, 1);
        root.Controls.Add(detailGroup, 0, 2);
        root.Controls.Add(rangeGroup, 0, 3);
        root.Controls.Add(behaviorGroup, 0, 4);
        root.Controls.Add(_freeDesktopWarning, 0, 5);
        root.Controls.Add(_status, 0, 6);
        root.Controls.Add(buttons, 0, 7);
        Controls.Add(root);

        PlayerSettings settings = _startupSettings;
        _countdownMode.SelectedIndex = settings.CountdownMode == PlayerCountdownMode.KeepVisible ? 1 : 0;
        _startButton.Enabled = false;
        _stopButton.Enabled = false;
        _elevateButton.Visible = false;
        AcceptButton = _startButton;
        CancelButton = _closeButton;
    }

    private void InitializeRuntime()
    {
        _overlay = new CountdownOverlayService();
        PlaybackSessionLog sessionLog = new();
        _windowModeService = new WinFormsPlayerWindowModeService(this);
        _windowModeService.SetFailureHandler(reason => Ui(() =>
        {
            _presentationFailureMessage = reason;
            StopPlayback();
        }));
        _controller = new PlaybackStartController(
            _foreground,
            _windowModeService,
            new SystemCountdownService(),
            new SafePlaybackServiceFactory(_foreground),
            sessionLog,
            _overlay,
            () => _lastNonToolForeground);
        _controller.StateChanged += (_, state) => Ui(() =>
        {
            _safetySession?.SetActivity(state switch
            {
                PlaybackWorkflowState.Countdown => "Countdown",
                PlaybackWorkflowState.Playing => "Playing",
                PlaybackWorkflowState.Validating => "Armed",
                _ => "Idle"
            });
            UpdateState(state);
        });
        _controller.CountdownChanged += (_, remaining) => Ui(() => SetStatus(
            $"{_selectedMacro?.Name}\r\n即將播放：{remaining}\r\n長按 F11 2 秒緊急停止",
            Color.FromArgb(255, 249, 230), Color.FromArgb(90, 55, 0)));
        _controller.ProgressChanged += (_, progress) => Ui(() => SetStatus(
            $"播放中：{progress.EventsSent} / {progress.TotalEvents}，事件時間 {progress.TimelinePosition:mm\\:ss}，實際經過 {progress.Elapsed:mm\\:ss}，漂移 {progress.Drift.TotalMilliseconds:0} ms。長按 F11 2 秒緊急停止。",
            Color.FromArgb(229, 240, 255), Color.FromArgb(20, 62, 110)));
    }

    private void InitializeForegroundTracking()
    {
        if (_options.LaunchForegroundWindow != nint.Zero)
        {
            _lastNonToolForeground = _foreground.CaptureWindow(_options.LaunchForegroundWindow);
        }
        _foreground.NonToolForegroundChanged += OnNonToolForegroundChanged;
        _foreground.StartForegroundTracking();
        if (_safetySession is not null)
        {
            _safetySession.EmergencyRequested += OnEmergencyRequested;
            _safetySession.ReplacementShutdownRequested += OnReplacementShutdownRequested;
        }
    }

    private void OnNonToolForegroundChanged(ForegroundSnapshot snapshot) =>
        _lastNonToolForeground = snapshot;

    private void OnEmergencyRequested()
    {
        Ui(() =>
        {
            _emergencyExitRequested = true;
            _internalCloseRequested = true;
            StopPlayback();
            if (_controller?.IsRunning != true)
            {
                _windowModeService?.AllowInternalClose();
                Close();
            }
        });
    }

    private void OnReplacementShutdownRequested()
    {
        Ui(() =>
        {
            _replacementExitRequested = true;
            _internalCloseRequested = true;
            StopPlayback();
            if (_controller?.IsRunning != true)
            {
                _windowModeService?.AllowInternalClose();
                Close();
            }
        });
    }

    private void WireEvents()
    {
        _recordingsList.SelectedIndexChanged += (_, _) => LoadSelectedListItem();
        _countdownMode.SelectedIndexChanged += (_, _) => SaveSettings();
        _startButton.Click += async (_, _) => await StartPlaybackAsync();
        _stopButton.Click += (_, _) => StopPlayback();
        _refreshButton.Click += (_, _) => RefreshRecordings();
        _openFolderButton.Click += (_, _) => OpenRecordingsFolder();
        _chooseButton.Click += (_, _) => ChooseExternalMacro();
        _elevateButton.Click += (_, _) => RelaunchElevated();
        _closeButton.Click += (_, _) => Close();
        _foregroundTimer.Tick += (_, _) =>
        {
            CaptureLastForeground();
            MonitorEmergencyStop();
        };
        DragEnter += (_, args) =>
        {
            if (args.Data?.GetDataPresent(DataFormats.FileDrop) == true &&
                args.Data.GetData(DataFormats.FileDrop) is string[] paths &&
                paths.Length == 1 && paths[0].EndsWith(".macro", StringComparison.OrdinalIgnoreCase))
            {
                args.Effect = DragDropEffects.Copy;
            }
        };
        DragDrop += (_, args) =>
        {
            if (args.Data?.GetData(DataFormats.FileDrop) is string[] paths && paths.Length == 1)
            {
                SelectExternalMacro(paths[0]);
            }
        };
        FormClosing += (_, args) =>
        {
            if (_internalCloseRequested)
            {
                return;
            }
            if (_controller?.IsRunning == true)
            {
                args.Cancel = true;
                StopPlayback();
                SetStatus("播放正在安全停止，請稍候再關閉。", Color.FromArgb(255, 244, 214), Color.DarkOrange);
            }
        };
        FormClosed += (_, _) =>
        {
            _foregroundTimer.Stop();
            _playbackCancellation?.Cancel();
            _overlay?.Dispose();
            _foreground.NonToolForegroundChanged -= OnNonToolForegroundChanged;
            _foreground.Dispose();
            if (_safetySession is not null)
            {
                _safetySession.EmergencyRequested -= OnEmergencyRequested;
                _safetySession.ReplacementShutdownRequested -= OnReplacementShutdownRequested;
            }
            _windowModeService?.Dispose();
        };
    }

    private void SaveSettings()
    {
        if (_initializingUi || !_runtimeEnabled || _countdownMode.SelectedItem is not ModeChoice choice)
        {
            return;
        }

        PlayerSettingsStore.Update(current => current with
        {
            CountdownMode = choice.Mode
        });
    }

    private async Task StartPlaybackAsync()
    {
        if (_selectedMacro is null || _controller is null || _controller.IsRunning)
        {
            return;
        }

        try
        {
            _selectedMacro = PlaybackMacroDocument.Load(_selectedMacro.FilePath);
        }
        catch (Exception ex)
        {
            SetStatus("巨集驗證失敗：" + ex.Message, Color.MistyRose, Color.DarkRed);
            return;
        }

        EffectivePlaybackPrivilegeResolution privilege = EffectivePlaybackPrivilegeResolver.Resolve(_selectedMacro);
        if (privilege.Requirement == EffectivePlaybackPrivilegeRequirement.Administrator && !WindowsPlayerPrivilege.IsElevated)
        {
            SetStatus(privilege.Reason, Color.MistyRose, Color.DarkRed);
            _elevateButton.Visible = true;
            return;
        }

        if (privilege.Requirement == EffectivePlaybackPrivilegeRequirement.Unknown && !WindowsPlayerPrivilege.IsElevated)
        {
            DialogResult choice = MessageBox.Show(
                "此巨集的權限需求未知。可在一般模式嘗試，若目標是管理員程式則應取消並改用管理員模式。是否繼續？",
                "權限需求未知", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (choice != DialogResult.Yes)
            {
                return;
            }
        }

        if (!_selectedMacro.MatchesCurrentScreen(SystemInformation.VirtualScreen))
        {
            DialogResult choice = MessageBox.Show(
                $"巨集螢幕配置：{_selectedMacro.ScreenSummary}\r\n目前配置：{SystemInformation.VirtualScreen.Width} x {SystemInformation.VirtualScreen.Height}，原點 ({SystemInformation.VirtualScreen.Left}, {SystemInformation.VirtualScreen.Top})\r\n\r\n配置不同可能使座標落點錯誤。是否仍要繼續？",
                "螢幕配置不同", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (choice != DialogResult.Yes)
            {
                return;
            }
        }

        if (!TryCapturePlaybackSessionOptions(out PlaybackSessionOptionsSnapshot? sessionOptions, out string sessionOptionError) || sessionOptions is null)
        {
            MessageBox.Show(this, sessionOptionError, "無法開始播放", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        PlayerCountdownMode countdownMode = sessionOptions.CountdownMode;
        _presentationFailureMessage = null;
        _playbackCancellation = new CancellationTokenSource();
        SetInteractive(false);
        PlaybackRunResult result;
        try
        {
            result = await _controller.StartAsync(
                _selectedMacro,
                countdownMode,
                WindowsPlayerPrivilege.IsElevated,
                _playbackCancellation.Token);
        }
        finally
        {
            SetInteractive(true);
            _playbackCancellation.Dispose();
            _playbackCancellation = null;
        }

        if (_replacementExitRequested || _emergencyExitRequested)
        {
            _internalCloseRequested = true;
            _windowModeService?.AllowInternalClose();
            Close();
            return;
        }
        if (!string.IsNullOrWhiteSpace(_presentationFailureMessage))
        {
            SetStatus("播放失敗：Player 保持可見狀態無法安全修復。\r\n" + _presentationFailureMessage,
                Color.MistyRose, Color.DarkRed);
            _presentationFailureMessage = null;
            return;
        }

        UpdateLastTimingDetails(result);
        if (result.Completed)
        {
            bool timingWarning = IsTimingWarning(result);
            SetStatus($"播放完畢\r\n完成時間：{DateTime.Now:yyyy-MM-dd HH:mm:ss}，送出事件：{result.EventsSent}\r\n{FormatCompletionTiming(result)}",
                timingWarning ? Color.FromArgb(255, 244, 214) : Color.FromArgb(219, 243, 224),
                timingWarning ? Color.FromArgb(120, 70, 0) : Color.FromArgb(18, 100, 45));
        }
        else if (result.Cancelled)
        {
            SetStatus("播放已停止。所有可能按住的按鍵與滑鼠按鈕已釋放。", Color.FromArgb(255, 244, 214), Color.FromArgb(120, 70, 0));
        }
        else
        {
            SetStatus(result.Message, Color.MistyRose, Color.DarkRed);
        }
        if (_emergencyExitRequested)
        {
            Close();
        }
    }

    private void StopPlayback()
    {
        _playbackCancellation?.Cancel();
        _controller?.Stop();
    }

    private void RefreshRecordings()
    {
        string? selectedPath = _selectedMacro?.FilePath;
        IEnumerable<string> paths = Directory.EnumerateFiles(PlayerRuntimePaths.Recordings, "*.macro", SearchOption.TopDirectoryOnly);
        string legacy = Path.Combine(PlayerRuntimePaths.ProjectRoot, "Program", "App", "Recorder", "Recordings");
        if (Directory.Exists(legacy) && !Path.GetFullPath(legacy).Equals(Path.GetFullPath(PlayerRuntimePaths.Recordings), StringComparison.OrdinalIgnoreCase))
        {
            paths = paths.Concat(Directory.EnumerateFiles(legacy, "*.macro", SearchOption.TopDirectoryOnly));
        }

        FileListItem[] items = paths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new FileListItem(Path.GetFullPath(path), File.GetCreationTime(path), File.GetLastWriteTime(path)))
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.ModifiedAt)
            .ToArray();
        _recordingsList.BeginUpdate();
        _recordingsList.Items.Clear();
        _recordingsList.Items.AddRange(items);
        _recordingsList.EndUpdate();
        if (!string.IsNullOrWhiteSpace(selectedPath))
        {
            SelectPathInList(selectedPath);
        }

        if (_recordingsList.SelectedIndex < 0 && _recordingsList.Items.Count > 0)
        {
            _recordingsList.SelectedIndex = 0;
        }

        if (_recordingsList.Items.Count == 0)
        {
            _details.Text = "Recordings 資料夾目前沒有 .macro 檔案。";
            _selectedMacro = null;
            UpdateStartAvailability();
        }
    }

    private void CaptureLastForeground()
    {
        ForegroundSnapshot? current = _foreground.CaptureCurrent();
        if (current is not null)
        {
            _lastNonToolForeground = current;
        }
    }

    private void MonitorEmergencyStop()
    {
        if (_controller?.IsRunning != true)
        {
            _f11Held = null;
            return;
        }

        if ((GetAsyncKeyState(0x7A) & 0x8000) != 0)
        {
            _f11Held ??= Stopwatch.StartNew();
            if (_f11Held.Elapsed >= TimeSpan.FromSeconds(2))
            {
                StopPlayback();
                _f11Held = null;
            }
        }
        else
        {
            _f11Held = null;
        }
    }

    private void LoadSelectedListItem()
    {
        if (_recordingsList.SelectedItem is FileListItem item)
        {
            LoadMacro(item.Path);
        }
    }

    private void LoadMacro(string path)
    {
        try
        {
            PlaybackMacroDocument macro = PlaybackMacroDocument.Load(path);
            _selectedMacro = macro;
            EffectivePlaybackPrivilegeResolution effective = EffectivePlaybackPrivilegeResolver.Resolve(macro);
            string privilege = effective.Requirement switch
            {
                EffectivePlaybackPrivilegeRequirement.Administrator => "管理員",
                EffectivePlaybackPrivilegeRequirement.Standard => "一般",
                _ => "未知"
            };
            _details.Text = string.Join(Environment.NewLine, new[]
            {
                $"巨集名稱：{macro.Name}",
                $"完整檔名：{Path.GetFileName(macro.FilePath)}",
                $"建立時間：{macro.CreatedAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss") ?? File.GetCreationTime(macro.FilePath).ToString("yyyy-MM-dd HH:mm:ss")}",
                $"錄製長度：{TimeSpan.FromMilliseconds(macro.DurationMilliseconds):hh\\:mm\\:ss\\.fff}",
                $"事件時間軸：{TimeSpan.FromMilliseconds(GetEventTimelineMilliseconds(macro)):hh\\:mm\\:ss\\.fff}",
                "播放倒數：固定 5 秒，與事件時間軸分開計算",
                $"上次實際播放：{GetLastTimingSummary(macro.FilePath)}",
                $"事件總數：{macro.Events.Count}",
                $"錄製模式：{macro.CaptureMode}",
                $"權限需求：{privilege}",
                $"權限 metadata 一致性：{effective.Consistency}",
                $"目標程式 metadata：{(string.IsNullOrWhiteSpace(macro.TargetProcessName) ? "未提供" : macro.TargetProcessName)}",
                $"目標視窗 metadata：{(string.IsNullOrWhiteSpace(macro.TargetWindowTitle) ? "未提供" : macro.TargetWindowTitle)}",
                $"螢幕配置：{macro.ScreenSummary}",
                $"Schema：{macro.SchemaVersion}"
            });
            PlayerPrivilegeUiDecision privilegeUi = PlayerPrivilegeUiPolicy.Resolve(macro, WindowsPlayerPrivilege.IsElevated);
            _elevateButton.Visible = privilegeUi.ElevateVisible;
            if (effective.Requirement == EffectivePlaybackPrivilegeRequirement.Administrator && !WindowsPlayerPrivilege.IsElevated)
            {
                SetStatus(effective.Reason, Color.MistyRose, Color.DarkRed);
                _elevateButton.Visible = true;
            }
            else
            {
                SetStatus("巨集已載入；直接重播於目前桌面，按下開始後倒數 5 秒。",
                    Color.White, Color.FromArgb(35, 65, 45));
            }
        }
        catch (Exception ex)
        {
            _selectedMacro = null;
            _details.Text = "無法載入：" + ex.Message;
            SetStatus("檔案損壞或格式不支援；不會倒數，也不會送出任何輸入。", Color.MistyRose, Color.DarkRed);
        }

        UpdateStartAvailability();
    }

    private void SelectExternalMacro(string path)
    {
        if (!path.EndsWith(".macro", StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("請選擇副檔名為 .macro 的檔案。", Color.MistyRose, Color.DarkRed);
            return;
        }

        if (!SelectPathInList(path))
        {
            _recordingsList.ClearSelected();
            LoadMacro(path);
        }
    }

    private bool SelectPathInList(string path)
    {
        string fullPath = Path.GetFullPath(path);
        for (int index = 0; index < _recordingsList.Items.Count; index++)
        {
            if (_recordingsList.Items[index] is FileListItem item && item.Path.Equals(fullPath, StringComparison.OrdinalIgnoreCase))
            {
                _recordingsList.SelectedIndex = index;
                return true;
            }
        }

        return false;
    }

    private void ChooseExternalMacro()
    {
        using OpenFileDialog dialog = new()
        {
            Title = "選擇巨集檔案",
            Filter = "巨集檔案 (*.macro)|*.macro",
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = PlayerRuntimePaths.Recordings
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            SelectExternalMacro(dialog.FileName);
        }
    }

    private static void OpenRecordingsFolder()
    {
        Directory.CreateDirectory(PlayerRuntimePaths.Recordings);
        Process.Start(new ProcessStartInfo("explorer.exe")
        {
            UseShellExecute = true,
            ArgumentList = { PlayerRuntimePaths.Recordings }
        });
    }

    private void RelaunchElevated()
    {
        if (WindowsPlayerPrivilege.IsElevated)
        {
            return;
        }

        try
        {
            string executable = Environment.ProcessPath ?? throw new InvalidOperationException("找不到播放器執行檔路徑。");
            ProcessStartInfo start = new(executable)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(executable)!
            };
            PlayerCountdownMode countdown = (_countdownMode.SelectedItem as ModeChoice)?.Mode ?? _startupSettings.CountdownMode;
            foreach (string argument in PlayerElevationRelaunchArguments.Build(
                PlayerRuntimePaths.ProjectRoot,
                _selectedMacro?.FilePath,
                countdown))
            {
                start.ArgumentList.Add(argument);
            }

            _ = Process.Start(start) ?? throw new InvalidOperationException("管理員播放器未啟動。");
            Close();
        }
        catch (System.ComponentModel.Win32Exception ex) when (PlayerElevationPolicy.IsUserCancellation(ex))
        {
            SetStatus("已取消管理員模式。普通播放器仍可安全使用。", Color.FromArgb(255, 244, 214), Color.FromArgb(110, 65, 0));
        }
        catch (Exception ex)
        {
            SetStatus("無法啟動管理員播放器：" + ex.Message, Color.MistyRose, Color.DarkRed);
        }
    }

    private void SetInteractive(bool enabled)
    {
        _startButton.Enabled = enabled && CanStart();
        _stopButton.Enabled = !enabled;
        _refreshButton.Enabled = enabled;
        _openFolderButton.Enabled = enabled;
        _chooseButton.Enabled = enabled;
        _elevateButton.Enabled = enabled;
        _recordingsList.Enabled = enabled;
        _countdownMode.Enabled = enabled;
        _closeButton.Enabled = enabled;
    }

    private void UpdateStartAvailability() => _startButton.Enabled = CanStart() && _controller?.IsRunning != true;

    private bool CanStart()
    {
        if (_selectedMacro is not null && !AbsoluteOnlyPlaybackGate.TryValidate(_selectedMacro, out _))
        {
            return false;
        }
        if (!PlayerPrivilegeUiPolicy.Resolve(_selectedMacro, WindowsPlayerPrivilege.IsElevated).StartEnabled)
        {
            return false;
        }

        return true;
    }

    private void UpdateState(PlaybackWorkflowState state)
    {
        if (state == PlaybackWorkflowState.Validating)
        {
            SetStatus("正在重新驗證巨集、螢幕配置、權限與桌面安全狀態。",
                Color.White, Color.FromArgb(35, 65, 45));
        }
    }

    private void SetStatus(string text, Color background, Color foreground)
    {
        _status.Text = text;
        _status.BackColor = background;
        _status.ForeColor = foreground;
    }

    private void Ui(Action action)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke((MethodInvoker)(() => action()));
        }
        else
        {
            action();
        }
    }

    private static TableLayoutPanel TwoColumnLayout(int rightWidth)
    {
        TableLayoutPanel panel = new() { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, rightWidth));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        return panel;
    }

    private static void ConfigureButton(Button button, string name, string text, int width)
    {
        button.Name = name;
        button.Text = text;
        button.Width = width;
        button.Height = 36;
        button.Margin = new Padding(0, 0, 8, 0);
        button.AccessibleName = text;
    }

    private sealed record FileListItem(string Path, DateTime CreatedAt, DateTime ModifiedAt)
    {
        public override string ToString() => $"{System.IO.Path.GetFileName(Path)}    {CreatedAt:yyyy-MM-dd HH:mm:ss}";
    }

    private sealed record ModeChoice(PlayerCountdownMode Mode, string Text)
    {
        public override string ToString() => Text;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}

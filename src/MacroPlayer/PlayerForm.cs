using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using MacroCore.Models;
using MacroCore.Security;
using MacroCore.Runtime;
using MacroCore.Serialization;
using MacroPlayer.Services;

namespace MacroPlayer;

public sealed class PlayerForm : Form
{
    // Retired compatibility UI. Program.Main intentionally launches PlaybackLibraryForm.
    private readonly MacroToolLaunchOptions _launchOptions;
    private readonly WindowsPrivilegeService _privilegeService = new();
    private readonly MacroLibraryService _library = new();
    private readonly ListBox _macroList = new();
    private readonly Label _modeLabel = new();
    private readonly Label _detailsLabel = new();
    private readonly Label _statusLabel = new();
    private readonly Label _progressLabel = new();
    private readonly Button _startButton = new();
    private readonly Button _cancelButton = new();
    private readonly Button _elevateButton = new();
    private readonly ComboBox _countdownWindowBehavior = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly System.Windows.Forms.Timer _progressTimer = new() { Interval = 200 };
    private CancellationTokenSource? _operationCancellation;
    private PlaybackService? _playback;
    private DateTimeOffset _playbackStarted;
    private MacroLibraryItem? _selected;
    private bool _busy;
    private bool _autoMinimized;

    public PlayerForm(MacroToolLaunchOptions launchOptions)
    {
        _launchOptions = launchOptions;
        Text = "巨集重播";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(820, 590);
        Size = new Size(900, 650);
        TopMost = false;
        ShowInTaskbar = true;
        AllowDrop = true;

        BuildUi();
        WireEvents();
    }

    private void BuildUi()
    {
        var integrity = _privilegeService.GetCurrentIntegrity();
        var high = integrity >= WindowsIntegrityLevel.High;
        _modeLabel.Text = high
            ? "管理員重播模式  |  Player Integrity：High"
            : "一般重播模式  |  Player Integrity：Medium";
        _modeLabel.ForeColor = high ? Color.DarkOrange : Color.DarkSlateBlue;
        _modeLabel.Font = new Font("Microsoft JhengHei UI", 13, FontStyle.Bold);
        _modeLabel.AutoSize = true;
        _modeLabel.Margin = new Padding(12);

        _macroList.Dock = DockStyle.Fill;
        _macroList.Font = new Font("Microsoft JhengHei UI", 10);
        _macroList.HorizontalScrollbar = true;
        _macroList.DisplayMember = nameof(MacroLibraryItem.DisplayName);

        _detailsLabel.Dock = DockStyle.Fill;
        _detailsLabel.Padding = new Padding(12);
        _detailsLabel.Font = new Font("Microsoft JhengHei UI", 10);
        _detailsLabel.Text = "請選取一個 .macro 檔案。";
        _detailsLabel.BorderStyle = BorderStyle.FixedSingle;

        _statusLabel.AutoSize = true;
        _statusLabel.Font = new Font("Microsoft JhengHei UI", 16, FontStyle.Bold);
        _statusLabel.Text = "等待選擇";
        _progressLabel.AutoSize = true;
        _progressLabel.Font = new Font("Microsoft JhengHei UI", 10);
        _progressLabel.Text = "長按 F11 2 秒可緊急停止播放";

        var refresh = NewButton("重新整理");
        var openFolder = NewButton("開啟 Recordings 資料夾");
        var otherFile = NewButton("選擇其他 .macro 檔案");
        _startButton.Text = "開始播放";
        _startButton.AutoSize = true;
        _startButton.Enabled = false;
        _cancelButton.Text = "取消／停止";
        _cancelButton.AutoSize = true;
        _cancelButton.Enabled = false;
        _elevateButton.Text = "以管理員模式重新開啟";
        _elevateButton.AutoSize = true;
        _elevateButton.Visible = false;
        var close = NewButton("關閉");

        _countdownWindowBehavior.Items.AddRange(["倒數時保留播放器", "開始播放前最小化播放器"]);
        _countdownWindowBehavior.SelectedIndex = 0;

        refresh.Click += (_, _) => RefreshLibrary();
        openFolder.Click += (_, _) => OpenRecordingsFolder();
        otherFile.Click += (_, _) => SelectOtherFile();
        _startButton.Click += async (_, _) => await StartSelectedAsync();
        _cancelButton.Click += (_, _) => CancelCurrentOperation();
        _elevateButton.Click += (_, _) => RelaunchElevated();
        close.Click += (_, _) => Close();

        var manual = NewButton("操作手冊");
        manual.Click += (_, _) => OpenManual();
        var topButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        topButtons.Controls.AddRange(new Control[]
        {
            refresh, openFolder, otherFile, manual,
            new Label { Text = "倒數視窗：", AutoSize = true, Padding = new Padding(8, 7, 0, 0) },
            _countdownWindowBehavior
        });
        var bottomButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        bottomButtons.Controls.AddRange(new Control[] { _startButton, _cancelButton, _elevateButton, close });

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 390 };
        split.Panel1.Controls.Add(_macroList);
        split.Panel2.Controls.Add(_detailsLabel);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 6 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(_modeLabel, 0, 0);
        layout.Controls.Add(topButtons, 0, 1);
        layout.Controls.Add(split, 0, 2);
        layout.Controls.Add(_statusLabel, 0, 3);
        layout.Controls.Add(_progressLabel, 0, 4);
        layout.Controls.Add(bottomButtons, 0, 5);
        Controls.Add(layout);
    }

    private void WireEvents()
    {
        Load += (_, _) =>
        {
            RefreshLibrary();
            if (!string.IsNullOrWhiteSpace(_launchOptions.PreselectedMacroPath))
            {
                PreselectPath(_launchOptions.PreselectedMacroPath);
            }
            LaunchReadiness.SignalApplicationReady();
        };
        _macroList.SelectedIndexChanged += (_, _) => SelectItem(_macroList.SelectedItem as MacroLibraryItem);
        FormClosing += (_, _) => CancelCurrentOperation();
        DragEnter += (_, eventArgs) =>
        {
            if (eventArgs.Data?.GetDataPresent(DataFormats.FileDrop) == true)
            {
                eventArgs.Effect = DragDropEffects.Copy;
            }
        };
        DragDrop += (_, eventArgs) =>
        {
            if (eventArgs.Data?.GetData(DataFormats.FileDrop) is string[] files)
            {
                var path = files.FirstOrDefault(file => string.Equals(Path.GetExtension(file), ".macro", StringComparison.OrdinalIgnoreCase));
                if (path is not null)
                {
                    PreselectPath(path);
                }
            }
        };
        _progressTimer.Tick += (_, _) => UpdatePlaybackProgress();
    }

    private static Button NewButton(string text) => new() { Text = text, AutoSize = true };

    private void RefreshLibrary()
    {
        var selectedPath = _selected?.FullPath;
        var items = _library.Scan();
        _macroList.BeginUpdate();
        try
        {
            _macroList.Items.Clear();
            _macroList.Items.AddRange(items.Cast<object>().ToArray());
        }
        finally
        {
            _macroList.EndUpdate();
        }

        if (selectedPath is not null)
        {
            SelectListPath(selectedPath);
        }
        _statusLabel.Text = items.Count == 0 ? "Recordings 中尚無巨集" : $"找到 {items.Count} 個巨集";
    }

    private void PreselectPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (SelectListPath(fullPath))
        {
            return;
        }

        var item = _library.LoadSingle(fullPath);
        _macroList.Items.Insert(0, item);
        _macroList.SelectedItem = item;
    }

    private bool SelectListPath(string path)
    {
        for (var index = 0; index < _macroList.Items.Count; index++)
        {
            if (_macroList.Items[index] is MacroLibraryItem item && string.Equals(item.FullPath, path, StringComparison.OrdinalIgnoreCase))
            {
                _macroList.SelectedIndex = index;
                return true;
            }
        }
        return false;
    }

    private void SelectItem(MacroLibraryItem? item)
    {
        _selected = item;
        _elevateButton.Visible = false;
        _startButton.Enabled = !_busy && item is { IsValid: true };

        if (item is null)
        {
            _detailsLabel.Text = "請選取一個 .macro 檔案。";
            return;
        }

        if (!item.IsValid || item.Macro is null)
        {
            _detailsLabel.Text = $"檔名：{item.FileName}\r\n狀態：無法載入\r\n錯誤：{item.Error}";
            _statusLabel.Text = "巨集檔案無效，未開始倒數，也不會送出輸入。";
            return;
        }

        var requirement = PrivilegePolicy.GetPlaybackRequirement(item.Macro);
        var permissionText = requirement switch
        {
            PlaybackPrivilegeRequirement.Administrator => "管理員",
            PlaybackPrivilegeRequirement.Normal => "一般",
            _ => "未知"
        };
        _elevateButton.Visible = requirement != PlaybackPrivilegeRequirement.Normal &&
                                 _privilegeService.GetCurrentIntegrity() < WindowsIntegrityLevel.High;
        _detailsLabel.Text =
            $"巨集名稱：{ModelDisplay.ReadName(item.Macro, Path.GetFileNameWithoutExtension(item.FileName))}\r\n" +
            $"完整檔名：{item.FileName}\r\n" +
            $"建立時間：{item.CreatedLocal:yyyy-MM-dd HH:mm:ss}\r\n" +
            $"錄製長度：{ModelDisplay.Duration(item.Macro)}\r\n" +
            $"事件總數：{item.Macro.Events.Count}\r\n" +
            $"錄製模式：{item.Macro.CaptureMetadata?.CaptureMode ?? "未知"}\r\n" +
            $"捕捉來源：{ModelDisplay.CaptureSources(item.Macro)}\r\n" +
            $"去重事件：{item.Macro.CaptureMetadata?.DuplicateCount ?? 0}\r\n" +
            $"鍵盤／滑鼠事件：{ModelDisplay.EventCounts(item.Macro)}\r\n" +
            $"ESC Down／Up：{ModelDisplay.EscapeCounts(item.Macro)}\r\n" +
            $"特殊鍵驗證：{ModelDisplay.SpecialKeyValidation(item.Macro)}\r\n" +
            $"滑鼠座標模式：{ModelDisplay.MouseModes(item.Macro)}\r\n" +
            $"權限需求：{permissionText}\r\n" +
            $"螢幕配置摘要：{ModelDisplay.ScreenSummary(item.Macro)}";
    }

    private async Task StartSelectedAsync()
    {
        if (_busy || _selected is null)
        {
            return;
        }

        var reloaded = _library.LoadSingle(_selected.FullPath);
        if (!reloaded.IsValid || reloaded.Macro is null)
        {
            SelectItem(reloaded);
            MessageBox.Show(this, reloaded.Error ?? "巨集檔案損壞或不支援。", "無法播放", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var requirement = PrivilegePolicy.GetPlaybackRequirement(reloaded.Macro);
        if (!DisplayLayoutGate.MatchesCurrent(reloaded.Macro, out var displayDifference))
        {
            var displayChoice = MessageBox.Show(this,
                "目前螢幕配置與錄製時不同，座標與縮放可能不準確。\r\n" + displayDifference + "\r\n\r\n是否仍要繼續？",
                "螢幕配置警告", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (displayChoice != DialogResult.Yes)
            {
                _statusLabel.Text = "已取消：螢幕配置不同。";
                return;
            }
        }

        if (!PrivilegePolicy.CanPlay(_privilegeService.GetCurrentIntegrity(), requirement))
        {
            _statusLabel.Text = "目標或巨集需要管理員權限，請改用管理員模式。";
            _elevateButton.Visible = true;
            MessageBox.Show(this, "此巨集錄製於管理員權限目標，普通模式無法可靠重播。", "需要管理員模式", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (requirement == PlaybackPrivilegeRequirement.Unknown && _privilegeService.GetCurrentIntegrity() < WindowsIntegrityLevel.High)
        {
            var choice = MessageBox.Show(this,
                "此巨集的權限需求未知。可在一般模式嘗試，或取消後按「以管理員模式重新開啟」。是否在一般模式繼續？",
                "權限需求未知", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (choice != DialogResult.Yes)
            {
                return;
            }
        }

        _selected = reloaded;
        SetBusy(true);
        _operationCancellation = new CancellationTokenSource();
        var cancellationToken = _operationCancellation.Token;
        try
        {
            for (var remaining = 5; remaining >= 1; remaining--)
            {
                _statusLabel.ForeColor = Color.DarkOrange;
                _statusLabel.Text = $"{ModelDisplay.ReadName(reloaded.Macro, Path.GetFileNameWithoutExtension(reloaded.FileName))} 即將播放：{remaining}";
                _progressLabel.Text = "長按 F11 2 秒緊急停止";
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }

            if (_countdownWindowBehavior.SelectedIndex == 1)
            {
                WindowState = FormWindowState.Minimized;
                _autoMinimized = true;
                await Task.Delay(150, cancellationToken);
            }

            if (PlayerForegroundGuard.IsPlayerForeground(Handle))
            {
                _statusLabel.ForeColor = Color.DarkOrange;
                _statusLabel.Text = "播放已停止";
                _progressLabel.Text = "播放器仍在前景，因此沒有送出任何輸入。請重新開始並在倒數期間切換到目標程式。";
                return;
            }

            _statusLabel.Text = "播放中";
            _statusLabel.ForeColor = Color.DarkBlue;
            _playbackStarted = DateTimeOffset.Now;
            _playback = new PlaybackService(reloaded.Macro);
            _progressTimer.Start();
            await PlaybackInvoker.PlayOnceAsync(_playback, reloaded.Macro, cancellationToken);
            _statusLabel.ForeColor = Color.Green;
            _statusLabel.Text = "播放完畢";
            _progressLabel.Text = $"完成時間：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}。可選擇其他巨集繼續播放。";
        }
        catch (OperationCanceledException)
        {
            _statusLabel.ForeColor = Color.DarkOrange;
            _statusLabel.Text = "播放已停止";
        }
        catch (Exception exception)
        {
            _statusLabel.ForeColor = Color.DarkRed;
            _statusLabel.Text = "播放失敗";
            _progressLabel.Text = "未繼續送出輸入；可重新選擇巨集。";
            WritePlaybackError(exception);
            MessageBox.Show(this, $"播放失敗：{exception.Message}", "播放失敗", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _progressTimer.Stop();
            _playback?.Dispose();
            _playback = null;
            _operationCancellation?.Dispose();
            _operationCancellation = null;
            SetBusy(false);
            RestorePlayerWindowIfNeeded();
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _macroList.Enabled = !busy;
        _startButton.Enabled = !busy && _selected is { IsValid: true };
        _cancelButton.Enabled = busy;
        _countdownWindowBehavior.Enabled = !busy;
    }

    private void CancelCurrentOperation()
    {
        _operationCancellation?.Cancel();
    }

    private void RestorePlayerWindowIfNeeded()
    {
        if (!_autoMinimized)
        {
            return;
        }
        WindowState = FormWindowState.Normal;
        ShowInTaskbar = true;
        _autoMinimized = false;
        Activate();
    }

    private void UpdatePlaybackProgress()
    {
        if (_selected?.Macro is not { } macro)
        {
            return;
        }
        var elapsed = Math.Max(0, (DateTimeOffset.Now - _playbackStarted).TotalMilliseconds);
        var current = macro.Events.Count(evt => evt.TimeMs <= elapsed);
        _progressLabel.Text = $"目前事件：{current} / {macro.Events.Count}  |  經過：{elapsed / 1000:0.0} 秒  |  長按 F11 2 秒緊急停止";
    }

    private void RelaunchElevated()
    {
        if (_selected is null || _privilegeService.GetCurrentIntegrity() >= WindowsIntegrityLevel.High)
        {
            return;
        }
        var launcher = new CompiledMacroLauncherClient();
        var result = launcher.LaunchElevatedPlayer(_selected.FullPath, out var error);
        if (result == ElevationLaunchResult.Started)
        {
            Close();
        }
        else if (result == ElevationLaunchResult.Cancelled)
        {
            _statusLabel.Text = "已取消管理員模式";
        }
        else
        {
            MessageBox.Show(this, $"無法啟動管理員模式：{error}", "啟動失敗", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenRecordingsFolder()
    {
        Directory.CreateDirectory(RecordingLibraryPaths.CanonicalRecordingsDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{RecordingLibraryPaths.CanonicalRecordingsDirectory}\"") { UseShellExecute = true });
    }

    private void OpenManual()
    {
        try
        {
            Process.Start(new ProcessStartInfo(RuntimeFolders.Manual) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"無法開啟操作手冊：{ex.Message}", "操作手冊", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SelectOtherFile()
    {
        using var dialog = new OpenFileDialog { Filter = "巨集檔案 (*.macro)|*.macro", CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            PreselectPath(dialog.FileName);
        }
    }

    private static void WritePlaybackError(Exception exception)
    {
        try
        {
            var log = Path.Combine(RuntimeFolders.Logs, "player_errors.log");
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            MacroCore.Diagnostics.RotatingLog.Write(log, $"{DateTimeOffset.Now:O}\t{exception.GetType().Name}\t{exception.Message}");
        }
        catch
        {
        }
    }
}

internal static class ModelDisplay
{
    public static string ReadName(MacroFile macro, string fallback)
    {
        var value = macro.GetType().GetProperty("Name")?.GetValue(macro) as string;
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    public static string Duration(MacroFile macro)
    {
        var milliseconds = macro.Events.Count == 0 ? 0 : macro.Events.Max(item => item.TimeMs);
        return TimeSpan.FromMilliseconds(milliseconds).ToString(@"hh\:mm\:ss\.fff");
    }

    public static string CaptureSources(MacroFile macro)
    {
        var groups = macro.Events
            .GroupBy(item => item.CaptureSource)
            .OrderBy(group => group.Key.ToString(), StringComparer.Ordinal)
            .Select(group => $"{group.Key}={group.Count()}")
            .ToArray();
        return groups.Length == 0 ? "無事件" : string.Join(", ", groups);
    }

    public static string EventCounts(MacroFile macro)
    {
        var keyboard = macro.Events.Count(item => item.Type is MacroEventKind.KeyDown or MacroEventKind.KeyUp);
        return $"keyboard={keyboard}, mouse={macro.Events.Count - keyboard}";
    }

    public static string EscapeCounts(MacroFile macro)
    {
        var down = macro.Events.Count(item => item.VirtualKey == 0x1B && item.Type == MacroEventKind.KeyDown);
        var up = macro.Events.Count(item => item.VirtualKey == 0x1B && item.Type == MacroEventKind.KeyUp);
        return $"{down}／{up}";
    }

    public static string SpecialKeyValidation(MacroFile macro)
    {
        var specialKeys = macro.Events
            .Where(item => item.VirtualKey is 0x1B or 0x09 or 0x0D or 0x08 ||
                           item.VirtualKey is >= 0x21 and <= 0x2E ||
                           item.VirtualKey is >= 0x70 and <= 0x87)
            .Select(item => item.VirtualKey)
            .Distinct()
            .Count();
        return specialKeys == 0 ? "檔案未含特殊鍵（不補造）" : $"已保存 {specialKeys} 種特殊鍵";
    }

    public static string MouseModes(MacroFile macro)
    {
        var absolute = macro.Events.Count(item => item.Type == MacroEventKind.MouseMove && item.X.HasValue && item.Y.HasValue);
        var missingAbsolute = macro.Events.Count(item => item.Type == MacroEventKind.MouseMove && (!item.X.HasValue || !item.Y.HasValue));
        return $"absolute={absolute}, missing_absolute={missingAbsolute}";
    }

    public static string ScreenSummary(MacroFile macro)
    {
        var property = macro.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(item => item.Name.Contains("Desktop", StringComparison.OrdinalIgnoreCase) || item.Name.Contains("Screen", StringComparison.OrdinalIgnoreCase));
        if (property?.GetValue(macro) is not { } value)
        {
            return "未知";
        }
        var json = JsonSerializer.Serialize(value, MacroSerializer.SerializerOptions);
        return json.Length <= 180 ? json : json[..180] + "…";
    }
}

internal static class PlaybackInvoker
{
    public static async Task PlayOnceAsync(PlaybackService service, MacroFile macro, CancellationToken cancellationToken)
    {
        var method = typeof(PlaybackService).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Single(candidate => candidate.Name == "PlayAsync");
        var arguments = method.GetParameters().Select(parameter => CreateArgument(parameter, macro, cancellationToken)).ToArray();
        var result = method.Invoke(service, arguments);
        if (result is Task task)
        {
            await task.WaitAsync(cancellationToken);
        }
    }

    private static object? CreateArgument(ParameterInfo parameter, MacroFile macro, CancellationToken cancellationToken)
    {
        if (parameter.ParameterType == typeof(MacroFile))
        {
            return macro;
        }
        if (parameter.ParameterType == typeof(CancellationToken))
        {
            return cancellationToken;
        }
        if (parameter.HasDefaultValue)
        {
            return parameter.DefaultValue;
        }
        return null;
    }
}

internal static class DisplayLayoutGate
{
    public static bool MatchesCurrent(MacroFile macro, out string difference)
    {
        difference = string.Empty;
        try
        {
            var recordedProperty = macro.GetType().GetProperty("RecordedDisplayLayout", BindingFlags.Instance | BindingFlags.Public);
            var recorded = recordedProperty?.GetValue(macro);
            if (recorded is null)
            {
                return true;
            }

            var provider = typeof(MacroFile).Assembly.GetTypes().FirstOrDefault(type => type.Name == "DisplayLayoutProvider");
            var method = provider?.GetMethod("GetCurrentLayout", BindingFlags.Public | BindingFlags.Static);
            var current = method?.Invoke(null, null);
            if (current is null)
            {
                difference = "無法取得目前螢幕配置。";
                return false;
            }

            var recordedJson = JsonSerializer.Serialize(recorded, recorded.GetType(), MacroSerializer.SerializerOptions);
            var currentJson = JsonSerializer.Serialize(current, current.GetType(), MacroSerializer.SerializerOptions);
            if (string.Equals(recordedJson, currentJson, StringComparison.Ordinal))
            {
                return true;
            }

            difference = $"錄製：{Compact(recordedJson)}\r\n目前：{Compact(currentJson)}";
            return false;
        }
        catch (Exception exception)
        {
            difference = "配置比較失敗：" + exception.Message;
            return false;
        }
    }

    private static string Compact(string value) => value.Length <= 220 ? value : value[..220] + "…";
}

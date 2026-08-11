using System.Text;

namespace MacroPlayer;

public sealed partial class PlaybackLibraryForm
{
    private readonly PlayerSettings _startupSettings;
    private bool _initializingUi = true;
    private PlaybackSessionOptionsSnapshot? _activeSessionOptions;

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ApplyStableTextLayout();
        _initializingUi = false;
    }

    private void ApplyStableTextLayout()
    {
        _desktopScopeHelp.AutoSize = true;
        _desktopScopeHelp.AutoEllipsis = false;
        _desktopScopeHelp.Dock = DockStyle.Fill;
        UpdateDesktopScopeMaximumWidth();
        _desktopScopeHelp.ParentChanged += (_, _) => UpdateDesktopScopeMaximumWidth();
        if (_desktopScopeHelp.Parent is Control parent)
        {
            parent.Resize += (_, _) => UpdateDesktopScopeMaximumWidth();
        }

        MakeContainingRowsAutoSize(_desktopScopeTitle);
        MakeContainingRowsAutoSize(_desktopScopeHelp);
        MakeContainingRowsAutoSize(_mouseReplayWarning);
        MakeContainingRowsAutoSize(_mouseReplayCounts);
        if (Controls.OfType<TableLayoutPanel>().FirstOrDefault() is { } root)
        {
            root.AutoScroll = true;
        }
    }

    private void UpdateDesktopScopeMaximumWidth()
    {
        int available = Math.Max(240, (_desktopScopeHelp.Parent?.ClientSize.Width ?? ClientSize.Width) - 12);
        _desktopScopeHelp.MaximumSize = new Size(available, 0);
    }

    private static void MakeContainingRowsAutoSize(Control control)
    {
        for (Control? cursor = control; cursor is not null; cursor = cursor.Parent)
        {
            if (cursor.Parent is not TableLayoutPanel table)
            {
                continue;
            }

            int row = table.GetRow(cursor);
            if (row >= 0 && row < table.RowStyles.Count)
            {
                table.RowStyles[row].SizeType = SizeType.AutoSize;
            }

            if (cursor is GroupBox group)
            {
                group.AutoSize = true;
                group.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            }
        }
    }

    private bool TryCapturePlaybackSessionOptions(
        out PlaybackSessionOptionsSnapshot? snapshot,
        out string error)
    {
        PlayerCountdownMode? uiMode = (_countdownMode.SelectedItem as ModeChoice)?.Mode;
        string uiText = _countdownMode.SelectedItem is null
            ? string.Empty
            : _countdownMode.GetItemText(_countdownMode.SelectedItem) ?? string.Empty;
        PlayerSettings savedAtStart = PlayerSettingsStore.Load();
        bool created = PlaybackSessionOptionsFactory.TryCreate(
            uiMode,
            uiText,
            savedAtStart,
            DateTimeOffset.Now,
            out snapshot,
            out PlaybackSessionModeAudit? audit,
            out error);

        if (audit is not null)
        {
            AppendSessionOptionAudit(audit);
        }

        if (!created || snapshot is null)
        {
            return false;
        }

        _activeSessionOptions = snapshot;
        return true;
    }

    private static void AppendSessionOptionAudit(PlaybackSessionModeAudit audit)
    {
        try
        {
            Directory.CreateDirectory(PlayerRuntimePaths.Logs);
            File.AppendAllText(
                Path.Combine(PlayerRuntimePaths.Logs, "playback_session.log"),
                $"{DateTimeOffset.Now:O}\t{audit.ToLogLine()}{Environment.NewLine}",
                new UTF8Encoding(false));
        }
        catch
        {
            // A diagnostic write failure must not alter input safety behavior.
        }
    }

    internal IReadOnlyDictionary<string, Control> AdditionalProbeControls =>
        new Dictionary<string, Control>(StringComparer.Ordinal)
        {
            ["DesktopScopeTitle"] = _desktopScopeTitle,
            ["DesktopScopeHelp"] = _desktopScopeHelp,
            ["MouseReplayPanel"] = _mouseReplayPanel,
            ["MouseReplayMode"] = _mouseReplayMode,
            ["MouseReplayWarning"] = _mouseReplayWarning,
            ["MouseReplayCounts"] = _mouseReplayCounts
        };

    internal void ConfigureSanitizedProbe(
        PlayerCountdownMode countdownMode)
    {
        _recordingsList.Items.Clear();
        _details.Text = "範例巨集（版面探針）\r\n事件：0\r\n此畫面不含使用者巨集、路徑或輸入內容。";
        for (int index = 0; index < _countdownMode.Items.Count; index++)
        {
            if (_countdownMode.Items[index] is ModeChoice choice && choice.Mode == countdownMode)
            {
                _countdownMode.SelectedIndex = index;
                break;
            }
        }

        UpdateMouseReplayText();
        ApplyStableTextLayout();
        PerformLayout();
    }

    internal void ApplyProgressForProbe(PlaybackProgress progress) => SetStatus(
        $"播放中：{progress.EventsSent} / {progress.TotalEvents}，實際經過 {progress.Elapsed:mm\\:ss}。長按 F11 2 秒緊急停止。",
        Color.FromArgb(229, 240, 255), Color.FromArgb(20, 62, 110));
}

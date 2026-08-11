using MacroCore.Models;

namespace MacroPlayer;

public sealed partial class PlaybackLibraryForm
{
    private readonly GroupBox _mouseReplayPanel = new();
    private readonly TableLayoutPanel _mouseReplayLayout = new();
    private readonly Label _mouseReplayMode = new();
    private readonly Label _mouseReplayWarning = new();
    private readonly Label _mouseReplayCounts = new();

    private void InitializeMouseReplayModeUi()
    {
        _mouseReplayPanel.Name = "MouseReplayPanel";
        _mouseReplayPanel.Text = "滑鼠重播方式";
        _mouseReplayPanel.Dock = DockStyle.Fill;
        _mouseReplayPanel.AutoSize = true;
        _mouseReplayPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _mouseReplayPanel.Padding = new Padding(12, 8, 12, 8);
        _mouseReplayPanel.BackColor = Color.FromArgb(246, 248, 241);

        _mouseReplayLayout.Name = "MouseReplayLayout";
        _mouseReplayLayout.Dock = DockStyle.Fill;
        _mouseReplayLayout.AutoSize = true;
        _mouseReplayLayout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _mouseReplayLayout.ColumnCount = 2;
        _mouseReplayLayout.RowCount = 3;
        _mouseReplayLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _mouseReplayLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _mouseReplayLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _mouseReplayLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _mouseReplayLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        Label modeLabel = new()
        {
            Name = "MouseReplayTitle",
            Text = "方式：",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 5, 8, 5)
        };

        _mouseReplayMode.Name = "MouseReplayMode";
        _mouseReplayMode.Text = "絕對桌面座標（固定）";
        _mouseReplayMode.AutoSize = true;
        _mouseReplayMode.Font = new Font(Font, FontStyle.Bold);
        _mouseReplayMode.ForeColor = Color.DarkGreen;
        _mouseReplayMode.Anchor = AnchorStyles.Left;
        _mouseReplayMode.Dock = DockStyle.Left;
        _mouseReplayMode.Margin = new Padding(0, 2, 0, 4);

        _mouseReplayWarning.Name = "MouseReplayWarning";
        _mouseReplayWarning.AutoSize = true;
        _mouseReplayWarning.Dock = DockStyle.Fill;
        _mouseReplayWarning.Margin = new Padding(0, 2, 0, 4);

        _mouseReplayCounts.Name = "MouseReplayCounts";
        _mouseReplayCounts.AutoSize = true;
        _mouseReplayCounts.Dock = DockStyle.Fill;
        _mouseReplayCounts.Margin = new Padding(0, 0, 0, 2);
        _mouseReplayCounts.ForeColor = Color.DimGray;

        _mouseReplayLayout.Controls.Add(modeLabel, 0, 0);
        _mouseReplayLayout.Controls.Add(_mouseReplayMode, 1, 0);
        _mouseReplayLayout.Controls.Add(_mouseReplayWarning, 0, 1);
        _mouseReplayLayout.SetColumnSpan(_mouseReplayWarning, 2);
        _mouseReplayLayout.Controls.Add(_mouseReplayCounts, 0, 2);
        _mouseReplayLayout.SetColumnSpan(_mouseReplayCounts, 2);
        _mouseReplayPanel.Controls.Add(_mouseReplayLayout);
        AddMouseReplayPanelToRootLayout();

        _recordingsList.SelectedIndexChanged += (_, _) => UpdateMouseReplayForSelection();
        _mouseReplayPanel.Resize += (_, _) => UpdateMouseReplayMaximumWidths();
        UpdateMouseReplayText();
        UpdateMouseReplayMaximumWidths();
    }

    private void AddMouseReplayPanelToRootLayout()
    {
        TableLayoutPanel root = Controls.OfType<TableLayoutPanel>()
            .OrderByDescending(layout => layout.Controls.Count)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("Player root layout was not created before mouse replay UI initialization.");
        root.AutoScroll = true;
        int row = root.RowCount;
        root.RowCount++;
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(_mouseReplayPanel, 0, row);
        root.SetColumnSpan(_mouseReplayPanel, Math.Max(1, root.ColumnCount));
    }

    private void UpdateMouseReplayMaximumWidths()
    {
        int available = Math.Max(240, _mouseReplayPanel.ClientSize.Width - _mouseReplayPanel.Padding.Horizontal - 8);
        _mouseReplayWarning.MaximumSize = new Size(available, 0);
        _mouseReplayCounts.MaximumSize = new Size(available, 0);
    }

    private void UpdateMouseReplayForSelection()
    {
        PlaybackMacroDocument? macro = _selectedMacro;
        if (macro is null)
        {
            _mouseReplayCounts.Text = "尚未選取巨集。";
            UpdateMouseReplayText();
            return;
        }

        var moves = macro.Events.Where(item => item.Kind == PlaybackEventKind.MouseMove).ToArray();
        int absolute = moves.Count(item => item.HasAbsolutePosition);
        int missingAbsolute = moves.Length - absolute;
        int clicks = macro.Events.Count(item => item.Kind is PlaybackEventKind.MouseDown or PlaybackEventKind.MouseUp);
        int wheels = macro.Events.Count(item => item.Kind is PlaybackEventKind.MouseWheel or PlaybackEventKind.MouseHorizontalWheel);
        int xButtons = macro.Events.Count(item => item.MouseButton.Equals("X1", StringComparison.OrdinalIgnoreCase) ||
                                                   item.MouseButton.Equals("X2", StringComparison.OrdinalIgnoreCase));
        int horizontal = macro.Events.Count(item => item.Kind == PlaybackEventKind.MouseHorizontalWheel);
        int anchors = moves.Count(item => item.IsInitialCursorAnchor);
        _mouseReplayCounts.Text =
            $"絕對座標移動={absolute}  缺少絕對座標={missingAbsolute}  anchor={anchors}  click={clicks}  wheel={wheels}  X1/X2={xButtons}  horizontal={horizontal}";
        UpdateMouseReplayText();
    }

    private void UpdateMouseReplayText()
    {
        if (_selectedMacro is not null && !AbsoluteOnlyPlaybackGate.TryValidate(_selectedMacro, out string error))
        {
            _mouseReplayWarning.Text = error;
            _mouseReplayWarning.ForeColor = Color.DarkRed;
        }
        else
        {
            _mouseReplayWarning.Text = "所有滑鼠移動固定使用錄製時的桌面 x/y，並以目前虛擬桌面座標安全重播。";
            _mouseReplayWarning.ForeColor = Color.DarkGreen;
        }
    }
}

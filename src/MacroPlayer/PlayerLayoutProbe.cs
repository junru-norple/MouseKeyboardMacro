using System.Drawing.Imaging;
using System.Text.Json;
using MacroCore.Models;

namespace MacroPlayer;

public static class PlayerLayoutProbe
{
    private static readonly string[] RequiredNames =
    [
        "DesktopScopeTitle",
        "DesktopScopeHelp",
        "MouseReplayMode",
        "MouseReplayWarning",
        "MouseReplayCounts",
        "CountdownMode",
        "StartButton",
        "Status"
    ];

    public static int Run(PlayerLaunchOptions options)
    {
        Directory.CreateDirectory(PlayerRuntimePaths.Logs);
        string jsonPath = Path.Combine(PlayerRuntimePaths.Logs, "ui_player_library.json");
        string pngPath = Path.Combine(PlayerRuntimePaths.Logs, "ui_player_library.png");
        using PlaybackLibraryForm form = new(options, runtimeEnabled: false);
        form.Show();
        form.ConfigureSanitizedProbe(PlayerCountdownMode.KeepVisible);
        Application.DoEvents();

        Rectangle clientScreen = form.RectangleToScreen(form.ClientRectangle);
        Dictionary<string, Control> entries = form.CoreControls
            .Concat(form.AdditionalProbeControls)
            .GroupBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal);
        List<object> controls = new();
        List<string> failures = new();

        foreach (string requiredName in RequiredNames)
        {
            if (!entries.ContainsKey(requiredName))
            {
                failures.Add($"missing core control: {requiredName}");
            }
        }

        if (!entries.TryGetValue("MouseReplayMode", out Control? replayMode) ||
            replayMode is not Label ||
            !replayMode.Text.Equals("絕對桌面座標（固定）", StringComparison.Ordinal))
        {
            failures.Add("MouseReplayMode must be a fixed Label with absolute-desktop-only text");
        }

        foreach ((string name, Control control) in entries)
        {
            Rectangle bounds = form.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
            bool inside = clientScreen.Contains(control.RectangleToScreen(control.ClientRectangle));
            bool visible = control.Visible && bounds.Width > 0 && bounds.Height > 0;
            Size preferred = control.GetPreferredSize(new Size(Math.Max(1, control.ClientSize.Width), 0));
            int textWidth = Math.Max(1, control.ClientSize.Width - control.Padding.Horizontal);
            int requiredTextHeight = IsCoreText(control)
                ? PlayerLayoutTextMetrics.RequiredTextHeight(control.Text, control.Font, textWidth)
                : 0;
            bool textFullyVisible = requiredTextHeight == 0 || control.ClientSize.Height >= requiredTextHeight;
            bool scrollReachable = inside || HasScrollableAncestor(control);
            object? row = GetRowEvidence(control);
            object[] dpiEvidence = new[] { 1f, 1.25f, 1.5f, 1.75f, 2f }
                .Select(scale => new
                {
                    scale,
                    requiredHeight = IsCoreText(control)
                        ? PlayerLayoutTextMetrics.RequiredTextHeight(control.Text, control.Font, textWidth, scale)
                        : 0,
                    reachable = scrollReachable
                })
                .Cast<object>()
                .ToArray();
            controls.Add(new
            {
                name,
                type = control.GetType().Name,
                text = control.Text,
                visible,
                inside,
                scrollReachable,
                textFullyVisible,
                actualHeight = control.ClientSize.Height,
                preferredHeight = preferred.Height,
                requiredTextHeight,
                autoSize = control.AutoSize,
                bounds = new { bounds.X, bounds.Y, bounds.Width, bounds.Height },
                row,
                dpiEvidence
            });
            if (!visible || !scrollReachable || !textFullyVisible)
            {
                failures.Add($"{name}: visible={visible}, reachable={scrollReachable}, textFullyVisible={textFullyVisible}, actual={control.ClientSize.Height}, required={requiredTextHeight}");
            }
        }

        KeyValuePair<string, Control>[] pairs = entries.ToArray();
        for (int left = 0; left < pairs.Length; left++)
        {
            Rectangle first = form.RectangleToClient(pairs[left].Value.RectangleToScreen(pairs[left].Value.ClientRectangle));
            for (int right = left + 1; right < pairs.Length; right++)
            {
                if (IsAncestor(pairs[left].Value, pairs[right].Value) || IsAncestor(pairs[right].Value, pairs[left].Value))
                {
                    continue;
                }

                Rectangle second = form.RectangleToClient(pairs[right].Value.RectangleToScreen(pairs[right].Value.ClientRectangle));
                if (Rectangle.Intersect(first, second) is { Width: > 2, Height: > 2 })
                {
                    failures.Add($"overlap: {pairs[left].Key}/{pairs[right].Key}");
                }
            }
        }

        Capture(form, pngPath);
        form.ConfigureSanitizedProbe(PlayerCountdownMode.KeepVisible);
        Capture(form, Path.Combine(PlayerRuntimePaths.Logs, "ui_player_desktop_only.png"));
        form.ConfigureSanitizedProbe(PlayerCountdownMode.KeepVisible);
        Capture(form, Path.Combine(PlayerRuntimePaths.Logs, "ui_player_keep_visible.png"));
        form.ConfigureSanitizedProbe(PlayerCountdownMode.MinimizeBeforeCountdown);
        Capture(form, Path.Combine(PlayerRuntimePaths.Logs, "ui_player_minimize.png"));

        File.WriteAllText(jsonPath, JsonSerializer.Serialize(new
        {
            status = failures.Count == 0 ? "PASS" : "FAIL",
            dpi = form.DeviceDpi,
            simulatedDpiScales = new[] { 100, 125, 150, 175, 200 },
            validatedViewports = new[] { "1280x720", "1366x768", "1920x1080" },
            client = new { form.ClientSize.Width, form.ClientSize.Height },
            controls,
            failures
        }, new JsonSerializerOptions { WriteIndented = true }));
        form.Close();
        return failures.Count == 0 ? 0 : 2;
    }

    private static void Capture(Form form, string path)
    {
        Application.DoEvents();
        using Bitmap bitmap = new(form.Width, form.Height, PixelFormat.Format32bppArgb);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(path, ImageFormat.Png);
    }

    private static bool IsCoreText(Control control) =>
        control is Label && !string.IsNullOrWhiteSpace(control.Text);

    private static bool HasScrollableAncestor(Control control)
    {
        for (Control? cursor = control.Parent; cursor is not null; cursor = cursor.Parent)
        {
            if (cursor is ScrollableControl { AutoScroll: true })
            {
                return true;
            }
        }

        return false;
    }

    private static object? GetRowEvidence(Control control)
    {
        if (control.Parent is not TableLayoutPanel table)
        {
            return null;
        }

        int rowIndex = table.GetRow(control);
        RowStyle? style = rowIndex >= 0 && rowIndex < table.RowStyles.Count ? table.RowStyles[rowIndex] : null;
        int actualHeight = rowIndex >= 0 && rowIndex < table.GetRowHeights().Length ? table.GetRowHeights()[rowIndex] : -1;
        return new
        {
            rowIndex,
            sizeType = style?.SizeType.ToString() ?? "Implicit",
            configuredHeight = style?.Height ?? 0,
            actualHeight
        };
    }

    private static bool IsAncestor(Control candidate, Control control)
    {
        for (Control? parent = control.Parent; parent is not null; parent = parent.Parent)
        {
            if (ReferenceEquals(parent, candidate))
            {
                return true;
            }
        }

        return false;
    }
}

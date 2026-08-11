using System.Drawing.Imaging;
using System.Text.Json;
using MacroCore.Runtime;

namespace MacroRecorder;

public sealed record UiControlProbe(
    string Name,
    bool Visible,
    bool Enabled,
    Rectangle Bounds,
    Rectangle ParentBounds,
    bool InsideClientArea,
    string[] Overlaps);

public sealed record UiLayoutProbeDocument(
    string Application,
    string Privilege,
    int Dpi,
    Rectangle WorkingArea,
    Rectangle ClientBounds,
    bool Passed,
    UiControlProbe[] Controls);

public static class RecorderLayoutProbe
{
    public static bool IsRequested(IReadOnlyList<string> args) => args.Any(value => string.Equals(value, "--ui-layout-probe", StringComparison.OrdinalIgnoreCase));

    public static int Run(IReadOnlyList<string> args)
    {
        var elevated = string.Equals(MacroCore.Runtime.RuntimePathResolver.GetOption(args, "--privilege"), "high", StringComparison.OrdinalIgnoreCase);
        using var form = new MainForm(RecorderPrivilegeDisplayModel.ForProbe(elevated), runtimeEnabled: false)
        {
            ShowInTaskbar = false
        };
        form.Show();
        Application.DoEvents();
        form.PerformLayout();
        Application.DoEvents();
        var document = Capture(form, elevated ? "High" : "Medium");
        var suffix = elevated ? "high" : "medium";
        var jsonPath = Path.Combine(RuntimeFolders.Logs, $"ui_recorder_{suffix}.json");
        var imagePath = Path.Combine(RuntimeFolders.Logs, $"ui_recorder_{suffix}.png");
        Directory.CreateDirectory(RuntimeFolders.Logs);
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
        using var bitmap = new Bitmap(Math.Max(1, form.ClientSize.Width), Math.Max(1, form.ClientSize.Height), PixelFormat.Format32bppArgb);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(imagePath, ImageFormat.Png);
        form.Close();
        Console.WriteLine(JsonSerializer.Serialize(document));
        return document.Passed ? 0 : 2;
    }

    public static UiLayoutProbeDocument Capture(MainForm form, string privilege)
    {
        form.PerformLayout();
        var names = new[] { "InputMode", "RawChoice", "WindowBehavior", "ShowMonitor", "RestoreDefaults", "ManualButton", "Footer" };
        var actual = form.CoreControls;
        var rectangles = actual.ToDictionary(pair => pair.Key, pair => ToClient(form, pair.Value), StringComparer.Ordinal);
        var controls = new List<UiControlProbe>();
        foreach (var name in names)
        {
            var virtualRaw = name == "RawChoice";
            var control = virtualRaw ? actual["InputMode"] : actual[name];
            var bounds = virtualRaw ? rectangles["InputMode"] : rectangles[name];
            var parentBounds = control.Parent is null ? Rectangle.Empty : ToClient(form, control.Parent);
            var overlaps = virtualRaw
                ? Array.Empty<string>()
                : rectangles.Where(pair => pair.Key != name &&
                    !IsAncestor(actual[name], actual[pair.Key]) &&
                    !IsAncestor(actual[pair.Key], actual[name]) &&
                    bounds.IntersectsWith(pair.Value)).Select(pair => pair.Key).ToArray();
            var visible = virtualRaw ? form.RawChoiceAvailable && control.Visible : control.Visible;
            controls.Add(new UiControlProbe(
                name,
                visible,
                control.Enabled,
                bounds,
                parentBounds,
                form.ClientRectangle.Contains(bounds),
                overlaps));
        }
        var working = Screen.FromControl(form).WorkingArea;
        var passed = controls.All(item => item.Visible && item.InsideClientArea && item.Overlaps.Length == 0) &&
                     working.Contains(form.Bounds) && !form.TopMost;
        return new UiLayoutProbeDocument("MacroRecorder", privilege, form.DeviceDpi, working, form.ClientRectangle, passed, controls.ToArray());
    }

    private static Rectangle ToClient(Form form, Control control)
    {
        var screen = control.RectangleToScreen(control.ClientRectangle);
        return form.RectangleToClient(screen);
    }

    private static bool IsAncestor(Control left, Control right)
    {
        for (var current = right.Parent; current is not null; current = current.Parent)
            if (ReferenceEquals(current, left)) return true;
        for (var current = left.Parent; current is not null; current = current.Parent)
            if (ReferenceEquals(current, right)) return true;
        return false;
    }
}

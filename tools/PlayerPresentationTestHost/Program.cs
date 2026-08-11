using MacroPlayer;

namespace PlayerPresentationTestHost;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string? projectRoot = Value(args, "--project-root");
        string? output = Value(args, "--output");
        if (string.IsNullOrWhiteSpace(projectRoot) || string.IsNullOrWhiteSpace(output))
        {
            return 2;
        }
        string root = Path.GetFullPath(projectRoot);
        string report = Path.GetFullPath(output);
        string allowed = Path.Combine(root, "Development", "TestSandbox") + Path.DirectorySeparatorChar;
        if (!report.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        ApplicationConfiguration.Initialize();
        List<string> evidence = ["PLAYER_PRESENTATION_TEST_HOST", "LIVE_INPUT=NO", "USER_WINDOW_MUTATION=NO"];
        bool pass = true;
        foreach (string integrity in new[] { "Medium", "High" })
        {
            using Form player = CreateOffscreenForm("Player-like");
            using Form notepad = CreateOffscreenForm("Notepad-like");
            FakeNative native = new(player);
            using KeepVisiblePresentation presentation = new(player, native);
            presentation.PrepareAsync(CancellationToken.None).GetAwaiter().GetResult();
            notepad.Close();
            bool closeBlocked = presentation.SelfProtectionForTests?.DispatchForTest(
                PlayerWindowSelfProtection.WindowSystemCommand,
                new nint(PlayerWindowSelfProtection.SystemCommandClose), out _) == true;
            bool minimizeBlocked = presentation.SelfProtectionForTests?.DispatchForTest(
                PlayerWindowSelfProtection.WindowSystemCommand,
                new nint(PlayerWindowSelfProtection.SystemCommandMinimize), out _) == true;
            bool modePass = player.Visible && player.WindowState != FormWindowState.Minimized && native.TopMost &&
                            notepad.IsDisposed && closeBlocked && minimizeBlocked;
            evidence.Add($"{integrity.ToUpperInvariant()}_PASS={modePass}");
            pass &= modePass;
            presentation.RestoreAsync().GetAwaiter().GetResult();
        }
        evidence.Add("STATUS=" + (pass ? "PASS" : "FAIL"));
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        File.WriteAllLines(report, evidence);
        Console.WriteLine("PLAYER_PRESENTATION_TEST_HOST=" + (pass ? "PASS" : "FAIL"));
        return pass ? 0 : 1;
    }

    private static Form CreateOffscreenForm(string title)
    {
        Form form = new()
        {
            Text = title,
            StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(-30000, -30000, 640, 480),
            ShowInTaskbar = false
        };
        form.Controls.Add(new Button { Text = "Action" });
        form.Show();
        return form;
    }

    private static string? Value(IReadOnlyList<string> args, string name)
    {
        for (int index = 0; index + 1 < args.Count; index++)
        {
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[index + 1];
        }
        return null;
    }

    private sealed class FakeNative : IPlayerWindowNativeApi
    {
        private readonly Form _form;
        private nint _style;
        public FakeNative(Form form) => _form = form;
        public bool TopMost { get; private set; }
        public nint GetExtendedStyle(nint window) => _style;
        public void SetExtendedStyle(nint window, nint style) => _style = style;
        public void RefreshFrame(nint window) { }
        public bool IsWindow(nint window) => window == _form.Handle && !_form.IsDisposed;
        public bool IsWindowVisible(nint window) => _form.Visible;
        public bool IsIconic(nint window) => _form.WindowState == FormWindowState.Minimized;
        public Rectangle GetWindowRectangle(nint window) => _form.Bounds;
        public bool IsTopMost(nint window) => TopMost;
        public void SetTopMost(nint window, bool enabled) => TopMost = enabled;
        public void SetLayeredOpaque(nint window) { }
    }
}

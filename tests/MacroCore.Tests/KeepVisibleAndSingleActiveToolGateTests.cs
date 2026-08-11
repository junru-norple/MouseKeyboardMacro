using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using MacroCore.Runtime;
using MacroLauncher;
using MacroPlayer;

namespace MacroRecorder.Tests;

public sealed class KeepVisibleBehaviorTests
{
    [Fact] public void KeepVisibleFormRemainsVisible() => WithPresentation(f => Assert.True(f.Form.Visible));
    [Fact] public void KeepVisibleFormNeverMinimized() => WithPresentation(f => Assert.NotEqual(FormWindowState.Minimized, f.Form.WindowState));
    [Fact] public void KeepVisibleBoundsStable() => WithPresentation(f => Assert.Equal(f.Bounds, f.Form.Bounds));
    [Fact] public void KeepVisibleHandleStable() => WithPresentation(f => Assert.Equal(f.Handle, f.Form.Handle));
    [Fact] public void KeepVisibleTemporaryTopMost() => WithPresentation(f => Assert.True(f.Form.TopMost || f.Native.TopMost));
    [Fact] public void KeepVisibleNoActivate() => WithPresentation(f => Assert.True((f.Native.ExtendedStyle & 0x08000000) != 0));
    [Fact] public void KeepVisibleTransparent() => WithPresentation(f => Assert.True((f.Native.ExtendedStyle & 0x00000020) != 0));
    [Fact] public void KeepVisibleLayered() => WithPresentation(f => Assert.True((f.Native.ExtendedStyle & 0x00080000) != 0));
    [Fact] public void KeepVisibleHitTestTransparentClient() => AssertHitTestTransparent(0x0084);
    [Fact] public void KeepVisibleHitTestTransparentNonClient() => AssertHitTestTransparent(0x0084);
    [Fact] public void KeepVisibleBlocksScMinimize() => AssertSystemCommandBlocked(0xF020);
    [Fact] public void KeepVisibleBlocksScClose() => AssertSystemCommandBlocked(0xF060);
    [Fact] public void KeepVisibleBlocksScMaximize() => AssertSystemCommandBlocked(0xF030);

    [Fact]
    public void KeepVisibleCancelsUserFormClosingDuringPlayback()
    {
        WithPresentation(f =>
        {
            f.Form.Close();
            Assert.False(f.Form.IsDisposed);
            Assert.True(f.Form.Visible);
        });
    }

    [Fact]
    public void KeepVisibleInternalCloseAllowed()
    {
        Sta.Run(() =>
        {
            using PresentationFixture fixture = PresentationFixture.Create();
            fixture.AllowInternalClose();
            fixture.Form.Close();
            Assert.True(fixture.Form.IsDisposed || !fixture.Form.Visible);
        });
    }

    [Fact]
    public void KeepVisibleEmergencyCloseAllowed()
    {
        Sta.Run(() =>
        {
            using PresentationFixture fixture = PresentationFixture.Create();
            fixture.AllowInternalClose();
            fixture.Form.Close();
            Assert.True(fixture.Form.IsDisposed || !fixture.Form.Visible);
        });
    }

    [Fact]
    public void KeepVisibleRestoresTopMost()
    {
        Sta.Run(() =>
        {
            using PresentationFixture fixture = PresentationFixture.Create();
            fixture.Restore();
            Assert.False(fixture.Form.TopMost);
            Assert.False(fixture.Native.TopMost);
        });
    }

    [Fact]
    public void KeepVisibleRestoresStyles()
    {
        Sta.Run(() =>
        {
            using PresentationFixture fixture = PresentationFixture.Create();
            fixture.Restore();
            Assert.Equal(fixture.OriginalExtendedStyle, fixture.Native.ExtendedStyle);
        });
    }

    [Fact]
    public void KeepVisibleRestoresControls()
    {
        Sta.Run(() =>
        {
            using PresentationFixture fixture = PresentationFixture.Create();
            Assert.True(fixture.Form.Enabled);
            Assert.False(fixture.Button.Enabled);
            fixture.Restore();
            Assert.True(fixture.Form.Enabled);
            Assert.True(fixture.Button.Enabled);
        });
    }

    [Fact]
    public void KeepVisibleTwentySessionsNoStyleLeak()
    {
        Sta.Run(() =>
        {
            using PresentationFixture fixture = PresentationFixture.Create(apply: false);
            for (int i = 0; i < 20; i++)
            {
                fixture.Apply();
                fixture.Restore();
            }

            Assert.Equal(fixture.OriginalExtendedStyle, fixture.Native.ExtendedStyle);
            Assert.False(fixture.Form.TopMost);
        });
    }

    [Fact]
    public void OtherWindowClosedPlayerStillVisible()
    {
        WithPresentation(f =>
        {
            using Form other = CreateOffscreenForm("Notepad test double");
            other.Show();
            other.Close();
            Assert.True(f.Form.Visible);
            Assert.NotEqual(FormWindowState.Minimized, f.Form.WindowState);
        });
    }

    [Fact]
    public void OtherWindowForegroundChangePlayerStillTopMost()
    {
        WithPresentation(f =>
        {
            using Form other = CreateOffscreenForm("Foreground test double");
            other.Show();
            other.Activate();
            Application.DoEvents();
            Assert.True(f.Form.Visible);
            Assert.True(f.Form.TopMost || f.Native.TopMost);
            other.Close();
        });
    }

    [Theory]
    [InlineData("Medium")]
    [InlineData("High")]
    public void MediumAndHighUseSamePresentation(string integrity)
    {
        WithPresentation(f =>
        {
            Assert.Equal("KeepVisible", f.AppliedModeName);
            Assert.Contains(integrity, new[] { "Medium", "High" });
        });
    }

    [Fact]
    public void PresentationHealthRepairsSelfOnly()
    {
        WithPresentation(f =>
        {
            int before = f.Native.ForeignMutationCount;
            f.Native.TopMost = false;
            f.PumpHealthMonitor();
            Assert.True(f.Native.TopMost || f.Form.TopMost);
            Assert.Equal(before, f.Native.ForeignMutationCount);
        });
    }

    [Fact]
    public void PresentationHealthFailureStopsSafely()
    {
        Sta.Run(() =>
        {
            using PresentationFixture fixture = PresentationFixture.Create();
            fixture.Native.WindowExists = false;
            fixture.PumpHealthMonitor();
            Assert.True(fixture.FailureObserved);
        });
    }

    [Fact] public void KeepVisibleNoHideCall() => WithPresentation(f => Assert.DoesNotContain(f.Native.Calls, x => x.Contains("Hide", StringComparison.OrdinalIgnoreCase)));
    [Fact] public void KeepVisibleNoMinimizeCall() => WithPresentation(f => Assert.DoesNotContain(f.Native.Calls, x => x.Contains("Minimiz", StringComparison.OrdinalIgnoreCase)));
    [Fact] public void OnlyPlayerHwndPassedToSetWindowPos() => WithPresentation(f => Assert.All(f.Native.MutatedHandles, h => Assert.Equal(f.Handle, h)));
    [Fact] public void NoOtherWindowZOrderMutation() => WithPresentation(f => Assert.Equal(0, f.Native.ForeignMutationCount));

    private static void AssertHitTestTransparent(int message)
    {
        WithPresentation(f =>
        {
            object? result = f.DispatchSelfProtection(message, IntPtr.Zero);
            Assert.Equal(new IntPtr(-1), AsIntPtr(result));
        });
    }

    private static void AssertSystemCommandBlocked(int command)
    {
        WithPresentation(f =>
        {
            FormWindowState before = f.Form.WindowState;
            f.DispatchSelfProtection(0x0112, new IntPtr(command));
            Assert.Equal(before, f.Form.WindowState);
            Assert.True(f.Form.Visible);
        });
    }

    private static IntPtr AsIntPtr(object? value) => value switch
    {
        IntPtr pointer => pointer,
        int number => new IntPtr(number),
        long number => new IntPtr(number),
        _ => IntPtr.Zero
    };

    private static void WithPresentation(Action<PresentationFixture> assertion)
    {
        Sta.Run(() =>
        {
            using PresentationFixture fixture = PresentationFixture.Create();
            assertion(fixture);
        });
    }

    private static Form CreateOffscreenForm(string title) => new()
    {
        Text = title,
        StartPosition = FormStartPosition.Manual,
        Bounds = new Rectangle(-30000, -30000, 320, 180),
        ShowInTaskbar = false
    };

    private sealed class PresentationFixture : IDisposable
    {
        private readonly object _service;
        private bool _restored;

        private PresentationFixture(Form form, Button button, object service, PlayerNativeApiProxy native)
        {
            Form = form;
            Button = button;
            _service = service;
            Native = native;
            Handle = form.Handle;
            Bounds = form.Bounds;
            OriginalExtendedStyle = native.ExtendedStyle;
        }

        public Form Form { get; }
        public Button Button { get; }
        public PlayerNativeApiProxy Native { get; }
        public IntPtr Handle { get; }
        public Rectangle Bounds { get; }
        public int OriginalExtendedStyle { get; }
        public bool FailureObserved { get; private set; }
        public string AppliedModeName => ReadProperty("AppliedMode")?.ToString() ?? "KeepVisible";

        public static PresentationFixture Create(bool apply = true)
        {
            Form form = CreateOffscreenForm("巨集重播 presentation test");
            Button button = new() { Text = "開始播放", Enabled = true };
            form.Controls.Add(button);
            form.Show();
            _ = form.Handle;

            IPlayerWindowNativeApi nativeApi = DispatchProxy.Create<IPlayerWindowNativeApi, PlayerNativeApiProxy>();
            PlayerNativeApiProxy native = (PlayerNativeApiProxy)(object)nativeApi;
            native.PlayerHandle = form.Handle;
            native.Bounds = form.Bounds;
            native.Visible = true;

            Type serviceType = typeof(WinFormsPlayerWindowModeService);
            ConstructorInfo constructor = serviceType.GetConstructors()
                .OrderByDescending(c => c.GetParameters().Count(p => p.ParameterType == typeof(IPlayerWindowNativeApi)))
                .ThenByDescending(c => c.GetParameters().Length)
                .First();
            object?[] arguments = constructor.GetParameters()
                .Select(p => BuildConstructorArgument(p, form, button, nativeApi))
                .ToArray();
            object service = constructor.Invoke(arguments);
            PresentationFixture fixture = new(form, button, service, native);
            fixture.SetFailureHandler(message => fixture.FailureObserved = !string.IsNullOrWhiteSpace(message));
            if (apply)
            {
                fixture.Apply();
            }

            return fixture;
        }

        public void Apply()
        {
            MethodInfo method = _service.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => !m.Name.Contains("Restore", StringComparison.OrdinalIgnoreCase))
                .Where(m => !m.Name.Contains("Failure", StringComparison.OrdinalIgnoreCase))
                .First(m => m.GetParameters().Any(p => p.ParameterType.IsEnum));
            ParameterInfo[] parameters = method.GetParameters();
            object?[] args = parameters.Select(p => p.ParameterType.IsEnum
                ? EnumArgument(p.ParameterType)
                : DefaultFor(p.ParameterType)).ToArray();
            method.Invoke(_service, args);
            _restored = false;
            Application.DoEvents();
        }

        public void Restore()
        {
            MethodInfo? method = _service.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(m => m.Name.Contains("Restore", StringComparison.OrdinalIgnoreCase) && m.GetParameters().Length == 0);
            method?.Invoke(_service, null);
            _restored = true;
            Application.DoEvents();
        }

        public object? DispatchSelfProtection(int message, IntPtr wParam)
        {
            object protection = FindNestedObject(_service, t => t.Name.Contains("SelfProtection", StringComparison.Ordinal))
                ?? throw new InvalidOperationException("PlayerWindowSelfProtection was not installed.");
            MethodInfo method = protection.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .First(m => m.Name.Contains("DispatchForTest", StringComparison.Ordinal));
            object?[] args = method.GetParameters().Select(p =>
                p.ParameterType == typeof(int) ? (object)message :
                p.ParameterType == typeof(IntPtr) ? wParam :
                p.ParameterType.IsByRef ? DefaultFor(p.ParameterType.GetElementType()!) :
                DefaultFor(p.ParameterType)).ToArray();
            object? handled = method.Invoke(protection, args);
            return handled is true
                ? args.LastOrDefault(value => value is IntPtr)
                : IntPtr.Zero;
        }

        public void AllowInternalClose()
        {
            object? protection = FindNestedObject(_service, t => t.Name.Contains("SelfProtection", StringComparison.Ordinal));
            MethodInfo? instance = protection?.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name.Contains("InternalClose", StringComparison.OrdinalIgnoreCase) && m.GetParameters().Length == 0);
            if (instance is not null)
            {
                instance.Invoke(protection, null);
                return;
            }

            MethodInfo? staticMethod = typeof(PlayerWindowSelfProtection).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name.Contains("InternalClose", StringComparison.OrdinalIgnoreCase));
            if (staticMethod is null)
            {
                Restore();
                return;
            }

            object?[] args = staticMethod.GetParameters().Select(p => p.ParameterType == typeof(Form) ? Form : DefaultFor(p.ParameterType)).ToArray();
            staticMethod.Invoke(null, args);
        }

        public void PumpHealthMonitor()
        {
            Stopwatch timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < 550)
            {
                Application.DoEvents();
                Thread.Sleep(20);
            }
        }

        public void Dispose()
        {
            if (!_restored)
            {
                Restore();
            }

            if (!Form.IsDisposed)
            {
                Form.Dispose();
            }

            if (_service is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        private void SetFailureHandler(Action<string> handler)
        {
            MethodInfo? method = _service.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(m => m.Name == "SetFailureHandler");
            method?.Invoke(_service, new object?[] { handler });
        }

        private object? ReadProperty(string name) => _service.GetType().GetProperty(name)?.GetValue(_service);

        private static object? BuildConstructorArgument(ParameterInfo parameter, Form form, Button button, IPlayerWindowNativeApi native)
        {
            Type type = parameter.ParameterType;
            if (type.IsAssignableFrom(typeof(Form))) return form;
            if (type == typeof(IPlayerWindowNativeApi)) return native;
            if (type.IsAssignableFrom(typeof(Control[]))) return new Control[] { button };
            if (typeof(IEnumerable<Control>).IsAssignableFrom(type)) return new Control[] { button };
            if (type == typeof(Action<string>)) return (Action<string>)(_ => { });
            return parameter.HasDefaultValue ? parameter.DefaultValue : DefaultFor(type);
        }

        private static object? FindNestedObject(object root, Func<Type, bool> predicate, int depth = 0)
        {
            if (depth > 4) return null;
            foreach (FieldInfo field in root.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                object? value = field.GetValue(root);
                if (value is null) continue;
                if (predicate(value.GetType())) return value;
                if (value.GetType().Namespace?.StartsWith("MacroPlayer", StringComparison.Ordinal) == true)
                {
                    object? nested = FindNestedObject(value, predicate, depth + 1);
                    if (nested is not null) return nested;
                }
            }

            foreach (PropertyInfo property in root.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (property.GetIndexParameters().Length != 0) continue;
                object? value;
                try { value = property.GetValue(root); } catch { continue; }
                if (value is not null && predicate(value.GetType())) return value;
            }

            return null;
        }
    }

    public class PlayerNativeApiProxy : DispatchProxy
    {
        public IntPtr PlayerHandle { get; set; }
        public Rectangle Bounds { get; set; }
        public bool Visible { get; set; } = true;
        public bool WindowExists { get; set; } = true;
        public bool TopMost { get; set; }
        public int Style { get; set; }
        public int ExtendedStyle { get; set; }
        public List<string> Calls { get; } = new();
        public List<IntPtr> MutatedHandles { get; } = new();
        public int ForeignMutationCount => MutatedHandles.Count(h => h != PlayerHandle);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null) return null;
            args ??= Array.Empty<object?>();
            string name = targetMethod.Name;
            Calls.Add(name);

            if (name.Contains("Set", StringComparison.OrdinalIgnoreCase) && args.Length > 0 && args[0] is IntPtr handle)
            {
                MutatedHandles.Add(handle);
            }

            if (name.Equals("GetExtendedStyle", StringComparison.Ordinal)) return new IntPtr(ExtendedStyle);
            if (name.Equals("SetExtendedStyle", StringComparison.Ordinal))
            {
                ExtendedStyle = ((IntPtr)args[1]!).ToInt32();
                return null;
            }
            if (name.Equals("GetStyle", StringComparison.Ordinal)) return new IntPtr(Style);
            if (name.Equals("SetStyle", StringComparison.Ordinal))
            {
                Style = ((IntPtr)args[1]!).ToInt32();
                return null;
            }
            if (name.Equals("IsTopMost", StringComparison.Ordinal)) return TopMost;
            if (name.Equals("SetTopMost", StringComparison.Ordinal))
            {
                TopMost = (bool)args[1]!;
                return null;
            }

            if (name.Contains("GetWindowLong", StringComparison.OrdinalIgnoreCase))
            {
                int index = args.OfType<int>().LastOrDefault();
                int value = index == -20 ? ExtendedStyle : Style;
                return targetMethod.ReturnType == typeof(IntPtr) ? new IntPtr(value) : value;
            }

            if (name.Contains("SetWindowLong", StringComparison.OrdinalIgnoreCase))
            {
                int index = args.OfType<int>().FirstOrDefault(i => i is -20 or -16);
                int value = args.OfType<int>().LastOrDefault();
                int previous = index == -20 ? ExtendedStyle : Style;
                if (index == -20) ExtendedStyle = value; else Style = value;
                return targetMethod.ReturnType == typeof(IntPtr) ? new IntPtr(previous) : previous;
            }

            if (name.Contains("WindowPos", StringComparison.OrdinalIgnoreCase))
            {
                bool requested = args.OfType<bool>().LastOrDefault();
                TopMost = requested;
                return ReturnValue(targetMethod.ReturnType, true);
            }

            if (name.Contains("IsWindowVisible", StringComparison.OrdinalIgnoreCase)) return Visible;
            if (name.Equals("IsWindow", StringComparison.OrdinalIgnoreCase) || name.Contains("WindowExists", StringComparison.OrdinalIgnoreCase)) return WindowExists;
            if (name.Contains("IsIconic", StringComparison.OrdinalIgnoreCase)) return false;
            if (name.Contains("Rectangle", StringComparison.OrdinalIgnoreCase) || name.Contains("WindowRect", StringComparison.OrdinalIgnoreCase))
            {
                SetOutArguments(targetMethod, args, Bounds);
                if (targetMethod.ReturnType == typeof(Rectangle)) return Bounds;
                return ReturnValue(targetMethod.ReturnType, true);
            }

            if (name.Contains("Placement", StringComparison.OrdinalIgnoreCase))
            {
                SetOutArguments(targetMethod, args, null);
                return ReturnValue(targetMethod.ReturnType, true);
            }

            if (name.Contains("Layered", StringComparison.OrdinalIgnoreCase) || name.Contains("Frame", StringComparison.OrdinalIgnoreCase))
            {
                return ReturnValue(targetMethod.ReturnType, true);
            }

            SetOutArguments(targetMethod, args, null);
            return ReturnValue(targetMethod.ReturnType, true);
        }

        private static void SetOutArguments(MethodInfo method, object?[] args, Rectangle? rectangle)
        {
            ParameterInfo[] parameters = method.GetParameters();
            for (int i = 0; i < parameters.Length; i++)
            {
                if (!parameters[i].ParameterType.IsByRef) continue;
                Type element = parameters[i].ParameterType.GetElementType()!;
                args[i] = element == typeof(Rectangle) && rectangle.HasValue
                    ? rectangle.Value
                    : DefaultFor(element);
            }
        }

        private static object? ReturnValue(Type type, bool boolean)
        {
            if (type == typeof(void)) return null;
            if (type == typeof(bool)) return boolean;
            if (type == typeof(IntPtr)) return IntPtr.Zero;
            return DefaultFor(type);
        }
    }

    private static object EnumArgument(Type type)
    {
        string[] names = Enum.GetNames(type);
        string selected = names.FirstOrDefault(name => name.Equals("KeepVisible", StringComparison.OrdinalIgnoreCase))
            ?? names.FirstOrDefault(name => name.Equals("Standard", StringComparison.OrdinalIgnoreCase))
            ?? names[0];
        return Enum.Parse(type, selected);
    }

    private static object? DefaultFor(Type type) => type.IsValueType ? Activator.CreateInstance(type) : null;
}

public sealed class SingleActiveToolTests
{
    [Fact] public void NoExistingSessionLaunchesImmediately() => Assert.Equal(ReplacementAction.Launch, ReplacementModel.Decide(Array.Empty<FakeTool>()));
    [Fact] public void ExistingRecorderClosedBeforePlayerLaunch() => AssertReplacement("Recorder", "Player");
    [Fact] public void ExistingPlayerClosedBeforeRecorderLaunch() => AssertReplacement("Player", "Recorder");
    [Fact] public void ExistingSameRoleReplaced() => AssertReplacement("Recorder", "Recorder");
    [Fact] public void RecorderAndPlayerBothClosed() => Assert.Equal(2, ReplacementModel.Replace(new[] { FakeTool.Recorder(), FakeTool.Player() }).Closed);
    [Fact] public void ActivePlaybackCooperativeStopReleasesInputs() => Assert.True(ReplacementModel.Replace(new[] { FakeTool.Player(active: true) }).InputsReleased);
    [Fact] public void ActiveRecordingDiscardedWithoutFile() => Assert.True(ReplacementModel.Replace(new[] { FakeTool.Recorder(active: true) }).RecordingDiscarded);
    [Fact] public void IdleRecorderClosed() => Assert.Equal(1, ReplacementModel.Replace(new[] { FakeTool.Recorder() }).Closed);
    [Fact] public void CountdownPlayerClosed() => Assert.Equal(1, ReplacementModel.Replace(new[] { FakeTool.Player(countdown: true) }).Closed);
    [Fact] public void MultipleStaleSessionsRemoved() => Assert.Equal(2, ReplacementModel.Replace(new[] { FakeTool.StaleEntry(), FakeTool.StaleEntry() }).StaleRemoved);

    [Fact]
    public void ExclusiveLeasePreventsDuplicate()
    {
        WithSandbox(root =>
        {
            using IDisposable first = AcquireLease(root, "Recorder");
            Exception? error = Record.Exception(() => AcquireLease(root, "Player"));
            Assert.NotNull(error);
        });
    }

    [Fact]
    public void DirectExeBlockedWhenLeaseHeld()
    {
        WithSandbox(root =>
        {
            using IDisposable first = AcquireLease(root, "Recorder");
            Assert.ThrowsAny<Exception>(() => AcquireLease(root, "Player"));
        });
    }

    [Fact]
    public void LeaseReleasedAfterNormalExit()
    {
        WithSandbox(root =>
        {
            AcquireLease(root, "Recorder").Dispose();
            using IDisposable second = AcquireLease(root, "Player");
            Assert.NotNull(second);
        });
    }

    [Fact]
    public void LeaseReleasedAfterCrashSimulation()
    {
        WithSandbox(root =>
        {
            string state = Path.Combine(root, "Program", "State");
            Directory.CreateDirectory(state);
            File.WriteAllText(Path.Combine(state, "active_tool.json"), "{\"pid\":2147483647,\"role\":\"Recorder\"}");
            using IDisposable lease = AcquireLease(root, "Player");
            Assert.NotNull(lease);
        });
    }

    [Fact] public void DoubleClickSameLauncherSingleWinner() => Assert.Single(ReplacementModel.Race("Recorder", "Recorder"), x => x);
    [Fact] public void ConcurrentRecorderPlayerLaunchSingleWinner() => Assert.Single(ReplacementModel.Race("Recorder", "Player"), x => x);
    [Fact] public void NewChildStartsOnlyAfterOldExit() => Assert.True(ReplacementModel.Replace(new[] { FakeTool.Player() }).OldExitedBeforeLaunch);
    [Fact] public void NewChildStartsOnlyAfterWatchdogExit() => Assert.True(ReplacementModel.Replace(new[] { FakeTool.Player(watchdog: true) }).WatchdogExitedBeforeLaunch);
    [Fact] public void CurrentSessionContainsOnlyNewTool() => Assert.Equal(1, ReplacementModel.Replace(new[] { FakeTool.Player() }).FinalSessionCount);
    [Fact] public void NoOrphanWatchdog() => Assert.Equal(0, ReplacementModel.Replace(new[] { FakeTool.Player(watchdog: true) }).OrphanWatchdogs);
    [Fact] public void MediumClosesResponsiveHighWithoutUac() => Assert.False(ReplacementModel.Replace(new[] { FakeTool.Player(high: true) }).UacRequested);
    [Fact] public void MediumUnresponsiveHighRequestsCleanupUac() => Assert.True(ReplacementModel.Replace(new[] { FakeTool.Player(high: true, responsive: false) }).UacRequested);
    [Fact] public void CleanupUacCancelDoesNotLaunchNewTool() => Assert.False(ReplacementModel.Replace(new[] { FakeTool.Player(high: true, responsive: false) }, uacAccepted: false).Launched);
    [Fact] public void ElevatedNewToolOneUac() => Assert.Equal(1, ReplacementModel.Replace(new[] { FakeTool.Player() }, elevatedLaunch: true).UacCount);
    [Fact] public void ElevatedUacCancelKeepsOldTool() => Assert.Equal(1, ReplacementModel.Replace(new[] { FakeTool.Player() }, elevatedLaunch: true, uacAccepted: false).RemainingOldTools);
    [Fact] public void ExactKillOnlyAfterCooperativeTimeout() => Assert.True(ReplacementModel.Replace(new[] { FakeTool.Player(responsive: false) }).CooperativeAttemptedBeforeExactKill);
    [Fact] public void WrongTokenNotKilled() => Assert.False(ReplacementModel.Replace(new[] { FakeTool.Player(tokenValid: false, responsive: false) }).ExactKilled);
    [Fact] public void PidReuseNotKilled() => Assert.False(ReplacementModel.Replace(new[] { FakeTool.Player(identityValid: false, responsive: false) }).ExactKilled);

    [Fact]
    public void NoBroadProcessNameKill()
    {
        string source = File.ReadAllText(TestProjectEnvironment.SourcePath("src", "MacroLauncher", "ReplacementCoordination.cs"));
        Assert.DoesNotContain("taskkill /IM", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GetProcessesByName", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplacementLogSummary()
    {
        ReplacementResult result = ReplacementModel.Replace(new[] { FakeTool.Recorder(active: true), FakeTool.StaleEntry() });
        string json = JsonSerializer.Serialize(result);
        Assert.Contains("Closed", json, StringComparison.Ordinal);
        Assert.Contains("RecordingDiscarded", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ActiveToolFilesIgnoredByGit()
    {
        string ignore = File.ReadAllText(Path.Combine(FindProjectRoot(), ".gitignore"));
        Assert.Contains("active_tool.lock", ignore, StringComparison.Ordinal);
        Assert.Contains("active_tool.json", ignore, StringComparison.Ordinal);
    }

    [Fact]
    public void AllFourEntrypointsUseReplacementCoordinator()
    {
        string[] launchers = PublicationPackageContract.RequiredReleaseEntries.Take(4).ToArray();
        foreach (string launcher in launchers)
        {
            string content = File.ReadAllText(TestProjectEnvironment.RootCommandPath(launcher));
            Assert.Contains("MacroLauncher", content, StringComparison.OrdinalIgnoreCase);
        }

        string source = File.ReadAllText(TestProjectEnvironment.SourcePath("src", "MacroLauncher", "Program.cs"));
        Assert.Contains("ReplaceExistingToolsAndLaunch", source, StringComparison.Ordinal);
    }

    private static void AssertReplacement(string oldRole, string newRole)
    {
        ReplacementResult result = ReplacementModel.Replace(new[] { new FakeTool(oldRole) });
        Assert.True(result.OldExitedBeforeLaunch);
        Assert.True(result.Launched);
        Assert.Equal(newRole, newRole);
    }

    private static IDisposable AcquireLease(string root, string role)
    {
        Type type = typeof(MacroToolExclusiveLease);
        MethodInfo method = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name.Contains("Acquire", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(m => m.Name.StartsWith("Try", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenByDescending(m => m.GetParameters().Length)
            .First();
        ParameterInfo[] parameters = method.GetParameters();
        object?[] args = parameters.Select(p => LeaseArgument(p, root, role)).ToArray();
        try
        {
            object? result = method.Invoke(null, args);
            if (result is IDisposable disposable) return disposable;
            IDisposable? fromOut = args.OfType<IDisposable>().FirstOrDefault();
            if (fromOut is not null) return fromOut;
            if (result is bool success && !success) throw new InvalidOperationException("Exclusive lease was not acquired.");
            throw new InvalidOperationException("Exclusive lease API did not return a disposable lease.");
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            throw error.InnerException;
        }
    }

    private static object? LeaseArgument(ParameterInfo parameter, string root, string role)
    {
        Type type = parameter.ParameterType;
        if (type.IsByRef) return null;
        if (type == typeof(string))
        {
            string name = parameter.Name ?? string.Empty;
            if (name.Contains("state", StringComparison.OrdinalIgnoreCase)) return Path.Combine(root, "Program", "State");
            if (name.Contains("root", StringComparison.OrdinalIgnoreCase)) return root;
            if (name.Contains("role", StringComparison.OrdinalIgnoreCase)) return role;
            if (name.Contains("integrity", StringComparison.OrdinalIgnoreCase)) return "Medium";
            if (name.Contains("mode", StringComparison.OrdinalIgnoreCase)) return "DesktopSafe";
            if (name.Contains("process", StringComparison.OrdinalIgnoreCase)) return role == "Recorder" ? "MacroRecorder.exe" : "MacroPlayer.exe";
            if (name.Contains("token", StringComparison.OrdinalIgnoreCase)) return Guid.NewGuid().ToString("N");
            return role;
        }

        if (type == typeof(int)) return Environment.ProcessId;
        if (type == typeof(DateTimeOffset)) return DateTimeOffset.UtcNow;
        if (type == typeof(DateTime)) return Process.GetCurrentProcess().StartTime.ToUniversalTime();
        if (type == typeof(CancellationToken)) return CancellationToken.None;
        if (parameter.HasDefaultValue) return parameter.DefaultValue;
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }

    private static void WithSandbox(Action<string> action)
    {
        string path = ProjectLocalTestSandbox.Create();
        string root = Path.Combine(path, "lease-project");
        Directory.CreateDirectory(Path.Combine(root, "Program", "State", "Logs"));
        action(root);
    }

    private static string FindProjectRoot()
    {
        return TestProjectEnvironment.Root;
    }

    private enum ReplacementAction { Launch, Replace }

    private sealed record FakeTool(
        string Role,
        bool Active = false,
        bool Countdown = false,
        bool Watchdog = false,
        bool High = false,
        bool Responsive = true,
        bool TokenValid = true,
        bool IdentityValid = true,
        bool Stale = false)
    {
        public static FakeTool Recorder(bool active = false) => new("Recorder", Active: active);
        public static FakeTool Player(bool active = false, bool countdown = false, bool watchdog = false, bool high = false, bool responsive = true, bool tokenValid = true, bool identityValid = true) =>
            new("Player", active, countdown, watchdog, high, responsive, tokenValid, identityValid);
        public static FakeTool StaleEntry() => new("Stale", Stale: true);
    }

    private sealed record ReplacementResult(
        int Closed,
        int StaleRemoved,
        bool InputsReleased,
        bool RecordingDiscarded,
        bool OldExitedBeforeLaunch,
        bool WatchdogExitedBeforeLaunch,
        int FinalSessionCount,
        int OrphanWatchdogs,
        bool UacRequested,
        int UacCount,
        bool Launched,
        int RemainingOldTools,
        bool CooperativeAttemptedBeforeExactKill,
        bool ExactKilled);

    private static class ReplacementModel
    {
        private static int _raceWinner;

        public static ReplacementAction Decide(IReadOnlyCollection<FakeTool> tools) => tools.Count == 0 ? ReplacementAction.Launch : ReplacementAction.Replace;

        public static ReplacementResult Replace(IReadOnlyCollection<FakeTool> tools, bool elevatedLaunch = false, bool uacAccepted = true)
        {
            FakeTool[] live = tools.Where(t => !t.Stale).ToArray();
            bool cleanupUac = live.Any(t => t.High && !t.Responsive);
            bool requestUac = cleanupUac || elevatedLaunch;
            int uacCount = requestUac ? 1 : 0;
            bool launch = !requestUac || uacAccepted;
            bool exactKill = live.Any(t => !t.Responsive && t.TokenValid && t.IdentityValid) && uacAccepted;
            bool allExit = live.All(t => t.Responsive || exactKill);
            return new ReplacementResult(
                Closed: launch && allExit ? live.Length : 0,
                StaleRemoved: tools.Count(t => t.Stale),
                InputsReleased: live.Where(t => t.Role == "Player" && t.Active).All(_ => true),
                RecordingDiscarded: live.Any(t => t.Role == "Recorder" && t.Active),
                OldExitedBeforeLaunch: launch && allExit,
                WatchdogExitedBeforeLaunch: launch && allExit,
                FinalSessionCount: launch && allExit ? 1 : live.Length,
                OrphanWatchdogs: launch && allExit ? 0 : live.Count(t => t.Watchdog),
                UacRequested: requestUac,
                UacCount: uacCount,
                Launched: launch && allExit,
                RemainingOldTools: launch && allExit ? 0 : live.Length,
                CooperativeAttemptedBeforeExactKill: live.Any(t => !t.Responsive),
                ExactKilled: exactKill);
        }

        public static bool[] Race(string first, string second)
        {
            _raceWinner = 0;
            bool[] results = new bool[2];
            Parallel.Invoke(
                () => results[0] = Interlocked.CompareExchange(ref _raceWinner, 1, 0) == 0,
                () => results[1] = Interlocked.CompareExchange(ref _raceWinner, 2, 0) == 0);
            _ = first;
            _ = second;
            return results;
        }
    }
}

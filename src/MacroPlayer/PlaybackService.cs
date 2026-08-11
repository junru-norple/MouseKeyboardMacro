using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MacroCore.Runtime;
using MacroCore.Diagnostics;
using MacroCore.Display;
using MacroCore.Input;
using MacroCore.Models;
using MacroCore.Timing;

namespace MacroPlayer;

public sealed class PlaybackService : IDisposable
{
    // Absolute-only compatibility engine used by the retired PlayerForm/tests.
    // Program.Main uses PlaybackLibraryForm + SafePlaybackSession as the formal path.
    private readonly GlobalInputHook _hook;
    private readonly BoundedCapturePipeline _controlPipeline;
    private readonly LongPressDetector _f11Detector;
    private readonly MacroFile _macro;
    private readonly IPlaybackInputSender _inputSender;
    private readonly MacroDisplayLayout _playbackLayout;
    private readonly CancellationTokenSource _internalCts = new();
    private readonly object _stateLock = new();
    private readonly HashSet<MouseButtonKind> _pressedMouseButtons = [];
    private readonly HashSet<KeyIdentity> _pressedKeys = [];

    private bool _isStopped;

    public event Action<int, int>? ProgressChanged;
    public event Action<bool, string?>? Completed;

    public PlaybackCompatibilityStatus CompatibilityStatus { get; private set; } = PlaybackCompatibilityStatus.Unknown;

    public bool IsStopped
    {
        get
        {
            lock (_stateLock)
            {
                return _isStopped;
            }
        }
    }

    public PlaybackService(MacroFile macro)
        : this(macro, new SendInputPlaybackSender())
    {
    }

    public PlaybackService(MacroFile macro, IPlaybackInputSender inputSender)
    {
        _macro = macro ?? throw new ArgumentNullException(nameof(macro));
        _inputSender = inputSender ?? throw new ArgumentNullException(nameof(inputSender));
        _playbackLayout = DisplayLayoutProvider.GetCurrentLayout();
        _controlPipeline = new BoundedCapturePipeline(256, HookCallbackSafety.F11);
        _hook = new GlobalInputHook
        {
            SuppressionMode = HookSuppressionMode.PlayerF11,
            TryEnqueue = _controlPipeline.TryEnqueue
        };
        _f11Detector = new LongPressDetector(2000);
        _controlPipeline.EventReady += OnHookInput;
        _f11Detector.Triggered += () =>
        {
            lock (_stateLock)
            {
                _isStopped = true;
            }
            _internalCts.Cancel();
        };
    }

    public async Task PlayAsync(CancellationToken externalToken)
    {
        if (!AbsoluteOnlyPlaybackGate.TryValidate(_macro, out string validationError))
        {
            CompatibilityStatus = PlaybackCompatibilityStatus.Error;
            Completed?.Invoke(false, validationError);
            return;
        }

        using var combined = CancellationTokenSource.CreateLinkedTokenSource(externalToken, _internalCts.Token);
        var ct = combined.Token;

        _hook.Start();
        LogCompatibility("playback=start mode=AbsoluteDesktop");

        try
        {
            var events = _macro.Events.OrderBy(e => e.TimeMs).ToList();
            PlaybackTimelineEvent<MacroEventRecord>[] timeline = events
                .Select(item => new PlaybackTimelineEvent<MacroEventRecord>(
                    item.TimeMs,
                    item,
                    item.Type == MacroEventKind.MouseMove
                        ? PlaybackTimelineEventClass.MouseMove
                        : PlaybackTimelineEventClass.Essential))
                .ToArray();
            int completed = 0;
            PlaybackTimelineScheduler scheduler = new();
            _ = await scheduler.RunAsync(
                timeline,
                timeline.Length == 0 ? 0 : timeline[^1].OffsetMilliseconds,
                (source, start, count, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    for (int index = start; index < start + count; index++)
                    {
                        ProcessEvent(source[index].Value);
                        completed++;
                        ProgressChanged?.Invoke(completed, events.Count);
                    }
                    return ValueTask.CompletedTask;
                },
                progress: null,
                ct).ConfigureAwait(false);

            CompatibilityStatus = PlaybackCompatibilityStatus.Compatible;
            Completed?.Invoke(!ct.IsCancellationRequested, "播放完成");
        }
        catch (OperationCanceledException)
        {
            Completed?.Invoke(false, "播放已停止");
        }
        catch (StandardInputRejectedException ex)
        {
            CompatibilityStatus = PlaybackCompatibilityStatus.UnsupportedSendInput;
            LogCompatibility($"playback=PLAYBACK_TARGET_REJECTED status=UNSUPPORTED_STANDARD_INPUT mode=AbsoluteDesktop win32_error={ex.Win32Error}");
            Completed?.Invoke(false, "PLAYBACK_TARGET_REJECTED：目標不接受標準 Windows SendInput；捕捉檔案未被判定為缺少事件，本工具不進行繞過。");
        }
        catch (Exception ex)
        {
            CompatibilityStatus = PlaybackCompatibilityStatus.Error;
            Completed?.Invoke(false, ex.Message);
        }
        finally
        {
            _hook.Stop();
            ReleasePressedInputs();
        }
    }

    internal int PressedKeyCount
    {
        get
        {
            lock (_stateLock)
            {
                return _pressedKeys.Count;
            }
        }
    }

    internal void ProcessEvent(MacroEventRecord item)
    {
        if (item.Type == MacroEventKind.KeyDown)
        {
            if (!item.ScanCode.HasValue)
            {
                return;
            }

            var identity = new KeyIdentity(item.VirtualKey ?? 0, item.ScanCode.Value, item.IsExtended);
            _inputSender.KeyDown(identity.ScanCode, identity.VirtualKey, identity.IsExtended);
            lock (_stateLock)
            {
                _pressedKeys.Add(identity);
            }
            return;
        }

        if (item.Type == MacroEventKind.KeyUp)
        {
            if (!item.ScanCode.HasValue)
            {
                return;
            }

            var identity = new KeyIdentity(item.VirtualKey ?? 0, item.ScanCode.Value, item.IsExtended);
            _inputSender.KeyUp(identity.ScanCode, identity.VirtualKey, identity.IsExtended);
            lock (_stateLock)
            {
                _pressedKeys.Remove(identity);
            }
            return;
        }

        if (item.Type == MacroEventKind.MouseMove)
        {
            if (!item.X.HasValue || !item.Y.HasValue)
            {
                throw new InvalidDataException(AbsoluteOnlyPlaybackGate.MissingAbsoluteCoordinatesMessage);
            }
            _inputSender.MouseMove(item.X.Value, item.Y.Value, _playbackLayout);
            return;
        }

        if (item.X is null || item.Y is null)
        {
            throw new InvalidDataException(AbsoluteOnlyPlaybackGate.MissingAbsoluteCoordinatesMessage);
        }

        if (item.Type == MacroEventKind.MouseDown && item.MouseButton.HasValue)
        {
            _inputSender.MouseDown(item.MouseButton.Value, _playbackLayout, item.X.Value, item.Y.Value);
            lock (_stateLock)
            {
                _pressedMouseButtons.Add(item.MouseButton.Value);
            }
        }
        else if (item.Type == MacroEventKind.MouseUp && item.MouseButton.HasValue)
        {
            _inputSender.MouseUp(item.MouseButton.Value, _playbackLayout, item.X.Value, item.Y.Value);
            lock (_stateLock)
            {
                _pressedMouseButtons.Remove(item.MouseButton.Value);
            }
        }
        else if (item.Type == MacroEventKind.MouseWheel)
        {
            _inputSender.MouseWheel(item.WheelDelta ?? 0, _playbackLayout, item.X.Value, item.Y.Value);
        }
        else if (item.Type == MacroEventKind.MouseHorizontalWheel)
        {
            _inputSender.MouseHorizontalWheel(item.WheelDelta ?? 0, _playbackLayout, item.X.Value, item.Y.Value);
        }
    }

    private void OnHookInput(HookEvent evt)
    {
        if (evt.Source != MacroCore.Input.HookSource.Keyboard || evt.VirtualKey != 0x7A)
        {
            return;
        }

        if (evt.Message == 0x0100 || evt.Message == 0x0104)
        {
            _f11Detector.OnKeyDown();
        }
        else if (evt.Message == 0x0101 || evt.Message == 0x0105)
        {
            _f11Detector.OnKeyUp();
        }
    }

    internal void ReleasePressedInputs()
    {
        KeyIdentity[] pressedKeys;
        MouseButtonKind[] pressedMouseButtons;
        lock (_stateLock)
        {
            pressedKeys = _pressedKeys.ToArray();
            pressedMouseButtons = _pressedMouseButtons.ToArray();
            _pressedKeys.Clear();
            _pressedMouseButtons.Clear();
        }

        foreach (var key in pressedKeys)
        {
            try
            {
                _inputSender.KeyUp(key.ScanCode, key.VirtualKey, key.IsExtended);
            }
            catch
            {
                // Best effort: continue releasing every other pressed input.
            }
        }

        foreach (var button in pressedMouseButtons)
        {
            try
            {
                _inputSender.ReleaseMouseButton(button);
            }
            catch
            {
                // Best effort: continue releasing every other pressed input.
            }
        }
    }

    public void Dispose()
    {
        _internalCts.Cancel();
        ReleasePressedInputs();
        _hook.Dispose();
        _controlPipeline.Dispose();
        _internalCts.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void LogCompatibility(string message)
    {
        try
        {
            var logs = RuntimeFolders.Logs;
            Directory.CreateDirectory(logs);
            MacroCore.Diagnostics.RotatingLog.Write(
                Path.Combine(logs, "game_input_compatibility.log"),
                $"{DateTimeOffset.Now:O} {message}");
        }
        catch
        {
            // Compatibility diagnostics are best effort and never contain key data.
        }
    }
}

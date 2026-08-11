using MacroCore.Models;

namespace MacroCore.Input;

public enum InputEventDisposition
{
    Recorded,
    MonitoredOnly,
    Duplicate,
    OwnSyntheticFiltered,
    ControlNotRecorded,
    Unsupported
}

public sealed record InputMonitorEntry(
    string InputName,
    string Action,
    string Source,
    InputEventDisposition Disposition,
    DateTimeOffset ObservedAt)
{
    public string DisplayText => $"{InputName} {Action} | {Source} | {DispositionText(Disposition)}";

    private static string DispositionText(InputEventDisposition value) => value switch
    {
        InputEventDisposition.Recorded => "RECORDED",
        InputEventDisposition.MonitoredOnly => "MONITORED ONLY",
        InputEventDisposition.Duplicate => "DUPLICATE",
        InputEventDisposition.OwnSyntheticFiltered => "OWN SYNTHETIC FILTERED",
        InputEventDisposition.ControlNotRecorded => "CONTROL / NOT RECORDED",
        _ => "UNSUPPORTED"
    };
}

public sealed record InputMonitorSnapshot(
    IReadOnlyList<string> HeldInputs,
    IReadOnlyList<InputMonitorEntry> RecentEvents,
    long LowLevelKeyboardObserved,
    long RawKeyboardObserved,
    long KeyboardOutput,
    long KeyboardDuplicate,
    long LowLevelMouseObserved,
    long RawMouseObserved,
    long MouseOutput,
    long DroppedMove,
    int QueueUsage,
    long EventsPerSecond)
{
    public string HeldText => HeldInputs.Count == 0 ? "目前按住：無" : "目前按住：" + string.Join(" + ", HeldInputs);
}

public sealed class InputMonitorModel
{
    public const int DefaultRecentCapacity = 25;
    private readonly object _sync = new();
    private readonly int _capacity;
    private readonly HashSet<string> _held = new(StringComparer.Ordinal);
    private readonly Queue<InputMonitorEntry> _recent = new();
    private long _llKeyboard;
    private long _rawKeyboard;
    private long _keyboardOutput;
    private long _keyboardDuplicate;
    private long _llMouse;
    private long _rawMouse;
    private long _mouseOutput;
    private CaptureQueueStats _queue;

    public InputMonitorModel(int recentCapacity = DefaultRecentCapacity)
    {
        _capacity = Math.Clamp(recentCapacity, 20, 30);
    }

    public void Observe(HookEvent input, InputEventDisposition disposition)
    {
        lock (_sync)
        {
            switch (input.Source)
            {
                case HookSource.Keyboard: _llKeyboard++; break;
                case HookSource.RawKeyboard: _rawKeyboard++; break;
                case HookSource.Mouse: _llMouse++; break;
                case HookSource.RawMouse: _rawMouse++; break;
            }

            if (disposition != InputEventDisposition.OwnSyntheticFiltered)
            {
                UpdateHeld(input);
            }

            if (disposition is InputEventDisposition.Recorded or InputEventDisposition.MonitoredOnly)
            {
                if (input.IsKeyboard) _keyboardOutput++;
                if (input.IsMouse) _mouseOutput++;
            }
            else if (disposition == InputEventDisposition.Duplicate && input.IsKeyboard)
            {
                _keyboardDuplicate++;
            }

            _recent.Enqueue(new InputMonitorEntry(
                InputNameFormatter.Name(input),
                InputNameFormatter.Action(input),
                input.IsRaw ? "RAW" : "LL",
                disposition,
                DateTimeOffset.Now));
            while (_recent.Count > _capacity)
            {
                _recent.Dequeue();
            }
        }
    }

    public void UpdateQueue(CaptureQueueStats queue)
    {
        lock (_sync)
        {
            _queue = queue;
        }
    }

    public InputMonitorSnapshot Snapshot()
    {
        lock (_sync)
        {
            return new InputMonitorSnapshot(
                _held.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                _recent.Reverse().ToArray(),
                _llKeyboard,
                _rawKeyboard,
                _keyboardOutput,
                _keyboardDuplicate,
                _llMouse,
                _rawMouse,
                _mouseOutput,
                _queue.DroppedMoveEvents,
                _queue.UsagePercent,
                _queue.EventsPerSecond);
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _held.Clear();
            _recent.Clear();
            _llKeyboard = 0;
            _rawKeyboard = 0;
            _keyboardOutput = 0;
            _keyboardDuplicate = 0;
            _llMouse = 0;
            _rawMouse = 0;
            _mouseOutput = 0;
            _queue = default;
        }
    }

    private void UpdateHeld(HookEvent input)
    {
        var name = InputNameFormatter.Name(input);
        if (input.IsKeyboard)
        {
            if (input.Message is 0x0100 or 0x0104) _held.Add(name);
            if (input.Message is 0x0101 or 0x0105) _held.Remove(name);
            return;
        }

        if (input.MouseButton.HasValue)
        {
            if (input.Message is 0x0201 or 0x0204 or 0x0207 or 0x020B) _held.Add(name);
            if (input.Message is 0x0202 or 0x0205 or 0x0208 or 0x020C) _held.Remove(name);
        }
    }
}

public static class InputNameFormatter
{
    private static readonly Dictionary<int, string> KeyNames = new()
    {
        [0x08] = "Backspace", [0x09] = "Tab", [0x0D] = "Enter", [0x13] = "Pause/Break",
        [0x1B] = "ESC", [0x20] = "Space", [0x21] = "Page Up", [0x22] = "Page Down",
        [0x23] = "End", [0x24] = "Home", [0x25] = "Left", [0x26] = "Up",
        [0x27] = "Right", [0x28] = "Down", [0x2C] = "Print Screen", [0x2D] = "Insert",
        [0x2E] = "Delete", [0x5B] = "Left Windows", [0x5C] = "Right Windows",
        [0xA0] = "Left Shift", [0xA1] = "Right Shift", [0xA2] = "Left Ctrl",
        [0xA3] = "Right Ctrl", [0xA4] = "Left Alt", [0xA5] = "Right Alt"
    };

    public static string Name(HookEvent input)
    {
        if (input.IsMouse)
        {
            if (input.MouseButton.HasValue) return "Mouse " + input.MouseButton.Value;
            if (input.Message == 0x020A) return "Wheel";
            if (input.Message == 0x020E) return "Horizontal Wheel";
            return "Mouse";
        }

        if (KeyNames.TryGetValue(input.VirtualKey, out var name)) return name;
        if (input.VirtualKey is >= 0x70 and <= 0x87) return "F" + (input.VirtualKey - 0x6F);
        if (input.VirtualKey is >= 0x60 and <= 0x69) return "NumPad " + (input.VirtualKey - 0x60);
        if (input.VirtualKey is >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39) return ((char)input.VirtualKey).ToString();
        return $"VK 0x{input.VirtualKey:X2}";
    }

    public static string Action(HookEvent input)
    {
        if (input.IsKeyboard) return input.Message is 0x0100 or 0x0104 ? "Down" : "Up";
        if (input.Message is 0x020A or 0x020E) return ((short)(input.MouseData >> 16)).ToString("+0;-0;0");
        if (input.IsMouseMove) return input.IsRaw ? $"Move ({input.DeltaX},{input.DeltaY})" : "Move";
        return input.Message is 0x0201 or 0x0204 or 0x0207 or 0x020B ? "Down" : "Up";
    }
}

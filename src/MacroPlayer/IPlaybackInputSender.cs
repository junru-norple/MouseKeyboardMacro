using MacroCore.Input;
using MacroCore.Models;

namespace MacroPlayer;

public interface IPlaybackInputSender
{
    void KeyDown(int scanCode, int virtualKey, bool isExtended);
    void KeyUp(int scanCode, int virtualKey, bool isExtended);
    void MouseDown(MouseButtonKind button, MacroDisplayLayout layout, int x, int y);
    void MouseUp(MouseButtonKind button, MacroDisplayLayout layout, int x, int y);
    void MouseMove(int x, int y, MacroDisplayLayout layout);
    void MouseWheel(int delta, MacroDisplayLayout layout, int x, int y);
    void MouseHorizontalWheel(int delta, MacroDisplayLayout layout, int x, int y);
    void ReleaseMouseButton(MouseButtonKind button);
}

internal sealed class SendInputPlaybackSender : IPlaybackInputSender
{
    public void KeyDown(int scanCode, int virtualKey, bool isExtended) =>
        SendInputService.KeyDown(scanCode, virtualKey, isExtended);

    public void KeyUp(int scanCode, int virtualKey, bool isExtended) =>
        SendInputService.KeyUp(scanCode, virtualKey, isExtended);

    public void MouseDown(MouseButtonKind button, MacroDisplayLayout layout, int x, int y) =>
        SendInputService.MouseDown(button, layout, x, y);

    public void MouseUp(MouseButtonKind button, MacroDisplayLayout layout, int x, int y) =>
        SendInputService.MouseUp(button, layout, x, y);

    public void MouseMove(int x, int y, MacroDisplayLayout layout) =>
        SendInputService.MouseMove(x, y, layout);

    public void MouseWheel(int delta, MacroDisplayLayout layout, int x, int y) =>
        SendInputService.MouseWheel(delta, layout, x, y);

    public void MouseHorizontalWheel(int delta, MacroDisplayLayout layout, int x, int y) =>
        SendInputService.MouseHorizontalWheel(delta, layout, x, y);

    public void ReleaseMouseButton(MouseButtonKind button) =>
        SendInputService.ReleaseMouseButton(button);
}

using System.Runtime.InteropServices;

namespace WPods.Ui;

/// <summary>
/// Hidden top-level window for system broadcasts (colour, theme, display and DPI changes) and the Win+Shift+A hotkey.
/// Message-only windows don't receive broadcasts, so this is a real (never shown) popup window.
/// </summary>
internal sealed class HostWindow : NativeWindow, IDisposable
{
    private const int HotKeyId = 1;
    private const uint VkA = 0x41;
    private const int SpiSetWorkArea = 0x002F;

    private readonly Action _colorsChanged;
    private readonly Action _displayChanged;
    private readonly Action _hotKey;
    private bool _hotKeyRegistered;

    public HostWindow(Action colorsChanged, Action displayChanged, Action hotKey)
    {
        _colorsChanged = colorsChanged;
        _displayChanged = displayChanged;
        _hotKey = hotKey;
        CreateHandle(new CreateParams { Caption = "WPods", Style = NativeUi.WsPopup, ExStyle = NativeUi.WsExToolWindow });
    }

    /// <summary>Registers or unregisters Win+Shift+A; a shortcut already taken by another app is skipped silently.</summary>
    public void SetHotKey(bool enabled)
    {
        if (enabled == _hotKeyRegistered)
        {
            return;
        }
        if (enabled)
        {
            _hotKeyRegistered = NativeUi.RegisterHotKey(Handle, HotKeyId, NativeUi.ModWin | NativeUi.ModShift | NativeUi.ModNoRepeat, VkA);
        }
        else
        {
            NativeUi.UnregisterHotKey(Handle, HotKeyId);
            _hotKeyRegistered = false;
        }
    }

    public void Dispose()
    {
        SetHotKey(false);
        DestroyHandle();
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case NativeUi.WmSettingChange when m.WParam == SpiSetWorkArea:
            case NativeUi.WmDisplayChange:
            case NativeUi.WmDpiChanged:
                _displayChanged();
                break;
            case NativeUi.WmSettingChange when m.LParam != 0 && Marshal.PtrToStringUni(m.LParam) == "ImmersiveColorSet":
            case NativeUi.WmDwmColorizationColorChanged:
                _colorsChanged();
                break;
            case NativeUi.WmHotKey when m.WParam == HotKeyId:
                _hotKey();
                break;
        }
        base.WndProc(ref m);
    }
}

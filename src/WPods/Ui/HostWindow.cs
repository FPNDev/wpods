using System.Runtime.InteropServices;

namespace WPods.Ui;

/// <summary>
/// Hidden top-level window for system broadcasts (colour, theme, display and DPI changes) and global hotkeys: the
/// popup's Win+Shift+A and the user's hotkeys. Message-only windows don't receive broadcasts, so this is a real
/// (never shown) popup window.
/// </summary>
internal sealed class HostWindow : NativeWindow, IDisposable
{
    /// <summary>WM_HOTKEY id of the popup's Win+Shift+A.</summary>
    public const int PopupHotKeyId = 1;

    private const uint VkA = 0x41;
    private const int SpiSetWorkArea = 0x002F;

    private readonly Action _colorsChanged;
    private readonly Action _displayChanged;
    private readonly Action<int> _hotKey;
    private readonly HashSet<int> _registered = [];

    public HostWindow(Action colorsChanged, Action displayChanged, Action<int> hotKey)
    {
        _colorsChanged = colorsChanged;
        _displayChanged = displayChanged;
        _hotKey = hotKey;
        CreateHandle(new CreateParams { Caption = "WPods", Style = NativeUi.WsPopup, ExStyle = NativeUi.WsExToolWindow });
    }

    /// <summary>Registers or unregisters Win+Shift+A; a shortcut already taken by another app is skipped silently.</summary>
    public void SetHotKey(bool enabled)
    {
        if (enabled)
        {
            Register(PopupHotKeyId, NativeUi.ModWin | NativeUi.ModShift, VkA);
        }
        else
        {
            Unregister(PopupHotKeyId);
        }
    }

    /// <summary>False when Windows or another app already has the shortcut. Auto-repeat never fires it again.</summary>
    public bool Register(int id, uint modifiers, uint vk)
    {
        if (_registered.Contains(id))
        {
            return true;
        }
        if (!NativeUi.RegisterHotKey(Handle, id, modifiers | NativeUi.ModNoRepeat, vk))
        {
            return false;
        }
        _registered.Add(id);
        return true;
    }

    public void Unregister(int id)
    {
        if (_registered.Remove(id))
        {
            NativeUi.UnregisterHotKey(Handle, id);
        }
    }

    public void Dispose()
    {
        foreach (int id in _registered)
        {
            NativeUi.UnregisterHotKey(Handle, id);
        }
        _registered.Clear();
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
            case NativeUi.WmHotKey:
                _hotKey((int)m.WParam);
                break;
        }
        base.WndProc(ref m);
    }
}

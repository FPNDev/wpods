namespace WPods.Ui;

/// <summary>
/// The nearby popup: a topmost tool window that never activates on show or click (only Win+Shift+A activates it),
/// no frame, DWM round corners and shadow. The host sizes and places it.
/// </summary>
internal sealed class PopupWindow : Form
{
    public const int WidthDip = 320;
    public const int MarginDip = 12;

    private bool _dark;

    public PopupWindow(Color background, bool dark, WebPane pane)
    {
        Pane = pane;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = background;
        _dark = dark;
    }

    public WebPane Pane { get; }

    /// <summary>The window moved to a monitor with another DPI; the host places it again.</summary>
    public event Action? ScaleChanged;

    /// <summary>Alt+F4 or an outside WM_CLOSE: the app hides the window instead.</summary>
    public event Action? CloseRequested;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            // A thick frame (removed again by WM_NCCALCSIZE) is what gives the window its DWM shadow.
            cp.Style |= NativeUi.WsThickFrame;
            cp.ExStyle |= NativeUi.WsExNoActivate | NativeUi.WsExToolWindow | NativeUi.WsExTopmost;
            return cp;
        }
    }

    public void SetTheme(Color background, bool dark)
    {
        BackColor = background;
        Pane.SetBackground(background);
        _dark = dark;
        if (IsHandleCreated)
        {
            NativeUi.SetWindowInt(Handle, NativeUi.DwmwaUseImmersiveDarkMode, dark ? 1 : 0);
        }
    }

    public void Destroy()
    {
        Pane.Dispose();
        Dispose();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeUi.SetWindowInt(Handle, NativeUi.DwmwaWindowCornerPreference, NativeUi.DwmwcpRound);
        NativeUi.SetWindowInt(Handle, NativeUi.DwmwaUseImmersiveDarkMode, _dark ? 1 : 0);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason is CloseReason.UserClosing or CloseReason.TaskManagerClosing)
        {
            e.Cancel = true;
            CloseRequested?.Invoke();
        }
        base.OnFormClosing(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Pane.SetBounds(new Rectangle(0, 0, Width, Height));
    }

    protected override void OnMove(EventArgs e)
    {
        base.OnMove(e);
        Pane.NotifyMoved();
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case NativeUi.WmNcCalcSize when m.WParam != 0:
                m.Result = 0;
                return;
            case NativeUi.WmNcHitTest:
                m.Result = NativeUi.HtClient;
                return;
            case NativeUi.WmMouseActivate:
                m.Result = NativeUi.MaNoActivate;
                return;
            case NativeUi.WmDpiChanged:
                m.Result = 0;
                ScaleChanged?.Invoke();
                return;
            default:
                base.WndProc(ref m);
                return;
        }
    }
}

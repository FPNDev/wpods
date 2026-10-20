namespace WPods.Ui;

/// <summary>
/// The main window: no title bar or frame (the page draws its own), Windows 11 round corners and shadow by DWM,
/// 400 DIPs wide, 480 to 960 DIPs tall, resizable vertically only from a thin strip at the top and bottom edge
/// (the page covers everything else, so the strips are the only area that sees WM_NCHITTEST).
/// </summary>
internal sealed class MainWindow : Form
{
    public const int WidthDip = 400;
    public const int MinHeightDip = 480;
    public const int MaxHeightDip = 960;
    private const int ResizeStripDip = 4;

    private bool _dark;

    public MainWindow(Rectangle bounds, Color background, bool dark, string title, Icon icon, WebPane pane)
    {
        Pane = pane;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = bounds;
        BackColor = background;
        Text = title;
        Icon = icon;
        _dark = dark;
    }

    public WebPane Pane { get; }

    /// <summary>Close button, Alt+F4 or the taskbar's Close: the app hides the window instead.</summary>
    public event Action? CloseRequested;

    /// <summary>The user finished resizing; new height in DIPs.</summary>
    public event Action<double>? HeightChanged;

    private float DpiScale => NativeUi.GetDpiForWindow(Handle) / 96f;

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

    /// <summary>Destroys the window for real (exit, language switch, hidden too long).</summary>
    public void Destroy()
    {
        Pane.Dispose();
        Dispose();
    }

    public void BeginDrag()
    {
        NativeUi.ReleaseCapture();
        NativeUi.SendMessage(Handle, NativeUi.WmNcLButtonDown, NativeUi.HtCaption, 0);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeUi.SetWindowInt(Handle, NativeUi.DwmwaWindowCornerPreference, NativeUi.DwmwcpRound);
        NativeUi.SetWindowInt(Handle, NativeUi.DwmwaUseImmersiveDarkMode, _dark ? 1 : 0);
        Pane.SetBounds(PaneBounds());
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            CloseRequested?.Invoke();
        }
        base.OnFormClosing(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (IsHandleCreated)
        {
            Pane.SetBounds(PaneBounds());
        }
    }

    protected override void OnMove(EventArgs e)
    {
        base.OnMove(e);
        Pane.NotifyMoved();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        Pane.Focus();
    }

    protected override unsafe void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case NativeUi.WmNcCalcSize when m.WParam != 0:
                // The whole window is client area.
                m.Result = 0;
                return;
            case NativeUi.WmNcHitTest:
            {
                int y = (short)((long)m.LParam >> 16) - Top;
                int strip = Strip();
                m.Result = y < strip ? NativeUi.HtTop : y >= Height - strip ? NativeUi.HtBottom : NativeUi.HtClient;
                return;
            }
            case NativeUi.WmGetMinMaxInfo:
            {
                float scale = NativeUi.GetDpiForWindow(m.HWnd) / 96f;
                var info = (NativeUi.MinMaxInfo*)m.LParam;
                int width = (int)MathF.Round(WidthDip * scale);
                info->MinTrackSize = new Point(width, (int)MathF.Round(MinHeightDip * scale));
                info->MaxTrackSize = new Point(width, (int)MathF.Round(MaxHeightDip * scale));
                m.Result = 0;
                return;
            }
            case NativeUi.WmDpiChanged:
            {
                var r = *(NativeUi.Rect*)m.LParam;
                NativeUi.SetBounds(Handle, Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom));
                m.Result = 0;
                return;
            }
            case NativeUi.WmExitSizeMove:
                base.WndProc(ref m);
                HeightChanged?.Invoke(Height / DpiScale);
                return;
            default:
                base.WndProc(ref m);
                return;
        }
    }

    private int Strip() => (int)MathF.Round(ResizeStripDip * DpiScale);

    private Rectangle PaneBounds()
    {
        int strip = Strip();
        return new Rectangle(0, strip, Width, Math.Max(0, Height - 2 * strip));
    }
}

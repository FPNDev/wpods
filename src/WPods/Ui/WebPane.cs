using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using WPods.Core;

namespace WPods.Ui;

/// <summary>
/// One WebView2 controller in a host window, showing a page of web\ through https://app.wpods/. Messages from the page
/// arrive on the UI thread; <see cref="Post"/> drops messages until the page has sent "ready".
/// </summary>
internal sealed class WebPane : IDisposable
{
    private const string HostName = "app.wpods";

    private readonly Action<WebPane, PageMessage> _onMessage;
    private readonly Action<WebPane> _onFailed;
    private CoreWebView2Controller? _controller;
    private Rectangle _bounds;
    private bool _visible = true;
    private bool _disposed;

    public WebPane(Action<WebPane, PageMessage> onMessage, Action<WebPane> onFailed)
    {
        _onMessage = onMessage;
        _onFailed = onFailed;
    }

    /// <summary>The page sent "ready" (it has its listener and waits for init).</summary>
    public bool Ready { get; private set; }

    /// <summary>One environment per process; user data in %LOCALAPPDATA%\WPods\WebView2.</summary>
    public static Task<CoreWebView2Environment> CreateEnvironmentAsync() =>
        CoreWebView2Environment.CreateAsync(
            null,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WPods", "WebView2"),
            new CoreWebView2EnvironmentOptions());

    /// <summary>Creates the controller in <paramref name="parent"/> and navigates to <paramref name="page"/> (e.g. "index.html").</summary>
    public async Task InitializeAsync(CoreWebView2Environment environment, nint parent, string page, Color background)
    {
        CoreWebView2ControllerOptions options = environment.CreateCoreWebView2ControllerOptions();
        options.DefaultBackgroundColor = background;
        CoreWebView2Controller controller = await environment.CreateCoreWebView2ControllerAsync(parent, options);
        if (_disposed)
        {
            controller.Close();
            return;
        }
        _controller = controller;
        controller.AllowExternalDrop = false;
        controller.Bounds = _bounds;
        controller.IsVisible = _visible;
        CoreWebView2 core = controller.CoreWebView2;
        CoreWebView2Settings settings = core.Settings;
#if DEBUG
        settings.AreDevToolsEnabled = true;
#else
        settings.AreDevToolsEnabled = false;
#endif
        settings.AreDefaultContextMenusEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsPinchZoomEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.IsNonClientRegionSupportEnabled = false;
        core.SetVirtualHostNameToFolderMapping(HostName, Path.Combine(AppContext.BaseDirectory, "web"), CoreWebView2HostResourceAccessKind.Deny);
        core.NewWindowRequested += static (_, e) => e.Handled = true;
        core.WebMessageReceived += OnWebMessage;
        core.ProcessFailed += (_, e) =>
        {
            Log.Warn("ui", $"WebView2 {page} process failed: {e.ProcessFailedKind} {e.Reason}");
            if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited or CoreWebView2ProcessFailedKind.RenderProcessExited)
            {
                _onFailed(this);
            }
        };
        core.Navigate($"https://{HostName}/{page}");
    }

    public void Post(string json)
    {
        if (Ready && _controller is { } controller)
        {
            controller.CoreWebView2.PostWebMessageAsJson(json);
        }
    }

    public void SetBounds(Rectangle bounds)
    {
        _bounds = bounds;
        if (_controller is { } controller)
        {
            controller.Bounds = bounds;
        }
    }

    /// <summary>Hidden = no rendering; showing again also resumes a suspended page.</summary>
    public void SetVisible(bool visible)
    {
        _visible = visible;
        if (_controller is { } controller)
        {
            controller.IsVisible = visible;
        }
    }

    /// <summary>Suspends the hidden page's script and timers (frees memory); a later <see cref="SetVisible"/>(true) resumes it.</summary>
    public async Task SuspendAsync()
    {
        if (_controller is { IsVisible: false } controller)
        {
            try
            {
                await controller.CoreWebView2.TrySuspendAsync();
            }
            catch (Exception e) when (e is InvalidOperationException or COMException)
            {
                // Not suspendable right now; it stays hidden, which is enough.
                Log.Info("ui", $"page not suspended: {e.Message}");
            }
        }
    }

    public void SetBackground(Color background)
    {
        if (_controller is { } controller)
        {
            controller.DefaultBackgroundColor = background;
        }
    }

    public void Focus() => _controller?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);

    public void NotifyMoved() => _controller?.NotifyParentWindowPositionChanged();

    public void Dispose()
    {
        _disposed = true;
        Ready = false;
        _controller?.Close();
        _controller = null;
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        PageMessage? message;
        try
        {
            message = JsonSerializer.Deserialize(e.WebMessageAsJson, PageJson.Default.PageMessage);
        }
        catch (JsonException ex)
        {
            Log.Warn("ui", $"page message not parsed: {ex.Message}");
            return;
        }
        if (message is null)
        {
            return;
        }
        if (message.Type == "ready")
        {
            Ready = true;
        }
        _onMessage(this, message);
    }
}

using WPods.Audio;
using WPods.Core;
using WPods.Nearby;
using WPods.Ui;

namespace WPods;

internal static class Program
{
    /// <summary>A second instance asked this one to show itself. Fires on a thread-pool thread.</summary>
    public static event Action? ActivationRequested;

    [STAThread]
    private static void Main(string[] args)
    {
        bool startup = false;
        foreach (string arg in args)
        {
            if (arg.Equals("--register-startup", StringComparison.OrdinalIgnoreCase))
            {
                // Setup's "Start WPods when I sign in": register and exit at once, no UI.
                StartupRegistration.SetEnabled(true);
                return;
            }
            if (arg.Equals("--startup", StringComparison.OrdinalIgnoreCase))
            {
                startup = true;
            }
        }

        using var instance = new Mutex(initiallyOwned: true, @"Local\WPods", out bool firstInstance);
        using var activation = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\WPods.Activate");
        if (!firstInstance)
        {
            NativeUi.AllowSetForegroundWindow(-1);
            activation.Set();
            return;
        }
        RegisteredWaitHandle activationWait = ThreadPool.RegisterWaitForSingleObject(
            activation, static (_, _) => OnActivationRequested(), null, Timeout.Infinite, executeOnlyOnce: false);

        Log.Info("app", $"start WPods {Application.ProductVersion} on Windows {Environment.OSVersion.Version} args=[{string.Join(' ', args)}] pid={Environment.ProcessId}");
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.ProcessExit += static (_, _) => Log.Flush();
        TaskScheduler.UnobservedTaskException += static (_, e) => Log.Error("app", "unobserved task exception", e.Exception);
        Application.ThreadException += OnThreadException;

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        TrayMenu.AllowDarkMode();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (Localization.Load(Path.Combine(AppContext.BaseDirectory, "locales")) is not { } locales)
        {
            Log.Error("app", "no locales found");
            Log.Flush();
            MessageBox.Show("No locales found", "WPods", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        Log.Info("app", $"locales loaded: {string.Join(',', locales.Files.Keys)}");

        var settings = new SettingsStore();
        var connections = new ConnectionManager(settings);
        var mic = new MicPipeline(connections);
        var keepAlive = new LowLatencyKeepAlive(connections);
        var ducker = new ConversationDucker(connections, mic);
        _ = new EarPause(connections);
        var nearby = new NearbyService(connections);
        // Opening the case releases a pair the user disconnected while Windows still has its link up.
        nearby.CaseOpened += connections.OnCaseOpened;
        var app = new TrayApp(settings, connections, nearby, mic, keepAlive, ducker, locales, startHidden: startup);
        using var defaultOutput = new DefaultOutputWatcher(connections);
        connections.Start();
        nearby.Start();
        Application.Run(app);

        activationWait.Unregister(null);
        Log.Info("app", "exit");
        Log.Flush();
    }

    private static void OnActivationRequested()
    {
        Log.Info("app", "second instance asked to show the window");
        ActivationRequested?.Invoke();
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Log.Error("app", $"unhandled exception (terminating={e.IsTerminating})", e.ExceptionObject as Exception);
        Log.Flush();
    }

    // Subscribing replaces WinForms' default handling, so this logs and then does what the default does: the
    // standard exception dialog, and Abort ends the process.
    private static void OnThreadException(object sender, ThreadExceptionEventArgs e)
    {
        Log.Error("app", "UI thread exception", e.Exception);
        Log.Flush();
        if (!SystemInformation.UserInteractive)
        {
            return;
        }
        using var dialog = new ThreadExceptionDialog(e.Exception);
        if (dialog.ShowDialog() == DialogResult.Abort)
        {
            Environment.Exit(0);
        }
    }
}

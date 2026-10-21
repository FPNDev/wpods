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
            activation, static (_, _) => ActivationRequested?.Invoke(), null, Timeout.Infinite, executeOnlyOnce: false);

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        TrayMenu.AllowDarkMode();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (Localization.Load(Path.Combine(AppContext.BaseDirectory, "locales")) is not { } locales)
        {
            MessageBox.Show("No locales found", "WPods", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var settings = new SettingsStore();
        var connections = new ConnectionManager(settings);
        var mic = new MicPipeline(connections);
        var keepAlive = new LowLatencyKeepAlive(connections);
        var ducker = new ConversationDucker(connections);
        var nearby = new NearbyService(connections);
        var app = new TrayApp(settings, connections, nearby, mic, keepAlive, ducker, locales, startHidden: startup);
        connections.Start();
        nearby.Start();
        Application.Run(app);

        activationWait.Unregister(null);
    }
}

using WPods.Core;

namespace WPods;

internal static class Program
{
    /// <summary>A second instance asked this one to show itself. Fires on a thread-pool thread.</summary>
    public static event Action? ActivationRequested;

    /// <summary>Launched by the Run key with --startup: start hidden in the tray.</summary>
    public static bool LaunchedAtStartup { get; private set; }

    [STAThread]
    private static void Main(string[] args)
    {
        using var instance = new Mutex(initiallyOwned: true, @"Local\WPods", out bool firstInstance);
        using var activation = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\WPods.Activate");
        if (!firstInstance)
        {
            activation.Set();
            return;
        }
        foreach (string arg in args)
        {
            if (arg.Equals("--startup", StringComparison.OrdinalIgnoreCase))
            {
                LaunchedAtStartup = true;
            }
        }
        RegisteredWaitHandle activationWait = ThreadPool.RegisterWaitForSingleObject(
            activation, static (_, _) => ActivationRequested?.Invoke(), null, Timeout.Infinite, executeOnlyOnce: false);

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var connections = new ConnectionManager(new SettingsStore());
        connections.Start();
        Application.Run(new ApplicationContext());

        activationWait.Unregister(null);
    }
}

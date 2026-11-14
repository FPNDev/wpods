using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using Microsoft.Win32;
using WPods.Service;

// The Windows service when run without arguments, otherwise a command for Setup or the SCM's recovery (see Usage).
return args.FirstOrDefault() switch
{
    "--install" => ServiceSetup.Install(start: !args.Contains("--no-start")),
    "--stop" => ServiceSetup.Stop(keepAdapter: true),
    "--uninstall" => ServiceSetup.Uninstall(),
    "--restore-native" => Engine.RestoreNative() ? 0 : 1,
    "--replug" => RadioDriver.Find(out _) is { } radio && RadioDriver.CyclePort(radio) ? 0 : 1,
    "--guard" => Guard(),
    "--status" => ServiceSetup.Status(),
    null => RunService(),
    _ => Usage(),
};

static int Guard()
{
    Log.Warn("guard", "the service keeps failing: giving the adapter back to Windows");
    Engine.GuardTripped = true;
    bool restored = Engine.RestoreNative();
    // Start the service again: it stays native and serves the app's status pipe, so the app can show it and retry.
    try
    {
        using var sc = new ServiceController(ServiceSetup.Name);
        sc.Start();
    }
    catch (InvalidOperationException e)
    {
        Log.Warn("guard", $"service not started: {e.Message}");
    }
    return restored ? 0 : 1;
}

static int RunService()
{
    ServiceBase.Run(new WPodsService());
    return 0;
}

static int Usage()
{
    Console.Error.WriteLine("usage: WPods.Service.exe [--install | --stop | --uninstall | --restore-native | --replug | --guard | --status]");
    return 2;
}

internal sealed class WPodsService : ServiceBase
{
    private Engine? _engine;

    public WPodsService()
    {
        ServiceName = ServiceSetup.Name;
        CanShutdown = true;
        CanStop = true;
        CanHandlePowerEvent = true;
    }

    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        Log.Info("service", $"power: {powerStatus}");
        switch (powerStatus)
        {
            case PowerBroadcastStatus.Suspend:
                _engine?.OnSuspend();
                break;
            case PowerBroadcastStatus.ResumeSuspend or PowerBroadcastStatus.ResumeAutomatic:
                _engine?.OnResume();
                break;
        }
        return true;
    }

    protected override void OnStart(string[] args)
    {
        Timing.BeginPeriod();
        Log.Info("service", $"start, version {typeof(WPodsService).Assembly.GetName().Version}");
        _engine = new Engine();
        _engine.Start();
    }

    protected override void OnStop()
    {
        Log.Info("service", "stop");
        RequestAdditionalTime(30000);
        _engine?.Stop(restoreNative: !Engine.KeepAdapterOnStop);
    }

    protected override void OnShutdown()
    {
        Log.Info("service", "shutdown");
        _engine?.Stop(restoreNative: false);
    }
}

internal static partial class Timing
{
    // 1 ms timer resolution for the mic's 1 ms isochronous pacing.
    public static void BeginPeriod() => _ = timeBeginPeriod(1);

    [LibraryImport("winmm.dll")]
    private static partial uint timeBeginPeriod(uint ms);
}

internal static class ServiceSetup
{
    public const string Name = "WPodsService";

    private static string Exe => Environment.ProcessPath!;

    public static int Install(bool start)
    {
        string bin = $"\"{Exe}\"";
        bool exists = ServiceController.GetServices().Any(s => s.ServiceName == Name);
        int r = Sc(exists ? "config" : "create", Name, "binPath=", bin, "start=", "auto", "obj=", "LocalSystem", "DisplayName=", "WPods Bluetooth Service");
        if (r != 0)
        {
            return r;
        }
        Sc("description", Name, "Runs the Bluetooth adapter for WPods: the AirPods control channel and the WPods microphone.");
        // Restart after 1 s, then 5 s; the third failure within 10 minutes gives the adapter back (--guard).
        if (Sc("failure", Name, "reset=", "600", "actions=", "restart/1000/restart/5000/run/0", "command=", $"{bin} --guard") != 0
            || Sc("failureflag", Name, "1") != 0)
        {
            return 1;
        }
        Engine.GuardTripped = false;
        Engine.Uninstalling = false;
        Engine.WaitsForRestart = !start;
        if (!start)
        {
            return 0;
        }
        try
        {
            using var sc = new ServiceController(Name);
            if (sc.Status != ServiceControllerStatus.Running)
            {
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            }
            return 0;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ServiceProcess.TimeoutException)
        {
            Console.Error.WriteLine($"start failed: {e.Message}");
            return 1;
        }
    }

    public static int Stop(bool keepAdapter)
    {
        try
        {
            using var sc = new ServiceController(Name);
            if (sc.Status is not (ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending))
            {
                Engine.KeepAdapterOnStop = keepAdapter;
                sc.Stop();
            }
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
            return 0;
        }
        catch (InvalidOperationException)
        {
            return 0; // not installed
        }
        catch (System.ServiceProcess.TimeoutException e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }

    public static int Uninstall()
    {
        Engine.Uninstalling = true;
        int stopped = Stop(keepAdapter: false);
        bool exists = ServiceController.GetServices().Any(s => s.ServiceName == Name);
        if (exists && Sc("delete", Name) != 0)
        {
            return 1;
        }
        if (stopped != 0)
        {
            return 1;
        }
        // The state stays while an adapter could not be given back: --restore-native needs it.
        if (!Engine.RestoreNative())
        {
            return 1;
        }
        Registry.LocalMachine.DeleteSubKeyTree(RadioDriver.StateKey, throwOnMissingSubKey: false);
        return 0;
    }

    public static int Status()
    {
        string service;
        try
        {
            using var sc = new ServiceController(Name);
            service = sc.Status.ToString();
        }
        catch (InvalidOperationException)
        {
            service = "not installed";
        }
        string? id = RadioDriver.StoredInstance;
        Console.WriteLine($"service: {service}");
        Console.WriteLine($"adapter: {id ?? "none"}{(id is not null && RadioDriver.IsPresent(id) ? RadioDriver.IsOnWinUsb(id) ? " (WinUSB)" : " (native driver)" : "")}");
        Console.WriteLine($"guard tripped: {Engine.GuardTripped}");
        return 0;
    }

    private static int Sc(params string[] args)
    {
        var psi = new ProcessStartInfo("sc.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using Process p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            Console.Error.WriteLine($"sc {args[0]}: {output}");
        }
        return p.ExitCode;
    }
}

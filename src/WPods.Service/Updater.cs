using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace WPods.Service;

internal enum UpdateStatus : byte
{
    Idle = 0,
    Checking = 1,
    Downloading = 2,
    /// <summary>Downloaded and verified; installs at the next start of the app or the service, or on "Update now".</summary>
    Ready = 3,
    /// <summary>Setup is starting; the service and the app close and come back.</summary>
    Installing = 4,
}

/// <summary>
/// Checks the latest GitHub release, downloads Setup only when its SHA-256 matches GitHub's digest, and installs it
/// silently as SYSTEM at the next service or app start, or at once on "Update now".
/// </summary>
internal sealed class Updater : IDisposable
{
    private const string LatestRelease = "https://api.github.com/repos/FPNDev/wpods/releases/latest";
    private const string StagedValue = "UpdateStaged";      // "version|file|sha256"
    private const string AttemptsValue = "UpdateAttempts";  // "version|count"
    private const string RelaunchValue = "RelaunchSessions"; // session ids where the app ran, "1,2"
    private const int MaxAttempts = 3;
    // The Setup of this build's architecture (build.ps1 -Arch); a release without it is no update for this PC.
    private static readonly string AssetName =
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "WPods-Setup-arm64.exe" : "WPods-Setup.exe";
    // A failed check (e.g. no network yet at Windows start) is retried after RetryDelay.
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(5);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);
    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WPods", "updates");

    private readonly Action _changed;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly Lock _lock = new();
    private readonly Timer _timer;
    private string? _etag;
    private Version? _available;
    private string? _assetUrl;
    private string? _digest;
    private string? _notesUrl;
    private bool _checked;
    private string _error = "";
    private UpdateStatus _status;
    private bool _working;
    private volatile bool _now;

    public Updater(Action changed)
    {
        _changed = changed;
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WPods", Current.ToString(3)));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        if (Staged() is { } staged && staged.Version > Current)
        {
            _available = staged.Version;
            _status = UpdateStatus.Ready;
        }
        if (AttemptsFor(_available) >= MaxAttempts)
        {
            _error = "D3";
        }
        _timer = new Timer(_ => _ = RunAsync(), null, TimeSpan.Zero, CheckInterval);
    }

    public static Version Current { get; } = Trim(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0));

    /// <summary>Auto-install new versions (Setup's checkbox, then the app's switch); on when not set.</summary>
    public static bool Auto
    {
        get => Registry.LocalMachine.OpenSubKey(RadioDriver.StateKey)?.GetValue("AutoUpdate") is not int v || v != 0;
        set
        {
            using RegistryKey key = Registry.LocalMachine.CreateSubKey(RadioDriver.StateKey);
            key.SetValue("AutoUpdate", value ? 1 : 0, RegistryValueKind.DWord);
        }
    }

    /// <summary>Update state for the app; Error is "" or D1-D3.</summary>
    public (UpdateStatus Status, bool Auto, bool Checked, Version? Available, string? NotesUrl, string Error) State
    {
        get
        {
            lock (_lock)
            {
                return (_status, Auto, _checked, _available, _notesUrl, _error);
            }
        }
    }

    public void SetAuto(bool on)
    {
        Auto = on;
        Log.Info("update", $"auto-update {(on ? "on" : "off")}");
        _changed();
        if (on)
        {
            CheckNow();
        }
    }

    public void CheckNow() => _ = RunAsync();

    /// <summary>"Update now": downloads if needed and installs at once (Bluetooth pauses for a few seconds).</summary>
    public void UpdateNow()
    {
        _now = true;
        _ = RunAsync();
    }

    /// <summary>Installs a downloaded update when the app starts; the app closes for Setup and is reopened.</summary>
    public void AppStarted()
    {
        if (Staged() is { } staged && staged.Version > Current && AttemptsFor(staged.Version) < MaxAttempts)
        {
            Log.Info("update", $"the app started with {staged.Version} downloaded: installing it now");
            UpdateNow();
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _http.Dispose();
    }

    /// <summary>
    /// Installs a newer staged update at service start; true means the adapter must not be taken over meanwhile.
    /// <paramref name="failed"/> runs on a thread-pool thread when Setup ends without replacing this service.
    /// </summary>
    public static bool InstallStagedAtStart(Action failed)
    {
        if (Staged() is not { } staged)
        {
            return false;
        }
        if (staged.Version <= Current)
        {
            Log.Info("update", $"{staged.Version} is installed; removing the staged copy");
            ClearStaged();
            ThreadPool.QueueUserWorkItem(_ => RemoveAfterSetup());
            return false;
        }
        if (AttemptsFor(staged.Version) >= MaxAttempts)
        {
            Log.Warn("update", $"{staged.Version} failed to install {MaxAttempts} times; not trying again until a newer release");
            return false;
        }
        // The app may already run (e.g. a service restart): it closes on "updating" and is reopened afterwards.
        RecordAppSessions();
        return Launch(staged, appRunning: false, () =>
        {
            RelaunchApp();
            failed();
        });
    }

    /// <summary>Reopens the app in the sessions it ran in before it closed for Setup.</summary>
    public static void RelaunchApp()
    {
        string? sessions;
        using (RegistryKey key = Registry.LocalMachine.CreateSubKey(RadioDriver.StateKey))
        {
            sessions = key.GetValue(RelaunchValue) as string;
            key.DeleteValue(RelaunchValue, throwOnMissingValue: false);
        }
        foreach (string s in (sessions ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (uint.TryParse(s, CultureInfo.InvariantCulture, out uint session))
            {
                SessionLauncher.Start(session, Path.Combine(AppContext.BaseDirectory, "WPods.exe"));
            }
        }
    }

    // One run at a time: check, download when auto-update is on or on "Update now", install at once only for the latter.
    private async Task RunAsync()
    {
        lock (_lock)
        {
            if (_working)
            {
                return;
            }
            _working = true;
        }
        try
        {
            RemoveLeftovers();
            SetStatus(UpdateStatus.Checking);
            try
            {
                await CheckAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (_now && Staged() is { } offline && offline.Version > Current && e is HttpRequestException
                or TaskCanceledException or IOException or JsonException or KeyNotFoundException or InvalidOperationException)
            {
                // "Update now" without a working check (e.g. offline): the verified update already downloaded installs.
                Log.Warn("update", $"check failed ({e.GetType().Name}: {e.Message}); installing the downloaded {offline.Version}");
                lock (_lock)
                {
                    _available = offline.Version;
                }
            }
            Version? version = State.Available;
            if (version is null || (!_now && !Auto))
            {
                return;
            }
            (Version Version, string File, string Sha256)? found = Staged();
            if (found is not { } current || current.Version != version)
            {
                SetStatus(UpdateStatus.Downloading);
                found = await DownloadAsync(version).ConfigureAwait(false);
            }
            if (found is not { } staged)
            {
                return;
            }
            if (!_now)
            {
                Log.Info("update", $"{version} ready; installs the next time WPods starts");
                return;
            }
            // Where the app runs is recorded before it hears "installing" (it closes at once).
            RecordAppSessions();
            SetStatus(UpdateStatus.Installing);
            if (!Launch(staged, appRunning: true, SetupFailed))
            {
                SetupFailed();
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException
            or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Log.Warn("update", $"failed: {e.GetType().Name}: {e.Message}; next try in {RetryDelay.TotalMinutes:0} minutes");
            if (State.Status == UpdateStatus.Downloading)
            {
                SetError("D1");
            }
            _timer.Change(RetryDelay, CheckInterval);
        }
        finally
        {
            lock (_lock)
            {
                _working = false;
                _now = false;
                if (_status != UpdateStatus.Installing)
                {
                    _status = Staged() is { } s && s.Version > Current ? UpdateStatus.Ready : UpdateStatus.Idle;
                }
            }
            _changed();
        }
    }

    // "Update now" did not install: the staged update stays for the next try, and the app that closed is reopened.
    private void SetupFailed()
    {
        Log.Warn("update", "the update was not installed");
        lock (_lock)
        {
            _status = Staged() is { } s && s.Version > Current ? UpdateStatus.Ready : UpdateStatus.Idle;
            _error = "D3";
        }
        _changed();
        RelaunchApp();
    }

    private async Task CheckAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestRelease);
        if (_etag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", _etag);
        }
        using HttpResponseMessage response = await _http.SendAsync(request).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotModified or HttpStatusCode.NotFound)
        {
            // Same answer as last time, or the project has no release yet.
            lock (_lock)
            {
                _checked = true;
            }
            return;
        }
        response.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        JsonElement root = doc.RootElement;
        // Tags are vR.Major.Minor.
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v'), out Version? version))
        {
            Log.Warn("update", $"check: tag \"{tag}\" is not a version");
            return;
        }
        version = Trim(version);
        if (Staged() is { } staged && staged.Version > version)
        {
            // The release it came from was withdrawn: it must not install.
            Log.Info("update", $"staged {staged.Version} is newer than the latest release {version}; removed");
            ClearStaged();
        }
        string? assetUrl = null;
        string? digest = null;
        foreach (JsonElement asset in root.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() == AssetName)
            {
                assetUrl = asset.GetProperty("browser_download_url").GetString();
                digest = asset.TryGetProperty("digest", out JsonElement d) ? d.GetString() : null;
            }
        }
        lock (_lock)
        {
            _checked = true;
            _etag = response.Headers.ETag?.ToString();
            _available = version > Current && assetUrl is not null ? version : null;
            _assetUrl = assetUrl;
            _digest = digest;
            _notesUrl = root.TryGetProperty("html_url", out JsonElement html) ? html.GetString() : null;
            if (_available is null)
            {
                _error = "";
            }
        }
        Log.Info("update", $"check: latest {version}, installed {Current}, asset {(assetUrl is null ? "missing" : "found")}");
    }

    // Downloads and verifies the Setup of `version`; null (and the error set) when that fails.
    private async Task<(Version Version, string File, string Sha256)?> DownloadAsync(Version version)
    {
        string? url;
        string? digest;
        lock (_lock)
        {
            url = _assetUrl;
            digest = _digest;
        }
        // GitHub reports "sha256:<hex>" for release assets. No digest, no install. HTTPS only.
        if (url is null || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || digest is null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            Log.Warn("update", $"download refused: url {url ?? "missing"}, digest {digest ?? "missing"}");
            SetError("D2");
            return null;
        }
        string expected = digest["sha256:".Length..].ToUpperInvariant();
        PrepareFolder();
        string file = Path.Combine(Folder, $"WPods-Setup-{version}.exe");
        using (HttpResponseMessage response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            await using FileStream target = new(file, FileMode.Create, FileAccess.Write, FileShare.None);
            await response.Content.CopyToAsync(target).ConfigureAwait(false);
        }
        if (Hash(file) != expected)
        {
            Log.Warn("update", $"download of {version} does not match its digest");
            File.Delete(file);
            SetError("D2");
            return null;
        }
        using (RegistryKey key = Registry.LocalMachine.CreateSubKey(RadioDriver.StateKey))
        {
            key.SetValue(StagedValue, $"{version}|{file}|{expected}");
        }
        Log.Info("update", $"downloaded {version}, digest verified");
        SetError("");
        return (version, file, expected);
    }

    // The file stays open with read-only sharing from the hash check until Setup runs, so it cannot be swapped.
    // `failed` runs on a thread-pool thread when Setup ends with this service still running.
    private static bool Launch((Version Version, string File, string Sha256) staged, bool appRunning, Action failed)
    {
        try
        {
            using var hold = new FileStream(staged.File, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (Hash(hold) != staged.Sha256)
            {
                Log.Warn("update", $"staged {staged.Version} changed on disk; removed");
                hold.Dispose();
                ClearStaged();
                return false;
            }
            if (appRunning)
            {
                WaitForAppsToClose();
            }
            int attempts = AttemptsFor(staged.Version) + 1;
            using (RegistryKey key = Registry.LocalMachine.CreateSubKey(RadioDriver.StateKey))
            {
                key.SetValue(AttemptsValue, $"{staged.Version}|{attempts}");
            }
            string log = Path.Combine(Path.GetDirectoryName(staged.File)!, $"setup-{staged.Version}.log");
            var start = new ProcessStartInfo(staged.File) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string a in (string[])["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/FROMSERVICE=1", "/LOG=" + log])
            {
                start.ArgumentList.Add(a);
            }
            Process? setup = Process.Start(start);
            if (setup is null)
            {
                return false;
            }
            Log.Info("update", $"Setup {staged.Version} started (attempt {attempts}), log {log}");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                using (setup)
                {
                    setup.WaitForExit();
                    Log.Warn("update", $"Setup {staged.Version} ended (exit code {setup.ExitCode}) without replacing this service");
                }
                failed();
            });
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception)
        {
            Log.Error("update", $"Setup {staged.Version} could not start", e);
            return false;
        }
    }

    // The sessions the app runs in: the new service reopens it there.
    private static void RecordAppSessions()
    {
        var sessions = new List<int>();
        foreach (Process p in Process.GetProcessesByName("WPods"))
        {
            using (p)
            {
                if (!sessions.Contains(p.SessionId))
                {
                    sessions.Add(p.SessionId);
                }
            }
        }
        using RegistryKey key = Registry.LocalMachine.CreateSubKey(RadioDriver.StateKey);
        key.SetValue(RelaunchValue, string.Join(',', sessions));
    }

    // The app closes when it sees the Installing state; waits up to 10 s for that.
    private static void WaitForAppsToClose()
    {
        for (int i = 0; i < 100; i++)
        {
            Process[] left = Process.GetProcessesByName("WPods");
            foreach (Process p in left)
            {
                p.Dispose();
            }
            if (left.Length == 0)
            {
                return;
            }
            Thread.Sleep(100);
        }
        Log.Warn("update", "the app did not close within 10 s; Setup closes it");
    }

    private static (Version Version, string File, string Sha256)? Staged()
    {
        string[]? parts = (Registry.LocalMachine.OpenSubKey(RadioDriver.StateKey)?.GetValue(StagedValue) as string)?.Split('|');
        return parts is { Length: 3 } && Version.TryParse(parts[0], out Version? v) && File.Exists(parts[1]) ? (v, parts[1], parts[2]) : null;
    }

    private static int AttemptsFor(Version? version)
    {
        string[]? parts = (Registry.LocalMachine.OpenSubKey(RadioDriver.StateKey)?.GetValue(AttemptsValue) as string)?.Split('|');
        return version is not null && parts is { Length: 2 } && parts[0] == version.ToString() && int.TryParse(parts[1], out int n) ? n : 0;
    }

    private static void ClearStaged()
    {
        using RegistryKey key = Registry.LocalMachine.CreateSubKey(RadioDriver.StateKey);
        key.DeleteValue(StagedValue, throwOnMissingValue: false);
        key.DeleteValue(AttemptsValue, throwOnMissingValue: false);
        // Nothing is staged now: every Setup file goes.
        RemoveLeftovers();
    }

    // Deletes Setup files other than the staged one; a running one stays until it has exited.
    private static void RemoveLeftovers()
    {
        if (!FolderIsOwn())
        {
            return;
        }
        string? keep = Staged()?.File;
        foreach (string f in Directory.Exists(Folder) ? Directory.GetFiles(Folder, "*.exe") : [])
        {
            if (string.Equals(f, keep, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            try
            {
                File.Delete(f);
                Log.Info("update", $"removed {Path.GetFileName(f)}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Info("update", $"{Path.GetFileName(f)} not removed yet: {e.Message}");
            }
        }
    }

    // The Setup that installed this version may still be finishing: its file goes once every Setup there has exited.
    private static void RemoveAfterSetup()
    {
        foreach (Process p in Process.GetProcesses())
        {
            using (p)
            {
                string? path;
                try
                {
                    path = p.MainModule?.FileName;
                }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException)
                {
                    continue; // protected or already gone
                }
                if (path is not null && path.StartsWith(Folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    p.WaitForExit();
                }
            }
        }
        RemoveLeftovers();
    }

    /// <summary>
    /// Limits %ProgramData%\WPods to SYSTEM and Administrators (users read only), without ProgramData's inheritance,
    /// which lets any user plant a link that would redirect this service's deletes. A link in its place is left alone.
    /// </summary>
    public static void ProtectDataFolder()
    {
        try
        {
            var root = new DirectoryInfo(Path.GetDirectoryName(Folder)!);
            root.Create();
            if ((root.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                Log.Warn("update", $"{root.FullName} is a link: not protected");
                return;
            }
            var sid = (WellKnownSidType t) => new SecurityIdentifier(t, null);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            const InheritanceFlags All = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(sid(WellKnownSidType.LocalSystemSid), FileSystemRights.FullControl, All, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(sid(WellKnownSidType.BuiltinAdministratorsSid), FileSystemRights.FullControl, All, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(sid(WellKnownSidType.BuiltinUsersSid), FileSystemRights.ReadAndExecute, All, PropagationFlags.None, AccessControlType.Allow));
            root.SetAccessControl(security);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Warn("update", $"{Path.GetDirectoryName(Folder)} not protected: {e.Message}");
        }
    }

    // The update folder and its parent are real folders, not links: only then are files in it deleted.
    private static bool FolderIsOwn()
    {
        foreach (string dir in (string[])[Path.GetDirectoryName(Folder)!, Folder])
        {
            if (new DirectoryInfo(dir) is { Exists: true } d && (d.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                Log.Warn("update", $"{dir} is a link: update files not touched");
                return false;
            }
        }
        return true;
    }

    // %ProgramData%\WPods\updates readable and writable only by SYSTEM and Administrators (other users could otherwise
    // create files there). A folder someone else made first is replaced; a link there is removed (only the link).
    private static void PrepareFolder()
    {
        var sid = (WellKnownSidType t) => new SecurityIdentifier(t, null);
        var root = new DirectoryInfo(Path.GetDirectoryName(Folder)!);
        root.Create();
        if ((root.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"{root.FullName} is a link");
        }
        if (new DirectoryInfo(Folder) is { Exists: true } existing && (existing.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            Log.Warn("update", $"{Folder} was a link; removed");
            existing.Delete();
        }
        if (Directory.Exists(Folder))
        {
            var owner = (SecurityIdentifier?)new DirectoryInfo(Folder).GetAccessControl().GetOwner(typeof(SecurityIdentifier));
            if (owner == sid(WellKnownSidType.LocalSystemSid) || owner == sid(WellKnownSidType.BuiltinAdministratorsSid))
            {
                return;
            }
            Directory.Delete(Folder, recursive: true);
        }
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (WellKnownSidType t in (WellKnownSidType[])[WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid])
        {
            security.AddAccessRule(new FileSystemAccessRule(sid(t), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }
        new DirectoryInfo(Folder).Create(security);
    }

    private static string Hash(string file)
    {
        using FileStream s = File.OpenRead(file);
        return Hash(s);
    }

    private static string Hash(FileStream s)
    {
        s.Position = 0;
        return Convert.ToHexString(SHA256.HashData(s));
    }

    private static Version Trim(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    private void SetStatus(UpdateStatus status)
    {
        lock (_lock)
        {
            _status = status;
        }
        _changed();
    }

    private void SetError(string code)
    {
        lock (_lock)
        {
            _error = code;
        }
        _changed();
    }
}

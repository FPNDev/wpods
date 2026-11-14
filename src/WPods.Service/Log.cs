using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace WPods.Service;

/// <summary>
/// The service log, one file capped at 500 MB. Callers only enqueue and a background thread writes in batches, so
/// logging never slows the radio; it never throws, an unwritable file drops the entries.
/// </summary>
internal static class Log
{
    private const long MaxBytes = 500L * 1024 * 1024;
    private const int WriteIntervalMs = 200;

    private readonly record struct Entry(long UtcTicks, string Level, string Area, string Text);

    private static readonly ConcurrentQueue<Entry> Queue = new();
    private static readonly AutoResetEvent Pending = new(initialState: false);
    private static readonly Lock WriteLock = new();
    private static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WPods", "logs");
    private static readonly string FilePath = Path.Combine(Dir, "service.log");

    // Under WriteLock.
    private static StreamWriter? _writer;

    static Log()
    {
        var thread = new Thread(WriteLoop) { IsBackground = true, Name = "service log", Priority = ThreadPriority.BelowNormal };
        thread.Start();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => Flush();
    }

    public static void Info(string area, string text) => Add("INFO", area, text);

    public static void Warn(string area, string text) => Add("WARN", area, text);

    public static void Error(string area, string text, Exception e) => Add("ERROR", area, $"{text}: {e}");

    /// <summary>Writes everything queued so far. Any thread.</summary>
    public static void Flush()
    {
        lock (WriteLock)
        {
            Drain();
        }
    }

    private static void Add(string level, string area, string text)
    {
        Queue.Enqueue(new Entry(DateTime.UtcNow.Ticks, level, area, text));
        Pending.Set();
    }

    private static void WriteLoop()
    {
        while (true)
        {
            Pending.WaitOne();
            Thread.Sleep(WriteIntervalMs);
            Flush();
        }
    }

    // Under WriteLock.
    private static void Drain()
    {
        if (Queue.IsEmpty)
        {
            return;
        }
        StreamWriter? writer = Open();
        if (writer is null)
        {
            Queue.Clear();
            return;
        }
        try
        {
            while (Queue.TryDequeue(out Entry e))
            {
                DateTime local = new DateTime(e.UtcTicks, DateTimeKind.Utc).ToLocalTime();
                writer.Write(string.Create(CultureInfo.InvariantCulture, $"{local:yyyy-MM-dd HH:mm:ss.fff} {e.Level,-5} [{e.Area}] {e.Text}\n"));
            }
            writer.Flush();
            if (writer.BaseStream.Length > MaxBytes)
            {
                Close();
                LogFile.CutOldest(FilePath, MaxBytes);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // Disk full or file gone: this batch is dropped, the file reopened next time.
            Close();
        }
    }

    // Under WriteLock. Null when the file cannot be opened now (retried on the next batch).
    private static StreamWriter? Open()
    {
        if (_writer is not null)
        {
            return _writer;
        }
        try
        {
            Directory.CreateDirectory(Dir);
            var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 64 * 1024);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Locked or not writable: this batch is dropped.
        }
        return _writer;
    }

    private static void Close()
    {
        try
        {
            _writer?.Dispose();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // Already broken; nothing to keep.
        }
        _writer = null;
    }
}

/// <summary>Size limit of a log file that stays one file.</summary>
internal static class LogFile
{
    /// <summary>
    /// Keeps the newest ~90 % of <paramref name="maxBytes"/> from a line start; the file must not be open for writing.
    /// </summary>
    public static void CutOldest(string path, long maxBytes)
    {
        string temp = path + ".tmp";
        using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            source.Position = Math.Max(0, source.Length - (maxBytes / 10 * 9));
            // Start at a line.
            int b;
            while ((b = source.ReadByte()) >= 0 && b != '\n')
            {
            }
            using var target = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);
            source.CopyTo(target);
        }
        File.Move(temp, path, overwrite: true);
    }
}

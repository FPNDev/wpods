using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace WPods.Core;

/// <summary>
/// Always-on diagnostic log: %LOCALAPPDATA%\WPods\logs\wpods.log, one line per entry
/// ("yyyy-MM-dd HH:mm:ss.fff [tid] LEVEL category: message", local time, UTF-8). Callers only enqueue and signal;
/// a background thread wakes on the signal, waits 500 ms to batch, then writes (idle: no wakeups). Above 2 MB the file
/// is renamed to wpods.1.log (one old file kept).
/// Logging never throws: an unwritable file drops the entries.
/// </summary>
internal static class Log
{
    private enum Level : byte
    {
        Info,
        Warn,
        Error,
        Rx,
        Tx,
    }

    private readonly record struct Entry(long UtcTicks, int Thread, Level Level, string Category, string Message);

    private const long MaxBytes = 2 * 1024 * 1024;
    private const int WriteIntervalMs = 500;

    private static readonly ConcurrentQueue<Entry> Queue = new();
    // Set after every enqueue; an entry added during a drain leaves it set, so the next round writes it.
    private static readonly AutoResetEvent Pending = new(initialState: false);
    private static readonly Lock WriteLock = new();
    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WPods", "logs");
    private static readonly string FilePath = Path.Combine(Folder, "wpods.log");
    private static readonly string OldPath = Path.Combine(Folder, "wpods.1.log");

    // Under WriteLock.
    private static StreamWriter? _writer;

    static Log()
    {
        var thread = new Thread(WriteLoop) { IsBackground = true, Name = "WPods log", Priority = ThreadPriority.BelowNormal };
        thread.Start();
    }

    public static void Info(string category, string message) => Add(Level.Info, category, message);

    public static void Warn(string category, string message) => Add(Level.Warn, category, message);

    public static void Error(string category, string message, Exception? ex = null) =>
        Add(Level.Error, category, ex is null ? message : message + " | " + ex);

    /// <summary>One AAP packet as hex (RX = inbound, TX = outbound), category "aap".</summary>
    public static void Packet(bool outbound, ReadOnlySpan<byte> packet) =>
        Add(outbound ? Level.Tx : Level.Rx, "aap", Convert.ToHexString(packet));

    /// <summary>Writes everything queued so far (exit, fatal exceptions). Any thread.</summary>
    public static void Flush()
    {
        lock (WriteLock)
        {
            Drain();
        }
    }

    private static void Add(Level level, string category, string message)
    {
        Queue.Enqueue(new Entry(DateTime.UtcNow.Ticks, Environment.CurrentManagedThreadId, level, category, message));
        Pending.Set();
    }

    private static void WriteLoop()
    {
        while (true)
        {
            Pending.WaitOne();
            Thread.Sleep(WriteIntervalMs);
            if (!Queue.IsEmpty)
            {
                Flush();
            }
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
        Span<char> time = stackalloc char[32];
        try
        {
            while (Queue.TryDequeue(out Entry entry))
            {
                if (writer is null)
                {
                    continue;
                }
                DateTime local = new DateTime(entry.UtcTicks, DateTimeKind.Utc).ToLocalTime();
                local.TryFormat(time, out int length, "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                writer.Write(time[..length]);
                writer.Write(" [");
                writer.Write(entry.Thread);
                writer.Write("] ");
                writer.Write(entry.Level switch
                {
                    Level.Info => "INFO ",
                    Level.Warn => "WARN ",
                    Level.Error => "ERROR ",
                    Level.Rx => "RX ",
                    _ => "TX ",
                });
                writer.Write(entry.Category);
                writer.Write(": ");
                writer.Write(entry.Message);
                writer.Write('\n');
            }
            if (writer is null)
            {
                return;
            }
            writer.Flush();
            if (writer.BaseStream.Length > MaxBytes)
            {
                Rotate(writer);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // Disk full or file gone: drop this batch, reopen next time.
            Close();
        }
    }

    // Under WriteLock. Null when the file cannot be opened now (retried on the next drain).
    private static StreamWriter? Open()
    {
        if (_writer is not null)
        {
            return _writer;
        }
        try
        {
            Directory.CreateDirectory(Folder);
            var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 16 * 1024);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Locked or not writable: this batch is dropped.
        }
        return _writer;
    }

    private static void Rotate(StreamWriter writer)
    {
        writer.Dispose();
        _writer = null;
        File.Move(FilePath, OldPath, overwrite: true);
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

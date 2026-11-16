using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace WPods.Core;

/// <summary>
/// Always-on diagnostic log in %LOCALAPPDATA%\WPods\logs, batched by a background thread and capped at 100 MB.
/// Never throws: entries that cannot be written are dropped.
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

    private const long MaxBytes = 10L * 1024 * 1024;
    private const int WriteIntervalMs = 500;

    private static readonly ConcurrentQueue<Entry> Queue = new();
    // Set after every enqueue; an entry added during a drain leaves it set, so the next round writes it.
    private static readonly AutoResetEvent Pending = new(initialState: false);
    private static readonly Lock WriteLock = new();
    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WPods", "logs");
    private static readonly string FilePath = Path.Combine(Folder, "wpods.log");

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

    public static void Packet(bool outbound, ReadOnlySpan<byte> packet) =>
        Add(outbound ? Level.Tx : Level.Rx, "aap", Convert.ToHexString(packet));

    /// <summary>Writes all queued entries now; for exit and fatal exceptions.</summary>
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
        Span<char> time = stackalloc char[32];
        try
        {
            while (Queue.TryDequeue(out Entry entry))
            {
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
            writer.Flush();
            if (writer.BaseStream.Length > MaxBytes)
            {
                Close();
                CutOldest();
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

    // Under WriteLock, file closed: keeps the newest half of MaxBytes, starting at a line, so the cut runs rarely.
    private static void CutOldest()
    {
        string temp = FilePath + ".tmp";
        using (var source = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            source.Position = Math.Max(0, source.Length - (MaxBytes / 2));
            int b;
            while ((b = source.ReadByte()) >= 0 && b != '\n')
            {
            }
            using var target = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);
            source.CopyTo(target);
        }
        File.Move(temp, FilePath, overwrite: true);
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

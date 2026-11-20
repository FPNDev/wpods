using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace WPods.Service;

/// <summary>The service's named pipes (see <see cref="Protocol"/>): message mode, local interactive users only.</summary>
internal static class Pipes
{
    public static NamedPipeServerStream Create(string name, int maxInstances, int outBuffer)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, maxInstances, PipeTransmissionMode.Message, PipeOptions.Asynchronous, 1 << 17, outBuffer, security);
    }

    /// <summary>Reads one whole message into <paramref name="buffer"/>; 0 when the client closed. Throws when it does not fit.</summary>
    public static async Task<int> ReadMessageAsync(PipeStream pipe, byte[] buffer, CancellationToken cancel)
    {
        int total = 0;
        do
        {
            if (total == buffer.Length)
            {
                throw new InvalidDataException("message too large");
            }
            int n = await pipe.ReadAsync(buffer.AsMemory(total), cancel).ConfigureAwait(false);
            if (n == 0)
            {
                return 0;
            }
            total += n;
        }
        while (!pipe.IsMessageComplete);
        return total;
    }

    /// <summary>
    /// Accepts clients until <paramref name="stop"/>; <paramref name="outBuffer"/> is what a write may queue before it waits.
    /// </summary>
    public static async Task AcceptAsync(string name, int maxInstances, Func<NamedPipeServerStream, Task> serve, CancellationToken stop, int outBuffer = 1 << 17)
    {
        while (!stop.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = Create(name, maxInstances, outBuffer);
            }
            catch (IOException)
            {
                // All instances busy (the single-instance mic pipe is in use): try again shortly.
                await Task.Delay(1000, stop).ConfigureAwait(false);
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                // Another program holds this pipe name with its own access rules: retry until it closes it.
                Log.Warn("pipe", $"{name}: the pipe name is taken by another program; retrying");
                await Task.Delay(5000, stop).ConfigureAwait(false);
                continue;
            }
            try
            {
                await pipe.WaitForConnectionAsync(stop).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or IOException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                if (stop.IsCancellationRequested)
                {
                    return;
                }
                continue;
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    await serve(pipe).ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidDataException or OperationCanceledException or InvalidOperationException)
                {
                    Log.Info("pipe", $"{name}: client ended: {e.Message}");
                }
                finally
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                }
            }, CancellationToken.None);
        }
    }
}

/// <summary>\\.\pipe\WPods.Aap: one connection = one AAP channel to one AirPods.</summary>
internal sealed class AapPipeServer(HciProxy hci)
{
    // A small outgoing buffer (~12 mic packets): an app reading slower than real time makes the writes wait, so the
    // channel's bounded mic queue drops instead of the pipe holding seconds of audio (see AapChannel.Deliver).
    public Task RunAsync(CancellationToken stop) => Pipes.AcceptAsync(Protocol.AapPipe, NamedPipeServerStream.MaxAllowedServerInstances, p => ServeAsync(p, stop), stop, outBuffer: 4096);

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken stop)
    {
        var buffer = new byte[Protocol.MaxAapPacket + 16];
        int n;
        using (var hello = CancellationTokenSource.CreateLinkedTokenSource(stop))
        {
            hello.CancelAfter(TimeSpan.FromSeconds(5));
            n = await Pipes.ReadMessageAsync(pipe, buffer, hello.Token).ConfigureAwait(false);
        }
        if (n != 10 || buffer[0] != (byte)'A' || buffer[1] != (byte)'P')
        {
            Log.Warn("aap", $"bad first message ({n} bytes)");
            return;
        }
        ulong address = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(2));
        // Reads on during the open, so an app that goes away cancels it and leaves the channel free.
        using var gone = CancellationTokenSource.CreateLinkedTokenSource(stop);
        Task<int> next = Pipes.ReadMessageAsync(pipe, buffer, stop);
        _ = next.ContinueWith(static (t, state) =>
        {
            _ = t.Exception; // observed here: the read may end after this method returned
            if (t.IsFaulted || t.IsCanceled || t.Result == 0)
            {
                try
                {
                    ((CancellationTokenSource)state!).Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }, gone, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        (int error, AapChannel? ch) = await hci.OpenAsync(address, gone.Token).ConfigureAwait(false);
        var reply = new byte[5];
        reply[0] = Protocol.AapOpenResult;
        BinaryPrimitives.WriteInt32LittleEndian(reply.AsSpan(1), error);
        try
        {
            await pipe.WriteAsync(reply, stop).ConfigureAwait(false);
        }
        catch
        {
            if (ch is not null)
            {
                hci.Close(ch);
            }
            throw;
        }
        if (ch is null)
        {
            return;
        }
        // Inbound packets and the end of the channel go to the app; the pipe closes after the last message. A thread of
        // its own: on the thread pool a mic packet sometimes waited tens of ms for a worker.
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            try
            {
                foreach (byte[] m in ch.Messages.GetConsumingEnumerable())
                {
                    pipe.Write(m);
                    ch.Sent(m);
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // The app closed the pipe.
            }
            pipe.Dispose();
            written.SetResult();
        }) { IsBackground = true, Name = "aap pipe", Priority = ThreadPriority.Highest }.Start();
        try
        {
            while (true)
            {
                n = await next.ConfigureAwait(false);
                if (n == 0 || n > Protocol.MaxAapPacket)
                {
                    break;
                }
                if (buffer[0] is Protocol.VoiceControl or Protocol.VoiceDownlink or Protocol.VoiceSilence)
                {
                    hci.OnVoiceMessage(ch, buffer.AsSpan(0, n));
                }
                else if (!hci.Send(ch, buffer.AsSpan(0, n)))
                {
                    break;
                }
                next = Pipes.ReadMessageAsync(pipe, buffer, stop);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidDataException or OperationCanceledException)
        {
            // Pipe closed by either side.
        }
        finally
        {
            hci.Close(ch);
            await written.Task.ConfigureAwait(false);
        }
    }
}

/// <summary>\\.\pipe\WPods.Mic: the app's mic PCM and pair, and the capture state back. One client at a time.</summary>
internal sealed class MicPipeServer(Engine engine)
{
    private readonly Lock _lock = new();
    private NamedPipeServerStream? _client;

    public Task RunAsync(CancellationToken stop) => Pipes.AcceptAsync(Protocol.MicPipe, 1, p => ServeAsync(p, stop), stop);

    /// <summary>Tells the connected app whether an app records from the mic.</summary>
    public void NotifyCapture(bool capturing)
    {
        lock (_lock)
        {
            if (_client is { } c)
            {
                TrySend(c, capturing);
            }
        }
    }

    private static void TrySend(NamedPipeServerStream pipe, bool capturing)
    {
        try
        {
            pipe.Write([Protocol.MicCapture, capturing ? (byte)1 : (byte)0]);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The read loop sees the closed pipe.
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken stop)
    {
        lock (_lock)
        {
            _client = pipe;
            TrySend(pipe, engine.MicCapturing);
        }
        Log.Info("mic", "app connected");
        try
        {
            var buffer = new byte[1 + 8 + Protocol.MaxPcm];
            while (true)
            {
                int n = await Pipes.ReadMessageAsync(pipe, buffer, stop).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }
                if (buffer[0] == Protocol.MicPcm && n >= 9)
                {
                    engine.MicPush(BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(1)), buffer.AsSpan(9, n - 9));
                }
                else if (buffer[0] == Protocol.MicDevice && n >= 10)
                {
                    engine.SetMic(BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(1)), Encoding.UTF8.GetString(buffer, 10, n - 10),
                        reported: (buffer[9] & Protocol.MicNameReported) != 0, hd: (buffer[9] & Protocol.MicHdVoice) != 0);
                }
            }
        }
        finally
        {
            lock (_lock)
            {
                if (_client == pipe)
                {
                    _client = null;
                }
            }
            Log.Info("mic", "app disconnected");
        }
    }
}

/// <summary>\\.\pipe\WPods.Service: the service state and updates for the app, and the app's requests.</summary>
internal sealed class StatusPipeServer(Engine engine)
{
    private readonly Lock _lock = new();
    private readonly List<NamedPipeServerStream> _clients = [];

    public Task RunAsync(CancellationToken stop) => Pipes.AcceptAsync(Protocol.StatusPipe, NamedPipeServerStream.MaxAllowedServerInstances, p => ServeAsync(p, stop), stop);

    public void Broadcast(ServiceState state, string detail) => BroadcastMessage(Message(state, detail));

    public void BroadcastUpdate() => BroadcastMessage(UpdateMessage());

    private void BroadcastMessage(byte[] m)
    {
        lock (_lock)
        {
            foreach (NamedPipeServerStream c in _clients)
            {
                TrySend(c, m);
            }
        }
    }

    private byte[] UpdateMessage()
    {
        if (engine.Updater is not { } updater)
        {
            return [Protocol.UpdateInfo, 0, Updater.Auto ? (byte)1 : (byte)0, .. Encoding.UTF8.GetBytes($"{Updater.Current}\0\0\0")];
        }
        (UpdateStatus status, bool auto, bool isChecked, Version? available, string? notes, string error) = updater.State;
        byte flags = (byte)((auto ? 1 : 0) | (isChecked ? 2 : 0));
        return [Protocol.UpdateInfo, (byte)status, flags, .. Encoding.UTF8.GetBytes($"{Updater.Current}\0{available}\0{notes}\0{error}")];
    }

    private static byte[] Message(ServiceState state, string detail)
    {
        var m = new byte[2 + Encoding.UTF8.GetByteCount(detail)];
        m[0] = Protocol.Status;
        m[1] = (byte)state;
        Encoding.UTF8.GetBytes(detail, m.AsSpan(2));
        return m;
    }

    private static void TrySend(NamedPipeServerStream pipe, byte[] m)
    {
        try
        {
            pipe.Write(m);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The read loop sees the closed pipe.
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken stop)
    {
        lock (_lock)
        {
            _clients.Add(pipe);
            (ServiceState state, string detail) = engine.State;
            TrySend(pipe, Message(state, detail));
            TrySend(pipe, UpdateMessage());
        }
        try
        {
            var buffer = new byte[64];
            while (true)
            {
                int n = await Pipes.ReadMessageAsync(pipe, buffer, stop).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }
                switch (buffer[0])
                {
                    case Protocol.Retry:
                        engine.Retry();
                        break;
                    case Protocol.SetAutoUpdate when n >= 2:
                        engine.Updater?.SetAuto(buffer[1] != 0);
                        break;
                    case Protocol.CheckUpdate:
                        engine.Updater?.CheckNow();
                        break;
                    case Protocol.UpdateNow:
                        engine.Updater?.UpdateNow();
                        break;
                    case Protocol.AppStarted:
                        engine.AppStarted();
                        break;
                    case Protocol.SetHdVoice when n >= 10:
                        engine.SetHdVoice(BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(1)), buffer[9] != 0);
                        break;
                }
            }
        }
        finally
        {
            lock (_lock)
            {
                _clients.Remove(pipe);
            }
        }
    }
}

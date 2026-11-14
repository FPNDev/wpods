using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;

namespace WPods.Service;

/// <summary>
/// HCI proxy between Windows' Bluetooth stack and the controller. Hides the service's own AAP channels (PSM 0x1001,
/// local CIDs from 0x7F40, signaling ids from 0xC0), its Hands-Free decline (RFCOMM, same ranges) and their
/// completed-packet counts from Windows; outbound ACL from both sides shares the controller buffers, and ours never goes
/// between fragments of a Windows L2CAP PDU.
/// </summary>
internal sealed class HciProxy
{
    private const ushort AapPsm = 0x1001;
    private const ushort FirstCid = 0x7F40;
    private const ushort OurMtu = Protocol.MaxAapPacket;
    // Controller ACL buffers our own packets may hold at once.
    private const int MaxOursOutstanding = 1;
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(10);
    private const ushort RfcommPsm = 0x0003;
    // The whole Hands-Free decline normally takes well under a second; past this it is cut short.
    private static readonly TimeSpan DeclineTimeout = TimeSpan.FromSeconds(5);
    // Hands-Free DLC up and no AT command yet: closing it ends the AirPods wait
    private static readonly TimeSpan SilentTimeout = TimeSpan.FromSeconds(1);
    private static readonly byte[] AtError = "\r\nERROR\r\n"u8.ToArray();

    // Guards all state below; Monitor.Wait/PulseAll on it for controller buffers.
    private readonly object _gate = new();
    private readonly Dictionary<int, Link> _links = [];
    private Action<Urb>? _send;
    private int _aclBuffers;
    private int _aclMaxLength = 1021;
    private bool _leShared;
    private int _outstanding;
    private ushort _nextCid = FirstCid;

    private readonly byte[] _event = new byte[257];
    private int _eventHave;
    private readonly ByteQueue _eventsOut = new();
    private readonly byte[] _aclHead = new byte[16];
    private int _aclHeadHave;
    private Route _aclRoute;
    private int _aclRemaining;
    private int _aclHandle;
    private byte[] _aclOurs = [];
    private int _aclOursHave;
    private readonly ByteQueue _aclOut = new();
    // Bulk IN is parsed as ACL only after an HCI_Reset completed.
    private bool _aclFramed;
    // Send time of Windows' unanswered command (0 = none pending) and the controller's last reported version.
    private long _commandSentAt;
    private byte[]? _localVersion;
    // The controller's BD_ADDR (Read_BD_ADDR answer); 0 until Windows asked for it.
    private ulong _localAddress;

    /// <summary>
    /// The controller lost its state: Hardware_Error, or a changed local version (firmware reloaded or lost).
    /// Raised on a thread-pool thread.
    /// </summary>
    public event Action<string>? ControllerTrouble;

    // Open SCO/eSCO (call audio) links: handle -> device address.
    private readonly Dictionary<int, ulong> _scoLinks = [];

    /// <summary>The last SCO/eSCO link closed. Raised under the proxy lock on the event pipe thread; return at once.</summary>
    public event Action? CallAudioEnded;

    /// <summary>A SCO/eSCO link came up. Raised under the proxy lock on the event pipe thread.</summary>
    public event Action? CallAudioStarted;

    public bool CallAudioActive
    {
        get
        {
            lock (_gate)
            {
                return _scoLinks.Count > 0;
            }
        }
    }

    /// <summary>How long Windows' last HCI command has waited for its answer (0 when answered).</summary>
    public long UnansweredMs
    {
        get
        {
            long sent = Volatile.Read(ref _commandSentAt);
            return sent == 0 ? 0 : Environment.TickCount64 - sent;
        }
    }

    /// <summary>Sets the radio link that sends the proxy's packets; null when the radio is detached.</summary>
    public void SetSender(Action<Urb>? send)
    {
        lock (_gate)
        {
            _send = send;
            ResetLocked(send is null ? "radio detached" : "radio attached", dropQueued: true);
        }
        // A command sent to the previous radio will never be answered.
        Volatile.Write(ref _commandSentAt, 0);
    }

    /// <summary>HCI command from Windows (control OUT, class request).</summary>
    public void OnCommand(ReadOnlySpan<byte> command)
    {
        if (command.Length < 3)
        {
            return;
        }
        ushort opcode = BinaryPrimitives.ReadUInt16LittleEndian(command);
        // Vendor commands (OGF 0x3F) may be answered outside the event pipe (Intel's bootloader answers on the bulk pipe,
        // MediaTek's WMT commands through a vendor request): they are not timed.
        if (opcode >> 10 != 0x3F)
        {
            Volatile.Write(ref _commandSentAt, Environment.TickCount64);
        }
        // First 32 parameter bytes of every command, vendor ones included; never key material.
        Log.Info("hci", $"command 0x{opcode:X4} {(CarriesKey(opcode) ? "(parameters not logged: they carry a key)" : Convert.ToHexString(command[3..Math.Min(command.Length, 35)]))}");
        if (opcode is 0x0428 or 0x043D && command.Length >= 5)
        {
            // (Enhanced_)Setup_Synchronous_Connection on an ACL link: Windows starts call audio.
            lock (_gate)
            {
                if (_links.TryGetValue(BinaryPrimitives.ReadUInt16LittleEndian(command[3..]) & 0x0FFF, out Link? link))
                {
                    SetCallAudioLocked(link, true, "Windows sets up call audio");
                }
            }
        }
        else if (opcode is 0x0429 or 0x043E && command.Length >= 9)
        {
            // (Enhanced_)Accept_Synchronous_Connection_Request: Windows accepts the device's call audio.
            lock (_gate)
            {
                if (FindLinkLocked(ReadAddress(command[3..])) is { } link)
                {
                    SetCallAudioLocked(link, true, "Windows accepts call audio");
                }
            }
        }
        else if (opcode == 0x042A && command.Length >= 9)
        {
            // Reject_Synchronous_Connection_Request: no call audio after all.
            lock (_gate)
            {
                ulong address = ReadAddress(command[3..]);
                if (FindLinkLocked(address) is { } link)
                {
                    SetCallAudioLocked(link, HasScoLocked(address), "Windows rejects call audio");
                }
            }
        }
        if (opcode == 0x0C03)
        {
            lock (_gate)
            {
                ResetLocked("HCI_Reset");
                _aclBuffers = 0;
            }
        }
        else if (opcode == 0x0C31 && command.Length >= 4 && (command[3] & 1) != 0)
        {
            // Windows does not use this; if on, the controller would wait for acks of the packets consumed here.
            Log.Warn("hci", "Windows enabled controller-to-host ACL flow control: AAP traffic may stall");
        }
    }

    // The interrupt and bulk IN pipes are byte streams: a packet may span transfers and a transfer may hold several, so
    // both are parsed across transfers and Windows gets only bytes already routed to it, in pieces fitting its requests.

    /// <summary>
    /// Feeds one event-pipe transfer. <paramref name="complete"/>: it ended with a short packet, so an event still
    /// incomplete lost bytes (cancelled read) and is dropped to keep later events aligned.
    /// </summary>
    public void FeedEvents(ReadOnlySpan<byte> data, bool complete)
    {
        lock (_gate)
        {
            FeedEventsLocked(data);
            if (complete && _eventHave > 0)
            {
                Log.Warn("hci", $"incomplete event dropped ({_eventHave} bytes of 0x{_event[0]:X2})");
                _eventHave = 0;
            }
        }
    }

    private void FeedEventsLocked(ReadOnlySpan<byte> data)
    {
        while (data.Length > 0)
        {
            int need = _eventHave < 2 ? 2 : 2 + _event[1];
            int n = Math.Min(need - _eventHave, data.Length);
            data[..n].CopyTo(_event.AsSpan(_eventHave));
            _eventHave += n;
            data = data[n..];
            if (_eventHave == 2 + _event[1])
            {
                if (OnEventLocked(_event[.._eventHave]) is { } keep)
                {
                    _eventsOut.Add(keep);
                }
                _eventHave = 0;
            }
        }
    }

    /// <summary>Feeds one ACL IN transfer from the controller: Windows' packets are queued for it, ours consumed.</summary>
    public void FeedAcl(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            if (!_aclFramed)
            {
                // Before the first HCI_Reset the bulk pipe is not an ACL stream (Intel bootloaders send their events
                // there during firmware download): passed through as is, and it answers the pending command.
                _aclOut.Add(data.ToArray());
                _aclOut.End();
                Volatile.Write(ref _commandSentAt, 0);
                return;
            }
            while (data.Length > 0)
            {
                if (_aclRoute == Route.Undecided)
                {
                    int need = AclHeaderNeeded();
                    if (_aclHeadHave < need)
                    {
                        int n = Math.Min(need - _aclHeadHave, data.Length);
                        data[..n].CopyTo(_aclHead.AsSpan(_aclHeadHave));
                        _aclHeadHave += n;
                        data = data[n..];
                        // Route as soon as the header is complete: the controller's next transfer may never come.
                        if (_aclHeadHave < AclHeaderNeeded())
                        {
                            continue;
                        }
                    }
                    DecideAclLocked();
                }
                else
                {
                    int n = Math.Min(_aclRemaining, data.Length);
                    if (_aclRoute == Route.Windows)
                    {
                        _aclOut.Add(data[..n].ToArray());
                    }
                    else
                    {
                        data[..n].CopyTo(_aclOurs.AsSpan(_aclOursHave));
                        _aclOursHave += n;
                    }
                    _aclRemaining -= n;
                    data = data[n..];
                }
                if (_aclRoute != Route.Undecided && _aclRemaining == 0)
                {
                    FinishAclLocked();
                }
            }
        }
    }

    /// <summary>Moves queued event or ACL bytes for Windows into <paramref name="dest"/>; 0 when none.</summary>
    public int Take(bool events, Span<byte> dest, out bool ended)
    {
        lock (_gate)
        {
            return (events ? _eventsOut : _aclOut).Take(dest, out ended);
        }
    }

    /// <summary>Returns bytes from <see cref="Take"/> whose transfer Windows cancelled, for the next one.</summary>
    public void PutBack(bool events, byte[] data, bool ended)
    {
        lock (_gate)
        {
            (events ? _eventsOut : _aclOut).PutBack(data, ended);
        }
    }

    // Bytes of the current packet needed to decide its route: the ACL header, and for a start fragment also the L2CAP
    // header and the signaling command header with its first two CIDs (16 bytes), or the whole packet if shorter.
    private int AclHeaderNeeded()
    {
        if (_aclHeadHave < 4)
        {
            return 4;
        }
        int total = 4 + BinaryPrimitives.ReadUInt16LittleEndian(_aclHead.AsSpan(2));
        int pb = (_aclHead[1] >> 4) & 3;
        return Math.Min(total, pb != 1 ? _aclHead.Length : 4);
    }

    private void DecideAclLocked()
    {
        ReadOnlySpan<byte> head = _aclHead.AsSpan(0, _aclHeadHave);
        int handle = BinaryPrimitives.ReadUInt16LittleEndian(head) & 0x0FFF;
        int pb = (head[1] >> 4) & 3;
        int total = 4 + BinaryPrimitives.ReadUInt16LittleEndian(head[2..]);
        bool ours = false;
        if (_links.TryGetValue(handle, out Link? link))
        {
            if (pb != 1)
            {
                link.InboundOurs = false;
                if (head.Length >= 8)
                {
                    ushort cid = BinaryPrimitives.ReadUInt16LittleEndian(head[6..]);
                    link.InboundOurs = (link.Aap is { } ch && cid == ch.LocalCid) || (link.Decline is { } decline && cid == decline.LocalCid)
                        || (cid == 0x0001 && SignalingIsOursLocked(link, head[8..]))
                        || (cid == 0x0001 && (link.Aap is not null || link.Decline is not null || link.Ids.Count > 0) && HasSeveralCommands(head));
                    if (cid == 0x0001 && !link.InboundOurs && link.Ids.Count > 0 && head.Length >= 10)
                    {
                        Log.Info("aap", $"{link.Address:X12}: signaling 0x{head[8]:X2} id 0x{head[9]:X2} for Windows while ours wait ({link.Ids.Count})");
                    }
                }
            }
            ours = link.InboundOurs;
        }
        _aclHandle = handle;
        _aclRemaining = total - _aclHeadHave;
        Log.Info("acl", $"in 0x{handle:X3}{(link is null ? " (no known link)" : link.Le ? " LE" : "")} pb {pb} len {total - 4}{L2capText(head[4..], pb)} -> {(ours ? "ours" : "Windows")}");
        if (ours)
        {
            _aclRoute = Route.Ours;
            _aclOurs = new byte[total];
            head.CopyTo(_aclOurs);
            _aclOursHave = head.Length;
        }
        else
        {
            _aclRoute = Route.Windows;
            _aclOut.Add(head.ToArray());
        }
        _aclHeadHave = 0;
    }

    private void FinishAclLocked()
    {
        if (_aclRoute == Route.Ours && _links.TryGetValue(_aclHandle, out Link? link))
        {
            ReadOnlySpan<byte> payload = _aclOurs.AsSpan(4, _aclOursHave - 4);
            if (((_aclOurs[1] >> 4) & 3) != 1)
            {
                StartPduLocked(link, payload);
            }
            else
            {
                ContinuePduLocked(link, payload);
            }
        }
        else if (_aclRoute == Route.Windows)
        {
            _aclOut.End();
        }
        _aclRoute = Route.Undecided;
        _aclOurs = [];
        _aclOursHave = 0;
    }

    private enum Route
    {
        Undecided,
        Windows,
        Ours,
    }

    /// <summary>
    /// Bytes waiting for Windows' IN requests. An empty chunk marks a packet end, so one transfer never carries two
    /// packets, as with a real controller (Windows' stack relies on it).
    /// </summary>
    private sealed class ByteQueue
    {
        private readonly Queue<byte[]> _chunks = new();
        private int _offset;

        public void Add(byte[] chunk)
        {
            if (chunk.Length > 0)
            {
                _chunks.Enqueue(chunk);
            }
        }

        public void End() => _chunks.Enqueue([]);

        public void Clear()
        {
            _chunks.Clear();
            _offset = 0;
        }

        /// <summary>Puts bytes of a cancelled <see cref="Take"/> back in front, with the packet end it passed.</summary>
        public void PutBack(byte[] data, bool ended)
        {
            var rest = new List<byte[]>();
            while (_chunks.TryDequeue(out byte[]? c))
            {
                rest.Add(rest.Count == 0 && _offset > 0 ? c[_offset..] : c);
            }
            _offset = 0;
            _chunks.Enqueue(data);
            if (ended)
            {
                _chunks.Enqueue([]);
            }
            foreach (byte[] c in rest)
            {
                _chunks.Enqueue(c);
            }
        }

        /// <summary>Moves queued bytes into <paramref name="dest"/>, stopping at a packet end.</summary>
        public int Take(Span<byte> dest, out bool ended)
        {
            int n = 0;
            ended = false;
            while (n < dest.Length && _chunks.TryPeek(out byte[]? c))
            {
                if (c.Length == 0)
                {
                    _chunks.Dequeue();
                    if (n > 0)
                    {
                        ended = true;
                        break;
                    }
                    continue;
                }
                int k = Math.Min(c.Length - _offset, dest.Length - n);
                c.AsSpan(_offset, k).CopyTo(dest[n..]);
                n += k;
                _offset += k;
                if (_offset == c.Length)
                {
                    _chunks.Dequeue();
                    _offset = 0;
                }
            }
            return n;
        }
    }

    /// <summary>
    /// Waits for a free controller ACL buffer per counted packet of one Windows bulk OUT transfer, then records them as
    /// sent. False when cancelled while waiting.
    /// </summary>
    public bool ReserveOut(byte[] packets, Func<bool> cancelled)
    {
        lock (_gate)
        {
            long waitStart = 0;
            while (true)
            {
                if (cancelled())
                {
                    return false;
                }
                int needed = CountedLocked(packets);
                if (_aclBuffers == 0 || needed == 0 || _outstanding + needed <= _aclBuffers)
                {
                    break;
                }
                if (waitStart == 0)
                {
                    waitStart = Environment.TickCount64;
                }
                else if (waitStart > 0 && Environment.TickCount64 - waitStart > 2000)
                {
                    waitStart = -1;
                    Log.Warn("hci", $"ACL OUT (Windows') waiting for controller buffers over 2 s: {_outstanding} of {_aclBuffers} outstanding ({OwnersText()})");
                }
                Monitor.Wait(_gate, 100);
            }
            if (waitStart != 0)
            {
                Log.Info("acl", $"ACL OUT (Windows') waited {(waitStart > 0 ? $"{Environment.TickCount64 - waitStart} ms" : "over 2 s")} for controller buffers");
            }
            RecordLocked(packets, ours: false);
            return true;
        }
    }

    /// <summary>
    /// Records all fragments of one of our PDUs as sent if buffers are free for all of them now, else returns false so
    /// Windows' traffic never waits behind ours. Ours hold at most MaxOursOutstanding buffers (or the fragment count),
    /// so AirPods that stop taking our packets (e.g. busy with another device) cannot starve Windows.
    /// </summary>
    public bool TryReserveOwn(byte[][] fragments)
    {
        lock (_gate)
        {
            int needed = 0;
            foreach (byte[] f in fragments)
            {
                needed += CountedLocked(f);
            }
            if (_aclBuffers != 0 && needed != 0
                && (_outstanding + needed > _aclBuffers || OursOutstandingLocked() + needed > Math.Max(MaxOursOutstanding, needed)))
            {
                return false;
            }
            foreach (byte[] f in fragments)
            {
                RecordLocked(f, ours: true);
            }
            return true;
        }
    }

    // Records the counted packets of one transfer as sent (owner per link, in order) and tracks Windows' PDUs.
    private void RecordLocked(byte[] packets, bool ours)
    {
        ForEachPacket(packets, (handle, pb, payload) =>
        {
            _links.TryGetValue(handle, out Link? link);
            Log.Info("acl", $"out 0x{handle:X3}{(link is null ? " (no known link)" : link.Le ? " LE" : "")} pb {pb} len {payload.Length}{L2capText(payload, pb)}{(ours ? " (ours)" : "")}");
            if (link is null)
            {
                return;
            }
            if (Counted(link))
            {
                link.Owners.AddLast(ours);
                _outstanding++;
            }
            if (!ours)
            {
                // Track Windows' L2CAP PDU so ours are not sent between its fragments.
                if (pb != 1 && payload.Length >= 2)
                {
                    link.WindowsRemaining = Math.Max(0, 4 + BinaryPrimitives.ReadUInt16LittleEndian(payload) - payload.Length);
                }
                else if (pb == 1)
                {
                    link.WindowsRemaining = Math.Max(0, link.WindowsRemaining - payload.Length);
                }
            }
        });
    }

    private int OursOutstandingLocked()
    {
        int n = 0;
        foreach (Link link in _links.Values)
        {
            foreach (bool owner in link.Owners)
            {
                if (owner)
                {
                    n++;
                }
            }
        }
        return n;
    }

    /// <summary>Undoes <see cref="ReserveOut"/> for a transfer the controller did not take.</summary>
    public void UnreserveOut(byte[] packets)
    {
        lock (_gate)
        {
            ForEachPacket(packets, (handle, _, _) =>
            {
                if (_links.TryGetValue(handle, out Link? link) && Counted(link) && link.Owners.Count > 0)
                {
                    link.Owners.RemoveLast();
                    _outstanding--;
                }
            });
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Whether our packets for <paramref name="handle"/> must wait: Windows is in the middle of a PDU there.</summary>
    public bool MustDefer(int handle)
    {
        lock (_gate)
        {
            return _links.TryGetValue(handle, out Link? link) && link.WindowsRemaining > 0;
        }
    }

    public bool LinkExists(int handle)
    {
        lock (_gate)
        {
            return _links.ContainsKey(handle);
        }
    }

    /// <summary>Opens the AAP channel on Windows' ACL link; Error is 0 or a Protocol error code.</summary>
    public async Task<(int Error, AapChannel? Channel)> OpenAsync(ulong address, CancellationToken clientGone)
    {
        AapChannel ch;
        lock (_gate)
        {
            Link? link = null;
            foreach (Link l in _links.Values)
            {
                if (l.Address == address && !l.Le)
                {
                    link = l;
                }
            }
            if (link is null)
            {
                return (Protocol.ErrorNotConnected, null);
            }
            if (link.Aap is not null)
            {
                return (Protocol.ErrorBusy, null);
            }
            ch = new AapChannel(link, NextCidLocked());
            link.Aap = ch;
            if (link.Encrypted)
            {
                ConnectLocked(ch);
            }
            Log.Info("aap", $"{address:X12}: opening channel 0x{ch.LocalCid:X4}{(link.Encrypted ? "" : " after encryption")}");
        }
        int error;
        try
        {
            error = await ch.Opened.Task.WaitAsync(OpenTimeout, clientGone).ConfigureAwait(false);
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException)
        {
            error = Protocol.ErrorTimeout;
        }
        if (error != 0)
        {
            Log.Warn("aap", $"{address:X12}: channel not opened, error {error}");
            Close(ch);
            return (error, null);
        }
        Log.Info("aap", $"{address:X12}: channel open (local 0x{ch.LocalCid:X4}, remote 0x{ch.RemoteCid:X4})");
        return (0, ch);
    }

    /// <summary>Sends one AAP packet; false when the channel is no longer open.</summary>
    public bool Send(AapChannel ch, ReadOnlySpan<byte> packet)
    {
        lock (_gate)
        {
            if (!ch.IsOpen || ch.Link.Aap != ch)
            {
                return false;
            }
            SendL2capLocked(ch.Link, ch.RemoteCid, packet);
            return true;
        }
    }

    /// <summary>Closes the channel for the app, disconnecting it at the AirPods if the link is still up.</summary>
    public void Close(AapChannel ch)
    {
        lock (_gate)
        {
            if (ch.Link.Aap != ch)
            {
                return;
            }
            if (ch.RemoteCid != 0 && _links.ContainsKey(ch.Link.Handle))
            {
                SignalLocked(ch.Link, null, 0x06, Cids(ch.RemoteCid, ch.LocalCid));
            }
            Log.Info("aap", $"{ch.Link.Address:X12}: channel 0x{ch.LocalCid:X4} closed by the app");
            DropLocked(ch);
            ch.End(0);
        }
    }

    private bool Counted(Link link) => !link.Le || _leShared;

    private string OwnersText()
    {
        var parts = new List<string>();
        foreach (Link l in _links.Values)
        {
            if (l.Owners.Count > 0)
            {
                int ours = 0;
                foreach (bool o in l.Owners)
                {
                    ours += o ? 1 : 0;
                }
                parts.Add($"0x{l.Handle:X3}: {l.Owners.Count} ({ours} ours)");
            }
        }
        return string.Join(", ", parts);
    }

    private int CountedLocked(byte[] packets)
    {
        int n = 0;
        ForEachPacket(packets, (handle, _, _) =>
        {
            if (_links.TryGetValue(handle, out Link? link) && Counted(link))
            {
                n++;
            }
        });
        return n;
    }

    private delegate void PacketAction(int handle, int pb, ReadOnlySpan<byte> payload);

    private static void ForEachPacket(byte[] data, PacketAction action)
    {
        for (int at = 0; at + 4 <= data.Length;)
        {
            ushort header = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at));
            int length = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 2));
            int end = Math.Min(data.Length, at + 4 + length);
            action(header & 0x0FFF, (header >> 12) & 3, data.AsSpan(at + 4, end - at - 4));
            at += 4 + length;
        }
    }

    private byte[]? OnEventLocked(byte[] e)
    {
        LogEvent(e);
        if (e[0] is 0x0E or 0x0F)
        {
            Volatile.Write(ref _commandSentAt, 0);
        }
        switch (e[0])
        {
            case 0x0E when e.Length >= 6 && e[5] == 0:
                OnCommandCompleteLocked(e);
                break;
            case 0x10:
                Log.Warn("hci", $"controller hardware error 0x{(e.Length >= 3 ? e[2] : 0):X2}");
                RaiseTrouble("hardware error");
                break;
            case 0x0F when e.Length >= 6 && e[2] != 0 && BinaryPrimitives.ReadUInt16LittleEndian(e.AsSpan(4)) is 0x0428 or 0x043D or 0x0429 or 0x043E:
                // Windows' call audio setup or accept failed at once (the status names no link).
                foreach (Link link in _links.Values)
                {
                    if (!HasScoLocked(link.Address))
                    {
                        SetCallAudioLocked(link, false, $"call audio setup refused, status 0x{e[2]:X2}");
                    }
                }
                break;
            case 0x04 when e.Length >= 12 && e[11] is 0x00 or 0x02:
                // Connection_Request for an SCO/eSCO link: the device asks for call audio.
                if (FindLinkLocked(ReadAddress(e.AsSpan(2))) is { } requester)
                {
                    SetCallAudioLocked(requester, true, "the device asks for call audio");
                }
                break;
            case 0x2C when e.Length >= 11:
                {
                    // Synchronous_Connection_Complete: a call audio (SCO/eSCO) link is up, or its setup failed.
                    int handle = BinaryPrimitives.ReadUInt16LittleEndian(e.AsSpan(3)) & 0x0FFF;
                    ulong address = ReadAddress(e.AsSpan(5));
                    Link? acl = FindLinkLocked(address);
                    if (e[2] == 0)
                    {
                        _scoLinks[handle] = address;
                        Log.Info("hci", $"call audio link 0x{handle:X3} up");
                        CallAudioStarted?.Invoke();
                        if (acl is not null)
                        {
                            SetCallAudioLocked(acl, true, "call audio link up");
                        }
                    }
                    else if (acl is not null)
                    {
                        SetCallAudioLocked(acl, HasScoLocked(address), $"call audio setup failed, status 0x{e[2]:X2}");
                    }
                    break;
                }
            case 0x03 when e.Length >= 13 && e[2] == 0 && e[11] == 1:
                {
                    int handle = BinaryPrimitives.ReadUInt16LittleEndian(e.AsSpan(3)) & 0x0FFF;
                    ulong address = ReadAddress(e.AsSpan(5));
                    var link = new Link(handle, address, le: false);
                    _links[handle] = link;
                    Log.Info("hci", $"ACL link 0x{handle:X3} to {address:X12}");
                    PrepareDeclineLocked(link);
                    break;
                }
            case 0x3E when e.Length >= 6 && e[2] is 0x01 or 0x0A or 0x29 && e[3] == 0:
                {
                    int handle = BinaryPrimitives.ReadUInt16LittleEndian(e.AsSpan(4)) & 0x0FFF;
                    _links[handle] = new Link(handle, 0, le: true);
                    break;
                }
            case 0x05 when e.Length >= 6 && e[2] == 0:
                {
                    int handle = BinaryPrimitives.ReadUInt16LittleEndian(e.AsSpan(3)) & 0x0FFF;
                    if (_scoLinks.Remove(handle, out ulong scoAddress))
                    {
                        Log.Info("hci", $"call audio link 0x{handle:X3} closed, reason 0x{e[5]:X2}");
                        if (FindLinkLocked(scoAddress) is { } acl && !HasScoLocked(scoAddress))
                        {
                            SetCallAudioLocked(acl, false, "call audio link closed");
                        }
                        if (_scoLinks.Count == 0)
                        {
                            CallAudioEnded?.Invoke();
                        }
                    }
                    else if (_links.Remove(handle, out Link? link))
                    {
                        // The controller flushed what was still queued for this link; its buffers are free again.
                        _outstanding -= link.Owners.Count;
                        Monitor.PulseAll(_gate);
                        if (!link.Le)
                        {
                            Log.Info("hci", $"ACL link 0x{handle:X3} to {link.Address:X12} closed, reason 0x{e[5]:X2}");
                        }
                        EndChannelLocked(link, Protocol.ErrorNotConnected);
                    }
                    break;
                }
            case 0x08 or 0x59 when e.Length >= 6 && e[2] == 0:
                {
                    int handle = BinaryPrimitives.ReadUInt16LittleEndian(e.AsSpan(3)) & 0x0FFF;
                    if (_links.TryGetValue(handle, out Link? link))
                    {
                        bool was = link.Encrypted;
                        link.Encrypted = e[5] != 0;
                        if (!was && link.Encrypted && link.Aap is { RequestSent: false } ch)
                        {
                            ConnectLocked(ch);
                        }
                        if (!was && link.Encrypted && link.Decline is { Step: DeclineStep.Waiting } decline)
                        {
                            ConnectDeclineLocked(link, decline);
                        }
                    }
                    break;
                }
            case 0x13 when e.Length >= 3 && e.Length >= 3 + (4 * e[2]):
                return OnCompletedPacketsLocked(e);
        }
        return e;
    }

    private void OnCommandCompleteLocked(byte[] e)
    {
        ushort opcode = BinaryPrimitives.ReadUInt16LittleEndian(e.AsSpan(3));
        if (opcode == 0x0C03)
        {
            // Reset on operational firmware: the bulk pipe now carries framed ACL data from a packet boundary.
            _aclFramed = true;
        }
        else if (opcode == 0x1005 && e.Length >= 11)
        {
            _aclMaxLength = BinaryPrimitives.ReadUInt16LittleEndian(e.AsSpan(6));
            _aclBuffers = BinaryPrimitives.ReadUInt16LittleEndian(e.AsSpan(9));
            Log.Info("hci", $"controller ACL buffers: {_aclBuffers} x {_aclMaxLength} bytes");
        }
        else if (opcode == 0x1009 && e.Length >= 12)
        {
            _localAddress = ReadAddress(e.AsSpan(6));
        }
        else if (opcode is 0x2002 or 0x2060 && e.Length >= 9)
        {
            // No LE buffers of its own: LE links use the shared ACL buffers.
            _leShared = BinaryPrimitives.ReadUInt16LittleEndian(e.AsSpan(6)) == 0 || e[8] == 0;
            Log.Info("hci", $"controller LE buffers {(_leShared ? "shared with ACL" : "separate")}");
        }
        else if (opcode == 0x1001 && e.Length >= 14)
        {
            // HCI version and revision, LMP version, manufacturer, LMP subversion: the subversion identifies the
            // running firmware (a vendor patch or the ROM), so it changes when the controller lost its firmware.
            byte[] version = e[6..14];
            if (_localVersion is { } before && !before.AsSpan().SequenceEqual(version))
            {
                Log.Warn("hci", $"controller version changed {Convert.ToHexString(before)} -> {Convert.ToHexString(version)}");
                RaiseTrouble("firmware changed");
            }
            else if (_localVersion is null)
            {
                Log.Info("hci", $"controller version {Convert.ToHexString(version)}");
            }
            _localVersion = version;
        }
    }

    private void RaiseTrouble(string reason)
    {
        if (ControllerTrouble is { } handler)
        {
            ThreadPool.QueueUserWorkItem(_ => handler(reason));
        }
    }

    private byte[]? OnCompletedPacketsLocked(byte[] e)
    {
        int n = e[2];
        bool any = false;
        for (int i = 0; i < n; i++)
        {
            int o = 3 + (i * 4);
            int handle = BinaryPrimitives.ReadUInt16LittleEndian(e.AsSpan(o)) & 0x0FFF;
            int count = BinaryPrimitives.ReadUInt16LittleEndian(e.AsSpan(o + 2));
            if (_links.TryGetValue(handle, out Link? traced))
            {
                Log.Info("acl", $"completed 0x{handle:X3} x{count}, outstanding before {traced.Owners.Count} ({string.Concat(traced.Owners.Select(o => o ? 'O' : 'W'))}), total {_outstanding}/{_aclBuffers}");
            }
            if (_links.TryGetValue(handle, out Link? link) && Counted(link))
            {
                int ours = 0;
                for (int k = 0; k < count && link.Owners.First is { } first; k++)
                {
                    if (first.Value)
                    {
                        ours++;
                    }
                    link.Owners.RemoveFirst();
                    _outstanding--;
                }
                count -= ours;
                BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(o + 2), (ushort)count);
            }
            if (count > 0)
            {
                any = true;
            }
        }
        Monitor.PulseAll(_gate);
        return any ? e : null;
    }

    // Queued bytes for Windows are kept on HCI_Reset: its own Command Complete may be among them (an Intel bootloader
    // answers on the bulk pipe).
    private void ResetLocked(string why, bool dropQueued = false)
    {
        _scoLinks.Clear();
        foreach (Link link in _links.Values)
        {
            EndChannelLocked(link, Protocol.ErrorNotConnected);
        }
        if (_links.Count > 0)
        {
            Log.Info("hci", $"link state reset: {why}");
        }
        _links.Clear();
        _outstanding = 0;
        _aclHeadHave = 0;
        _aclRoute = Route.Undecided;
        _aclOurs = [];
        _aclOursHave = 0;
        _aclFramed = false;
        if (dropQueued)
        {
            _eventHave = 0;
            _eventsOut.Clear();
            _aclOut.Clear();
        }
        Monitor.PulseAll(_gate);
    }

    private ushort NextCidLocked()
    {
        ushort cid = _nextCid;
        _nextCid = _nextCid == 0x7FFF ? FirstCid : (ushort)(_nextCid + 1);
        return cid;
    }

    private void ConnectLocked(AapChannel ch)
    {
        ch.RequestSent = true;
        SignalLocked(ch.Link, ch, 0x02, Cids(AapPsm, ch.LocalCid));
    }

    private static byte[] Cids(ushort a, ushort b) => [(byte)a, (byte)(a >> 8), (byte)b, (byte)(b >> 8)];

    // Responses to the request are routed to `ch`, or by `localCid` when there is no AAP channel (the Hands-Free decline).
    // A Disconnection Request names our CID second (DCID, SCID).
    private void SignalLocked(Link link, AapChannel? ch, byte code, ReadOnlySpan<byte> data, ushort localCid = 0)
    {
        byte id = link.NextId;
        link.NextId = link.NextId == 0xFF ? (byte)0xC0 : (byte)(link.NextId + 1);
        ushort cid = code == 0x06 ? BinaryPrimitives.ReadUInt16LittleEndian(data[2..]) : ch?.LocalCid ?? localCid;
        link.Ids[id] = new Request(ch, code, cid);
        Log.Info("aap", $"{link.Address:X12}: sending signaling 0x{code:X2} id 0x{id:X2} {Convert.ToHexString(data)}");
        SendCommandLocked(link, code, id, data);
    }

    private void SendCommandLocked(Link link, byte code, byte id, ReadOnlySpan<byte> data)
    {
        var cmd = new byte[4 + data.Length];
        cmd[0] = code;
        cmd[1] = id;
        BinaryPrimitives.WriteUInt16LittleEndian(cmd.AsSpan(2), (ushort)data.Length);
        data.CopyTo(cmd.AsSpan(4));
        SendL2capLocked(link, 0x0001, cmd);
    }

    private void SendL2capLocked(Link link, ushort cid, ReadOnlySpan<byte> payload)
    {
        if (_send is not { } send)
        {
            return;
        }
        var l2 = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(l2, (ushort)payload.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(l2.AsSpan(2), cid);
        payload.CopyTo(l2.AsSpan(4));
        var fragments = new List<byte[]>();
        for (int off = 0; off < l2.Length; off += _aclMaxLength)
        {
            int n = Math.Min(_aclMaxLength, l2.Length - off);
            var acl = new byte[4 + n];
            int pb = off == 0 ? 0b00 : 0b01;
            BinaryPrimitives.WriteUInt16LittleEndian(acl, (ushort)(link.Handle | (pb << 12)));
            BinaryPrimitives.WriteUInt16LittleEndian(acl.AsSpan(2), (ushort)n);
            Array.Copy(l2, off, acl, 4, n);
            fragments.Add(acl);
        }
        send(new Urb { Internal = true, Fragments = [.. fragments], Seq = (uint)link.Handle });
    }

    // A signaling frame longer than its first command holds several; while we have a channel or requests on the link,
    // it is taken whole and split per command.
    private static bool HasSeveralCommands(ReadOnlySpan<byte> head) =>
        head.Length >= 12 && BinaryPrimitives.ReadUInt16LittleEndian(head[4..]) > 4 + BinaryPrimitives.ReadUInt16LittleEndian(head[10..]);

    private bool SignalingIsOursLocked(Link link, ReadOnlySpan<byte> sig)
    {
        if (sig.Length < 4)
        {
            return false;
        }
        byte code = sig[0];
        byte id = sig[1];
        if (code is 0x01 or 0x03 or 0x05 or 0x07)
        {
            // Windows' identifiers reach our range too: a response is ours only if it answers our request kind with that
            // id and names our CID. A Command Reject names no CID, so the id alone decides.
            if (!link.Ids.TryGetValue(id, out Request request))
            {
                return false;
            }
            if (code == 0x01)
            {
                return true;
            }
            int at = code == 0x05 ? 4 : 6;
            return code == request.Code + 1 && sig.Length >= at + 2 && BinaryPrimitives.ReadUInt16LittleEndian(sig[at..]) == request.Cid;
        }
        // The AirPods open RFCOMM for Hands-Free while Windows has it off: refused here at once (see RefusesRfcommLocked).
        if (code == 0x02)
        {
            return sig.Length >= 8 && BinaryPrimitives.ReadUInt16LittleEndian(sig[4..]) == RfcommPsm && RefusesRfcommLocked(link);
        }
        // Configure_Request and Disconnection_Request name the destination CID first: ours.
        if (code is not (0x04 or 0x06) || sig.Length < 6)
        {
            return false;
        }
        ushort dcid = BinaryPrimitives.ReadUInt16LittleEndian(sig[4..]);
        return (link.Aap is { } ch && dcid == ch.LocalCid) || (link.Decline is { } decline && dcid == decline.LocalCid);
    }

    // Windows answers an RFCOMM channel from the AirPods while their Hands-Free service is off, but never their first
    // Hands-Free command: the AirPods hold back their settings report until it times out. Read from Windows' own
    // state each time, so a change in WPods or in Windows' settings applies at once.
    private bool RefusesRfcommLocked(Link link) =>
        link.AapDevice && BluetoothRegistry.HandsFreeEnabled(_localAddress, link.Address) == false;

    private void StartPduLocked(Link link, ReadOnlySpan<byte> l2)
    {
        int total = BinaryPrimitives.ReadUInt16LittleEndian(l2);
        if (link.RxPdu is { } unfinished)
        {
            Log.Info("acl", $"incomplete PDU dropped: {link.RxHave} of {unfinished.Length} bytes");
        }
        link.RxPdu = new byte[4 + total];
        int n = Math.Min(l2.Length, link.RxPdu.Length);
        l2[..n].CopyTo(link.RxPdu);
        link.RxHave = n;
        if (link.RxHave >= link.RxPdu.Length)
        {
            DeliverPduLocked(link);
        }
    }

    private void ContinuePduLocked(Link link, ReadOnlySpan<byte> fragment)
    {
        if (link.RxPdu is not { } pdu)
        {
            return;
        }
        int n = Math.Min(fragment.Length, pdu.Length - link.RxHave);
        fragment[..n].CopyTo(pdu.AsSpan(link.RxHave));
        link.RxHave += n;
        if (link.RxHave >= pdu.Length)
        {
            DeliverPduLocked(link);
        }
    }

    private void DeliverPduLocked(Link link)
    {
        byte[] pdu = link.RxPdu!;
        link.RxPdu = null;
        ushort cid = BinaryPrimitives.ReadUInt16LittleEndian(pdu.AsSpan(2));
        if (cid == 0x0001)
        {
            // Split per command: ours are handled here, Windows' are passed on in a frame of their own.
            var windows = new List<byte>();
            for (int at = 4; at + 4 <= pdu.Length;)
            {
                int end = Math.Min(pdu.Length, at + 4 + BinaryPrimitives.ReadUInt16LittleEndian(pdu.AsSpan(at + 2)));
                ReadOnlySpan<byte> command = pdu.AsSpan(at, end - at);
                if (SignalingIsOursLocked(link, command))
                {
                    OnSignalingLocked(link, command);
                }
                else
                {
                    windows.AddRange(command);
                }
                at = end;
            }
            if (windows.Count > 0)
            {
                var frame = new byte[8 + windows.Count];
                BinaryPrimitives.WriteUInt16LittleEndian(frame, (ushort)(link.Handle | (0b10 << 12)));
                BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), (ushort)(4 + windows.Count));
                BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4), (ushort)windows.Count);
                BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6), 0x0001);
                windows.CopyTo(frame, 8);
                _aclOut.Add(frame);
                _aclOut.End();
            }
        }
        else if (link.Aap is { IsOpen: true } ch && cid == ch.LocalCid)
        {
            // Header and opcode only (the app logs packets); mic audio (0x58, ~50 per second) is not logged.
            if (!(pdu.Length >= 10 && pdu[8] == 0x58 && pdu[9] == 0x00))
            {
                Log.Info("aap", $"deliver {pdu.Length - 4} bytes {Convert.ToHexString(pdu.AsSpan(4, Math.Min(6, pdu.Length - 4)))}");
            }
            ch.Deliver(pdu[4..]);
        }
        else if (link.Decline is { } decline && cid == decline.LocalCid)
        {
            OnDeclineFrameLocked(link, decline, pdu.AsSpan(4));
        }
        else
        {
            Log.Info("aap", $"dropped PDU cid 0x{cid:X4} ({pdu.Length - 4} bytes): no open channel");
        }
    }

    // Only for commands SignalingIsOursLocked accepted (at least 4 bytes).
    private void OnSignalingLocked(Link link, ReadOnlySpan<byte> sig)
    {
        byte code = sig[0];
        byte id = sig[1];
        ReadOnlySpan<byte> d = sig[4..];
        AapChannel? ch = link.Aap;
        Log.Info("aap", $"{link.Address:X12}: signaling 0x{code:X2} id 0x{id:X2} {Convert.ToHexString(d[..Math.Min(d.Length, 12)])}");
        switch (code)
        {
            case 0x03 when d.Length >= 8:
                {
                    ushort result = BinaryPrimitives.ReadUInt16LittleEndian(d[4..]);
                    if (result == 1)
                    {
                        return; // pending
                    }
                    if (!link.Ids.Remove(id, out Request request))
                    {
                        return;
                    }
                    if (IsDeclineRequest(link, request, out HandsFreeDecline? decline))
                    {
                        OnDeclineConnectedLocked(link, decline, result, BinaryPrimitives.ReadUInt16LittleEndian(d));
                        return;
                    }
                    if (request.Channel is not { } requester)
                    {
                        if (request.Code == 0x02 && result == 0)
                        {
                            SignalLocked(link, null, 0x06, Cids(BinaryPrimitives.ReadUInt16LittleEndian(d), request.Cid));
                        }
                        return;
                    }
                    if (requester != ch)
                    {
                        // The app gave up before the AirPods answered: close what they opened.
                        if (result == 0)
                        {
                            SignalLocked(link, null, 0x06, Cids(BinaryPrimitives.ReadUInt16LittleEndian(d), requester.LocalCid));
                        }
                        return;
                    }
                    if (result != 0)
                    {
                        Log.Warn("aap", $"{link.Address:X12}: connection refused, result {result}");
                        DropLocked(ch);
                        ch.End(Protocol.ErrorRefused);
                        return;
                    }
                    ch.RemoteCid = BinaryPrimitives.ReadUInt16LittleEndian(d);
                    byte[] config = [(byte)ch.RemoteCid, (byte)(ch.RemoteCid >> 8), 0, 0, 0x01, 2, unchecked((byte)OurMtu), OurMtu >> 8];
                    SignalLocked(link, ch, 0x04, config);
                    break;
                }
            case 0x04 when link.Decline is { } decline && BinaryPrimitives.ReadUInt16LittleEndian(d) == decline.LocalCid:
                SendCommandLocked(link, 0x05, id, [(byte)decline.RemoteCid, (byte)(decline.RemoteCid >> 8), 0, 0, 0, 0]);
                decline.RemoteConfigured = true;
                CheckDeclineOpenLocked(link, decline);
                break;
            case 0x04 when ch is not null:
                SendCommandLocked(link, 0x05, id, [(byte)ch.RemoteCid, (byte)(ch.RemoteCid >> 8), 0, 0, 0, 0]);
                ch.RemoteConfigured = true;
                CheckOpenLocked(ch);
                break;
            case 0x05 when d.Length >= 6:
                {
                    ushort result = BinaryPrimitives.ReadUInt16LittleEndian(d[4..]);
                    if (result == 4 /* pending */)
                    {
                        return;
                    }
                    // Only the answer to this channel's own request counts: a late answer for an earlier channel may
                    // name the same CID.
                    if (!link.Ids.Remove(id, out Request request))
                    {
                        return;
                    }
                    if (IsDeclineRequest(link, request, out HandsFreeDecline? decline))
                    {
                        if (result != 0)
                        {
                            Log.Warn("hfp", $"{link.Address:X12}: Hands-Free decline: configuration refused, result {result}");
                            CloseDeclineLocked(link, decline);
                            return;
                        }
                        decline.LocalConfigured = true;
                        CheckDeclineOpenLocked(link, decline);
                        return;
                    }
                    if (ch is null || request.Channel != ch)
                    {
                        return;
                    }
                    if (result != 0)
                    {
                        Log.Warn("aap", $"{link.Address:X12}: configuration refused, result {result}");
                        SignalLocked(link, null, 0x06, Cids(ch.RemoteCid, ch.LocalCid));
                        DropLocked(ch);
                        ch.End(Protocol.ErrorRefused);
                        return;
                    }
                    ch.LocalConfigured = true;
                    CheckOpenLocked(ch);
                    break;
                }
            case 0x06 when link.Decline is { } decline && BinaryPrimitives.ReadUInt16LittleEndian(d) == decline.LocalCid:
                SendCommandLocked(link, 0x07, id, Cids(decline.LocalCid, decline.RemoteCid));
                Log.Info("hfp", $"{link.Address:X12}: Hands-Free decline: channel closed by the AirPods");
                link.Decline = null;
                break;
            case 0x06 when ch is not null:
                SendCommandLocked(link, 0x07, id, Cids(ch.LocalCid, ch.RemoteCid));
                Log.Info("aap", $"{link.Address:X12}: channel closed by the AirPods");
                DropLocked(ch);
                ch.End(0);
                break;
            case 0x07:
                if (link.Ids.Remove(id, out Request closed) && link.Decline is { Step: DeclineStep.Closing } closing && closed.Cid == closing.LocalCid)
                {
                    link.Decline = null;
                }
                break;
            case 0x01:
                {
                    if (!link.Ids.Remove(id, out Request rejected))
                    {
                        break;
                    }
                    if (ch is not null && rejected.Channel == ch && !ch.IsOpen)
                    {
                        Log.Warn("aap", $"{link.Address:X12}: command rejected");
                        DropLocked(ch);
                        ch.End(Protocol.ErrorRefused);
                    }
                    else if (IsDeclineRequest(link, rejected, out HandsFreeDecline? decline))
                    {
                        Log.Warn("hfp", $"{link.Address:X12}: Hands-Free decline: command rejected");
                        link.Decline = null;
                    }
                    break;
                }
            case 0x02 when d.Length >= 4:
                // RefusesRfcommLocked said so: "PSM not supported", Windows never sees the request.
                Log.Info("hfp", $"{link.Address:X12}: Hands-Free is off in Windows: the AirPods' RFCOMM channel refused");
                SendCommandLocked(link, 0x03, id, [0, 0, d[2], d[3], 0x02, 0x00, 0, 0]);
                break;
        }
    }

    private static void CheckOpenLocked(AapChannel ch)
    {
        if (ch.RemoteConfigured && ch.LocalConfigured && !ch.IsOpen)
        {
            ch.IsOpen = true;
            ch.Opened.TrySetResult(0);
            if (ch.Link.CallAudio)
            {
                ch.SendCallAudio(true);
            }
        }
    }

    private static void DropLocked(AapChannel ch)
    {
        if (ch.Link.Aap == ch)
        {
            ch.Link.Aap = null;
        }
        ch.IsOpen = false;
    }

    // AirPods whose Hands-Free service is off in Windows 
    // never sets Hands-Free up, and the
    // holds back their settings report until their own attempt times out.
    // A Hands-Free connection from here ends that
    // wait at once.
    private void PrepareDeclineLocked(Link link)
    {
        (bool aap, byte channel) = BluetoothRegistry.ReadServices(link.Address);
        link.AapDevice = aap;
        if (!aap || BluetoothRegistry.HandsFreeEnabled(_localAddress, link.Address) != false)
        {
            return;
        }
        if (channel == 0)
        {
            Log.Info("hfp", $"{link.Address:X12}: Hands-Free is off in Windows, but no cached Hands-Free channel: not declined");
            return;
        }
        link.Decline = new HandsFreeDecline(NextCidLocked(), channel);
        Log.Info("hfp", $"{link.Address:X12}: Hands-Free is off in Windows: declining it on RFCOMM channel {channel} once encrypted");
    }

    private void ConnectDeclineLocked(Link link, HandsFreeDecline decline)
    {
        decline.Step = DeclineStep.Connecting;
        SignalLocked(link, null, 0x02, Cids(RfcommPsm, decline.LocalCid), decline.LocalCid);
        _ = CutDeclineShortAsync(link, decline, DeclineTimeout);
    }

    private static bool IsDeclineRequest(Link link, Request request, [NotNullWhen(true)] out HandsFreeDecline? decline)
    {
        decline = link.Decline;
        return request.Channel is null && decline is not null && request.Cid == decline.LocalCid;
    }

    private void OnDeclineConnectedLocked(Link link, HandsFreeDecline decline, ushort result, ushort remoteCid)
    {
        if (result != 0)
        {
            Log.Info("hfp", $"{link.Address:X12}: Hands-Free decline: the AirPods refused the RFCOMM channel, result {result}");
            link.Decline = null;
            return;
        }
        decline.RemoteCid = remoteCid;
        SignalLocked(link, null, 0x04, [(byte)remoteCid, (byte)(remoteCid >> 8), 0, 0], decline.LocalCid);
    }

    private void CheckDeclineOpenLocked(Link link, HandsFreeDecline decline)
    {
        if (decline.RemoteConfigured && decline.LocalConfigured && decline.Step == DeclineStep.Connecting)
        {
            decline.Step = DeclineStep.Mux;
            SendL2capLocked(link, decline.RemoteCid, Rfcomm.Command(0, Rfcomm.Sabm));
        }
    }

    // Multiplexer up, PN, Hands-Free DLC up, Modem Status, then the AirPods' first AT command (AT+BRSF) gets ERROR and
    // everything is closed again; without one within SilentTimeout it is closed all the same.
    private void OnDeclineFrameLocked(Link link, HandsFreeDecline decline, ReadOnlySpan<byte> frame)
    {
        if (!Rfcomm.TryParse(frame, out byte dlci, out byte control, out ReadOnlySpan<byte> info))
        {
            return;
        }
        // Frame kind and size only, never the content.
        Log.Info("hfp", $"{link.Address:X12}: RFCOMM in: DLC {dlci}, control 0x{control:X2}, {info.Length} bytes, step {decline.Step}");
        switch (control)
        {
            case Rfcomm.Ua when dlci == 0 && decline.Step == DeclineStep.Mux:
                decline.Step = DeclineStep.Pn;
                SendL2capLocked(link, decline.RemoteCid, Rfcomm.ParameterNegotiation(decline.Dlci));
                break;
            case Rfcomm.Uih when dlci == 0 && decline.Step == DeclineStep.Pn && info.Length >= 1 && info[0] >> 2 == Rfcomm.PnType && (info[0] & 0x02) == 0:
                decline.Step = DeclineStep.Dlc;
                SendL2capLocked(link, decline.RemoteCid, Rfcomm.Command(decline.Dlci, Rfcomm.Sabm));
                break;
            case Rfcomm.Ua when dlci == decline.Dlci && decline.Step == DeclineStep.Dlc:
                decline.Step = DeclineStep.Open;
                SendL2capLocked(link, decline.RemoteCid, Rfcomm.ModemStatus(decline.Dlci));
                _ = CutDeclineShortAsync(link, decline, SilentTimeout);
                break;
            case Rfcomm.Ua or Rfcomm.Dm when dlci == decline.Dlci && decline.Step is DeclineStep.Dlc or DeclineStep.ClosingDlc:
                decline.Step = DeclineStep.ClosingMux;
                SendL2capLocked(link, decline.RemoteCid, Rfcomm.Command(0, Rfcomm.Disc));
                break;
            case Rfcomm.Ua or Rfcomm.Dm when dlci == 0 && decline.Step is DeclineStep.Mux or DeclineStep.ClosingMux:
                CloseDeclineLocked(link, decline);
                break;
            case Rfcomm.Disc:
                SendL2capLocked(link, decline.RemoteCid, Rfcomm.Response(dlci, Rfcomm.Ua));
                if (dlci == 0)
                {
                    CloseDeclineLocked(link, decline);
                }
                else if (dlci == decline.Dlci && decline.Step == DeclineStep.Open)
                {
                    decline.Step = DeclineStep.ClosingMux;
                    SendL2capLocked(link, decline.RemoteCid, Rfcomm.Command(0, Rfcomm.Disc));
                }
                break;
            case Rfcomm.Uih when dlci == 0 && info.Length >= 1 && (info[0] & 0x02) != 0:
                {
                    // A multiplexer command (Modem Status and the like): answered with the same values.
                    byte[] answer = info.ToArray();
                    answer[0] &= 0xFD;
                    SendL2capLocked(link, decline.RemoteCid, Rfcomm.Data(0, answer));
                    break;
                }
            case Rfcomm.Uih when dlci == decline.Dlci && decline.Step == DeclineStep.Open && info.Length > 0:
                Log.Info("hfp", $"{link.Address:X12}: Hands-Free decline: first command ({info.Length} bytes) answered ERROR, closing");
                SendL2capLocked(link, decline.RemoteCid, Rfcomm.Data(decline.Dlci, AtError));
                decline.Step = DeclineStep.ClosingDlc;
                SendL2capLocked(link, decline.RemoteCid, Rfcomm.Command(decline.Dlci, Rfcomm.Disc));
                break;
        }
    }

    // link.Decline stays until the AirPods answer the Disconnection Request (case 0x07), so their late frames still land here.
    private void CloseDeclineLocked(Link link, HandsFreeDecline decline)
    {
        if (decline.Step == DeclineStep.Closing)
        {
            return;
        }
        Log.Info("hfp", $"{link.Address:X12}: Hands-Free decline: closing the RFCOMM channel");
        decline.Step = DeclineStep.Closing;
        if (decline.RemoteCid == 0)
        {
            link.Decline = null;
            return;
        }
        SignalLocked(link, null, 0x06, Cids(decline.RemoteCid, decline.LocalCid));
    }

    private async Task CutDeclineShortAsync(Link link, HandsFreeDecline decline, TimeSpan delay)
    {
        await Task.Delay(delay).ConfigureAwait(false);
        lock (_gate)
        {
            if (link.Decline == decline && decline.Step != DeclineStep.Closing && _links.TryGetValue(link.Handle, out Link? current) && current == link)
            {
                Log.Warn("hfp", $"{link.Address:X12}: Hands-Free decline not done after {delay.TotalMilliseconds:0} ms (step {decline.Step}): closing it");
                CloseDeclineLocked(link, decline);
            }
        }
    }

    // Tells the app that call audio (Hands-Free) starts or ended on the link, so it stops the AirPods' mic stream
    // before the call audio link is up: both at once make the controller fail.
    private static void SetCallAudioLocked(Link link, bool on, string why)
    {
        if (link.CallAudio == on)
        {
            return;
        }
        link.CallAudio = on;
        Log.Info("hci", $"{link.Address:X12}: call audio {(on ? "starting" : "ended")} ({why})");
        if (link.Aap is { IsOpen: true } ch)
        {
            ch.SendCallAudio(on);
        }
    }

    private Link? FindLinkLocked(ulong address)
    {
        foreach (Link link in _links.Values)
        {
            if (!link.Le && link.Address == address)
            {
                return link;
            }
        }
        return null;
    }

    private bool HasScoLocked(ulong address)
    {
        foreach (ulong a in _scoLinks.Values)
        {
            if (a == address)
            {
                return true;
            }
        }
        return false;
    }

    // Commands whose parameters hold a key: Link_Key / PIN_Code request replies, Remote_OOB_(Extended_)Data request
    // replies, LE_Encrypt, LE_Enable_Encryption, LE_Long_Term_Key_Request_Reply, LE_Add_Device_To_Resolving_List (IRKs).
    private static bool CarriesKey(ushort opcode) =>
        opcode is 0x040B or 0x040D or 0x0430 or 0x0445 or 0x2017 or 0x2019 or 0x201A or 0x2027;

    // Command bytes are logged only for the signaling channels (BR/EDR 1, LE 5); other channels' data
    // (audio, input, pairing, files) never is.
    private static string L2capText(ReadOnlySpan<byte> l2, int pb)
    {
        if (pb == 1 || l2.Length < 4)
        {
            return "";
        }
        ushort cid = BinaryPrimitives.ReadUInt16LittleEndian(l2[2..]);
        return cid is 0x0001 or 0x0005
            ? $" cid 0x{cid:X4} {Convert.ToHexString(l2[4..Math.Min(l2.Length, 36)])}"
            : $" cid 0x{cid:X4}";
    }

    // Skips advertising reports and Number_Of_Completed_Packets (logged per link); events and answers carrying a key
    // or secret are logged by code only.
    private static void LogEvent(byte[] e)
    {
        if (e[0] == 0x13 || (e[0] == 0x3E && e.Length >= 3 && e[2] is 0x02 or 0x0B or 0x0D))
        {
            return;
        }
        // Return_Link_Keys, Link_Key_Notification, LE Generate_DHKey_Complete.
        bool secret = e[0] is 0x15 or 0x18 || (e[0] == 0x3E && e.Length >= 3 && e[2] == 0x09);
        if (e[0] == 0x0E && e.Length >= 6)
        {
            ushort opcode = BinaryPrimitives.ReadUInt16LittleEndian(e.AsSpan(3));
            Log.Info("hci", $"command complete 0x{opcode:X4} status {e[5]} {(AnswerCarriesSecret(opcode) ? "(return parameters not logged)" : Convert.ToHexString(e.AsSpan(6, Math.Min(e.Length - 6, 32))))}");
        }
        else if (e[0] == 0x0F && e.Length >= 6)
        {
            Log.Info("hci", $"command status 0x{BinaryPrimitives.ReadUInt16LittleEndian(e.AsSpan(4)):X4} status {e[2]}");
        }
        else
        {
            Log.Info("hci", $"event 0x{e[0]:X2}{(e[0] == 0x3E && e.Length >= 3 ? $"/{e[2]:X2}" : "")} {(secret ? "(parameters not logged: they carry a key)" : Convert.ToHexString(e.AsSpan(2, Math.Min(e.Length - 2, 32))))}");
        }
    }

    // Command answers with secrets: Read_Local_OOB_(Extended_)Data (pairing values), LE_Encrypt, LE_Rand.
    private static bool AnswerCarriesSecret(ushort opcode) => opcode is 0x0C57 or 0x0C7D or 0x2017 or 0x2018;

    // BD_ADDR as HCI sends it: 6 bytes, least significant first.
    private static ulong ReadAddress(ReadOnlySpan<byte> b)
    {
        ulong address = 0;
        for (int i = 0; i < 6; i++)
        {
            address |= (ulong)b[i] << (8 * i);
        }
        return address;
    }

    private static void EndChannelLocked(Link link, int error)
    {
        if (link.Aap is { } ch)
        {
            DropLocked(ch);
            ch.End(error);
        }
    }

    /// <summary>One ACL link (BR/EDR, or LE when <see cref="Le"/>) as the controller reported it.</summary>
    internal sealed class Link(int handle, ulong address, bool le)
    {
        public int Handle { get; } = handle;
        public ulong Address { get; } = address;
        public bool Le { get; } = le;
        public bool Encrypted;
        /// <summary>Call audio (SCO/eSCO) is being set up or is up on this link.</summary>
        public bool CallAudio;
        /// <summary>Per packet sent and not yet completed, in order: true = ours.</summary>
        public readonly LinkedList<bool> Owners = new();
        public int WindowsRemaining;
        public bool InboundOurs;
        public byte[]? RxPdu;
        public int RxHave;
        public AapChannel? Aap;
        /// <summary>Windows' cached SDP records of the device list AAP (PSM 0x1001): AirPods.</summary>
        public bool AapDevice;
        public HandsFreeDecline? Decline;
        public byte NextId = 0xC0;
        public readonly Dictionary<byte, Request> Ids = [];
    }

    /// <summary>
    /// Our pending signaling request; Channel is null once the app gave up and for the Hands-Free decline, Cid is our CID
    /// in the response.
    /// </summary>
    internal readonly record struct Request(AapChannel? Channel, byte Code, ushort Cid);

    /// <summary>Our RFCOMM channel to the AirPods' Hands-Free service (see PrepareDeclineLocked); hidden from Windows.</summary>
    internal sealed class HandsFreeDecline(ushort localCid, byte channel)
    {
        public ushort LocalCid { get; } = localCid;
        /// <summary>The AirPods' Hands-Free server channel as seen from the session initiator (direction bit 0).</summary>
        public byte Dlci { get; } = (byte)(channel << 1);
        public ushort RemoteCid;
        public bool RemoteConfigured;
        public bool LocalConfigured;
        public DeclineStep Step;
    }

    internal enum DeclineStep
    {
        /// <summary>For encryption.</summary>
        Waiting,
        /// <summary>L2CAP connection and configuration.</summary>
        Connecting,
        /// <summary>SABM on DLC 0 sent.</summary>
        Mux,
        /// <summary>Parameter Negotiation (credit-based flow control) for the Hands-Free DLC sent.</summary>
        Pn,
        /// <summary>SABM on the Hands-Free DLC sent.</summary>
        Dlc,
        /// <summary>Hands-Free DLC up, waiting for the AirPods' first AT command.</summary>
        Open,
        /// <summary>DISC on the Hands-Free DLC sent.</summary>
        ClosingDlc,
        /// <summary>DISC on DLC 0 sent.</summary>
        ClosingMux,
        /// <summary>L2CAP Disconnection Request sent.</summary>
        Closing,
    }
}

/// <summary>One AAP channel per app connection; <see cref="Messages"/> holds framed pipe messages for the app.</summary>
internal sealed class AapChannel
{
    private readonly Channel<byte[]> _messages = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });

    public AapChannel(HciProxy.Link link, ushort localCid)
    {
        Link = link;
        LocalCid = localCid;
    }

    public HciProxy.Link Link { get; }
    public ushort LocalCid { get; }
    public ushort RemoteCid { get; set; }
    public bool RequestSent { get; set; }
    public bool RemoteConfigured { get; set; }
    public bool LocalConfigured { get; set; }
    public bool IsOpen { get; set; }
    public TaskCompletionSource<int> Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ChannelReader<byte[]> Messages => _messages.Reader;

    // Mic audio queued for the app is capped (~30 ms per packet): an app slower than real time gets dropped audio
    // instead of an ever-growing delay. Control packets always pass.
    private const int MaxQueuedAudio = 4;
    private int _queuedAudio;
    private long _droppedAudio;

    public void Deliver(byte[] packet)
    {
        bool audio = IsAudio(packet);
        if (audio && Interlocked.Increment(ref _queuedAudio) > MaxQueuedAudio)
        {
            Interlocked.Decrement(ref _queuedAudio);
            long dropped = Interlocked.Increment(ref _droppedAudio);
            if (dropped == 1 || dropped % 100 == 0)
            {
                Log.Warn("aap", $"the app reads the mic slower than real time: {dropped} audio packets dropped so far");
            }
            return;
        }
        var m = new byte[1 + packet.Length];
        m[0] = Protocol.AapData;
        packet.CopyTo(m, 1);
        _messages.Writer.TryWrite(m);
    }

    /// <summary>Queues the call audio (Hands-Free) state of the channel's link for the app.</summary>
    public void SendCallAudio(bool on) => _messages.Writer.TryWrite([Protocol.AapCallAudio, on ? (byte)1 : (byte)0]);

    /// <summary>A message from <see cref="Messages"/> reached the app's pipe.</summary>
    public void Sent(byte[] message)
    {
        if (message.Length > 1 && message[0] == Protocol.AapData && IsAudio(message.AsSpan(1)))
        {
            Interlocked.Decrement(ref _queuedAudio);
        }
    }

    // AAP 0x58 inbound mic audio: 04 00 04 00 58 00 01 00 ...
    private static bool IsAudio(ReadOnlySpan<byte> p) =>
        p.Length >= 8 && p[0] == 0x04 && p[2] == 0x04 && p[4] == 0x58 && p[5] == 0x00 && p[6] == 0x01;

    /// <summary>Ends the channel: an open fails with <paramref name="error"/>, an open channel reports it to the app.</summary>
    public void End(int error)
    {
        if (Opened.TrySetResult(error == 0 ? Protocol.ErrorNotConnected : error))
        {
            _messages.Writer.TryComplete();
            return;
        }
        var m = new byte[5];
        m[0] = Protocol.AapEnded;
        BinaryPrimitives.WriteInt32LittleEndian(m.AsSpan(1), error);
        _messages.Writer.TryWrite(m);
        _messages.Writer.TryComplete();
    }
}

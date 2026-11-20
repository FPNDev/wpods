using System.Buffers.Binary;

namespace WPods.Service;

/// <summary>What the radio link needs to stream a voice link of ours: its SCO handle and packet shape.</summary>
internal sealed record VoiceLinkInfo(int Handle, int TxLength, bool Transparent);

/// <summary>One eSCO setup to try: HFP codec and the Setup_Synchronous_Connection parameters for it.</summary>
internal readonly record struct VoiceAttempt(int Codec, uint Bandwidth, ushort MaxLatency, ushort VoiceSetting, byte Retransmission, ushort PacketTypes);

internal enum VoiceStep
{
    /// <summary>+BCS sent, waiting for the AirPods' AT+BCS.</summary>
    Selecting,
    /// <summary>Setup_Synchronous_Connection sent.</summary>
    Setting,
    /// <summary>The link is up; waiting for its first voice data.</summary>
    Checking,
    Up,
    /// <summary>Disconnect sent.</summary>
    Stopping,
}

/// <summary>Our voice link on an ACL link, for the app channel that asked for it.</summary>
internal sealed class VoiceLink(AapChannel owner, List<VoiceAttempt> attempts)
{
    public AapChannel Owner { get; } = owner;
    public List<VoiceAttempt> Attempts { get; } = attempts;
    public int Index;
    public VoiceStep Step;
    public int Handle = -1;
    public int Slots;
    public int RxLength;
    public int TxLength;
    /// <summary>Bumped on every step, so a timer of an earlier step does nothing.</summary>
    public int Generation;
    /// <summary>The app stopped it while a setup was in flight.</summary>
    public bool StopWanted;
    /// <summary>Our Disconnect was sent to try the next attempt, not to stop.</summary>
    public bool Retrying;
    /// <summary>Waiting for the AirPods to suspend Windows' A2DP stream before the first attempt.</summary>
    public bool WaitingForA2dp;

    public VoiceAttempt Current => Attempts[Index];
}

/// <summary>
/// HD voice (experimental): for AirPods with HD voice on and Windows' Hands-Free off, the service is their Hands-Free
/// audio gateway and runs the voice link (eSCO) itself, hidden from Windows, so the app gets the voice codecs the
/// AirPods offer (Apple's LC3 and AAC-ELD ones too) and processes the mic like the AAP stream.
/// </summary>
internal sealed partial class HciProxy
{
    // Codec preference and eSCO settings: latency and retransmission "don't care" with all packet types let the AirPods
    // pick the interval for Apple's codecs. A codec's later entries are tried after an earlier one failed.
    private static readonly VoiceAttempt[] VoiceAttempts =
    [
        new(127, 0x1F40, 0xFFFF, 0x0003, 0xFF, 0x003F),
        // AAC-ELD (128, 129) is off until its 180-byte packets come through an adapter and its framing is known.
        // new(128, 0x1F40, 0xFFFF, 0x0003, 0xFF, 0x003F),
        // new(128, 0x3E80, 0xFFFF, 0x0003, 0xFF, 0x003F),
        // new(129, 0x1F40, 0xFFFF, 0x0003, 0xFF, 0x003F),
        // new(129, 0x3E80, 0xFFFF, 0x0003, 0xFF, 0x003F),
        new(256, 0x1F40, 0xFFFF, 0x0003, 0xFF, 0x003F),
        new(2, 0x1F40, 0x000D, 0x0003, 0x02, 0x0380),
        new(2, 0x1F40, 0x0008, 0x0003, 0x02, 0x03C8),
        new(1, 0x1F40, 0x000C, 0x0060, 0x02, 0x0380),
        new(1, 0x1F40, 0x000A, 0x0060, 0x01, 0x03C8),
        new(1, 0x1F40, 0xFFFF, 0x0060, 0xFF, 0x003F),
    ];
    private static readonly TimeSpan CodecAnswerTimeout = TimeSpan.FromSeconds(2);
    // A link that carries no data (an adapter that cannot pass its packet size) is given up after this.
    private static readonly TimeSpan FirstVoiceDataTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan[] GatewayRechecks = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(6)];

    private Action<byte[]>? _sendCommand;
    // Opcode of our HCI command waiting for its status (0 = none).
    private int _ownOpcode;
    // The ACL link our voice link runs on (one at a time).
    private Link? _voiceLink;

    /// <summary>HD voice is on for the pair (set by the engine).</summary>
    public Func<ulong, bool> HdVoiceOn { get; set; } = static _ => false;

    /// <summary>Our voice link came up (its info) or went down (null). Raised under the proxy lock; return at once.</summary>
    public event Action<VoiceLinkInfo?>? VoiceLinkChanged;

    /// <summary>An HCI command of ours waits for its status: Windows' next command waits behind it.</summary>
    public bool OwnCommandPending => Volatile.Read(ref _ownOpcode) != 0;

    /// <summary>Sets what sends our own HCI commands (opcode + length + parameters); null when the radio is detached.</summary>
    public void SetCommandSender(Action<byte[]>? send)
    {
        lock (_gate)
        {
            _sendCommand = send;
            Volatile.Write(ref _ownOpcode, 0);
        }
    }

    public void OwnCommandSent(ushort opcode) => Volatile.Write(ref _ownOpcode, opcode);

    /// <summary>HD voice was switched for the pair: a link that is up starts or ends its Hands-Free session.</summary>
    public void HdVoiceChanged(ulong address)
    {
        lock (_gate)
        {
            if (FindLinkLocked(address) is { } link)
            {
                link.GatewayRetries = 0;
                link.GatewayNotBefore = 0;
            }
            CheckGatewayLocked(address);
        }
        // Windows' Hands-Free state follows the app's change a moment later.
        foreach (TimeSpan delay in GatewayRechecks)
        {
            _ = RecheckGatewayAsync(address, delay);
        }
    }

    /// <summary>0xF1 (start/stop), 0xF2 (downlink data) or 0xF3 (silent unit) from the app on its AAP channel.</summary>
    public void OnVoiceMessage(AapChannel ch, ReadOnlySpan<byte> message)
    {
        switch (message[0])
        {
            case Protocol.VoiceControl when message.Length >= 2:
                lock (_gate)
                {
                    if (message[1] != 0)
                    {
                        StartVoiceLocked(ch);
                    }
                    else if (ch.Link.Voice is { } v && v.Owner == ch)
                    {
                        StopVoiceLocked(ch.Link, "the app stopped it");
                    }
                    else
                    {
                        ch.Link.VoiceWanted = null;
                    }
                }
                break;
            case Protocol.VoiceDownlink:
                ch.AddDownlink(message[1..]);
                break;
            case Protocol.VoiceSilence:
                ch.SetSilence(message[1..]);
                break;
        }
    }

    /// <summary>Uplink payloads of our voice link (radio link's iso thread).</summary>
    public void OnVoiceIn(int handle, ReadOnlySpan<byte> payload)
    {
        lock (_gate)
        {
            if (_voiceLink?.Voice is not { } v || v.Handle != handle || payload.Length == 0)
            {
                return;
            }
            if (v.Step == VoiceStep.Checking)
            {
                v.Step = VoiceStep.Up;
                v.Generation++;
                Log.Info("hfp", $"{_voiceLink.Address:X12}: voice link up, codec {v.Current.Codec}, every {v.Slots} slots, {v.RxLength}/{v.TxLength} bytes");
                v.Owner.SendVoiceState(1, v.Current.Codec, v.Slots, v.RxLength, v.TxLength);
            }
            if (v.Step == VoiceStep.Up)
            {
                v.Owner.SendVoiceData(payload);
            }
        }
    }

    /// <summary>The next <paramref name="length"/> bytes to send on our voice link (radio link's iso thread).</summary>
    public byte[] TakeVoiceOut(int length)
    {
        AapChannel? owner;
        lock (_gate)
        {
            owner = _voiceLink?.Voice?.Owner;
        }
        return owner?.TakeDownlink(length) ?? new byte[length];
    }

    // Never several tries a second: refused tries in a row can leave the AirPods refusing Hands-Free until they are put
    // back in their case.
    private static readonly TimeSpan[] GatewayRetryDelays =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(30)];
    private static readonly TimeSpan GatewayRecheckDelay = TimeSpan.FromSeconds(1);

    // Our Hands-Free channel ended (declined, refused or closed): with HD voice on, ours is tried again after a pause.
    private void DeclineEndedLocked(Link link, HandsFreeDecline decline)
    {
        link.Decline = null;
        if (!HdVoiceOn(link.Address))
        {
            return;
        }
        if (decline.Gateway is null)
        {
            // HD voice came on while the startup decline ran.
            _ = RecheckGatewayAsync(link.Address, GatewayRecheckDelay);
            return;
        }
        if (link.GatewayRetries >= GatewayRetryDelays.Length)
        {
            Log.Warn("hfp", $"{link.Address:X12}: HD voice: Hands-Free not set up after {GatewayRetryDelays.Length} tries");
            return;
        }
        TimeSpan delay = GatewayRetryDelays[link.GatewayRetries++];
        HoldGatewayLocked(link, delay);
        _ = RecheckGatewayAsync(link.Address, delay);
    }

    // Other rechecks (HD voice change, Windows' channel closing) wait for the pause too.
    private static void HoldGatewayLocked(Link link, TimeSpan delay) =>
        link.GatewayNotBefore = Math.Max(link.GatewayNotBefore, Environment.TickCount64 + (long)delay.TotalMilliseconds);

    // Windows' own RFCOMM (Hands-Free) channels on the link: ours starts only once they are gone.
    private void NoteWindowsRfcommLocked(Link link, ReadOnlySpan<byte> sig)
    {
        for (int at = 0; at + 4 <= sig.Length;)
        {
            byte code = sig[at];
            byte id = sig[at + 1];
            int length = BinaryPrimitives.ReadUInt16LittleEndian(sig[(at + 2)..]);
            ReadOnlySpan<byte> d = sig.Slice(at + 4, Math.Min(length, sig.Length - at - 4));
            switch (code)
            {
                case 0x02 when d.Length >= 4:
                    {
                        // A new request with this id or this CID: an earlier RFCOMM one under it was refused.
                        ushort cid = BinaryPrimitives.ReadUInt16LittleEndian(d[2..]);
                        foreach ((ushort remote, ushort local) in link.RfcommAnswered)
                        {
                            if (local == cid)
                            {
                                link.RfcommAnswered.Remove(remote);
                                break;
                            }
                        }
                        if (BinaryPrimitives.ReadUInt16LittleEndian(d) == RfcommPsm)
                        {
                            link.RfcommRequests[id] = cid;
                        }
                        else
                        {
                            link.RfcommRequests.Remove(id);
                        }
                        break;
                    }
                // Windows answers the AirPods' channel ("pending" comes first): DCID is Windows' end, SCID theirs.
                case 0x03 when d.Length >= 6 && BinaryPrimitives.ReadUInt16LittleEndian(d[4..]) != 1
                    && link.RfcommIncoming.Remove(BinaryPrimitives.ReadUInt16LittleEndian(d[2..])):
                    if (BinaryPrimitives.ReadUInt16LittleEndian(d[4..]) == 0)
                    {
                        AddWindowsRfcommLocked(link, BinaryPrimitives.ReadUInt16LittleEndian(d));
                    }
                    break;
                // Windows configures the channel the AirPods accepted: DCID is their end.
                case 0x04 when d.Length >= 2 && link.RfcommAnswered.Remove(BinaryPrimitives.ReadUInt16LittleEndian(d), out ushort windows):
                    AddWindowsRfcommLocked(link, windows);
                    break;
                case 0x06 when d.Length >= 4:
                    RemoveWindowsRfcommLocked(link, BinaryPrimitives.ReadUInt16LittleEndian(d[2..]));
                    break;
                // Windows answers the AirPods' Disconnection Request, also one not seen inbound (only the first command
                // of a PDU passed on whole is): DCID is Windows' end.
                case 0x07 when d.Length >= 2:
                    RemoveWindowsRfcommLocked(link, BinaryPrimitives.ReadUInt16LittleEndian(d));
                    break;
            }
            at += 4 + length;
        }
    }

    /// <summary>One inbound L2CAP signaling command for Windows (at least 4 data bytes): the AirPods' side of Windows' RFCOMM.</summary>
    private void NoteInboundRfcommLocked(Link link, byte code, byte id, ReadOnlySpan<byte> d)
    {
        switch (code)
        {
            // The AirPods answer Windows' request. Their result lies past the bytes seen here: the channel counts once
            // Windows configures it (refusals name no CID).
            case 0x03 when BinaryPrimitives.ReadUInt16LittleEndian(d) is var remote and not 0 && link.RfcommRequests.Remove(id, out ushort windows):
                link.RfcommAnswered[remote] = windows;
                break;
            case 0x02 when BinaryPrimitives.ReadUInt16LittleEndian(d) == RfcommPsm:
                link.RfcommIncoming.Add(BinaryPrimitives.ReadUInt16LittleEndian(d[2..]));
                break;
            case 0x06:
                RemoveWindowsRfcommLocked(link, BinaryPrimitives.ReadUInt16LittleEndian(d));
                break;
        }
    }

    private static void AddWindowsRfcommLocked(Link link, ushort cid)
    {
        link.WindowsRfcomm.Add(cid);
        Log.Info("hfp", $"{link.Address:X12}: Windows' RFCOMM channel 0x{cid:X4} open");
    }

    private void RemoveWindowsRfcommLocked(Link link, ushort cid)
    {
        if (!link.WindowsRfcomm.Remove(cid))
        {
            return;
        }
        Log.Info("hfp", $"{link.Address:X12}: Windows' RFCOMM channel 0x{cid:X4} closing");
        if (link.WindowsRfcomm.Count == 0 && HdVoiceOn(link.Address))
        {
            HoldGatewayLocked(link, GatewayRecheckDelay);
            _ = RecheckGatewayAsync(link.Address, GatewayRecheckDelay);
        }
    }

    private async Task RecheckGatewayAsync(ulong address, TimeSpan delay)
    {
        await Task.Delay(delay).ConfigureAwait(false);
        lock (_gate)
        {
            CheckGatewayLocked(address);
        }
    }

    // Starts our Hands-Free session where HD voice is on and Windows' Hands-Free off, ends it where HD voice went off.
    private void CheckGatewayLocked(ulong address)
    {
        if (FindLinkLocked(address) is not { AapDevice: true } link)
        {
            return;
        }
        bool wanted = HdVoiceOn(address) && BluetoothRegistry.HandsFreeEnabled(_localAddress, address) == false;
        if (!wanted && link.Decline is { Gateway: not null } running)
        {
            Log.Info("hfp", $"{address:X12}: HD voice off: ending our Hands-Free session");
            StopVoiceLocked(link, "HD voice off");
            CloseDeclineLocked(link, running);
            return;
        }
        if (!wanted || link.Decline is not null || !link.Encrypted)
        {
            return;
        }
        if (link.WindowsRfcomm.Count > 0)
        {
            // Rechecked once Windows' channel closed (RemoveWindowsRfcommLocked).
            Log.Info("hfp", $"{address:X12}: HD voice: waiting for Windows' Hands-Free to close");
            return;
        }
        // The recheck scheduled for the end of the pause may fire a timer tick early.
        if (Environment.TickCount64 < link.GatewayNotBefore - 50)
        {
            return;
        }
        (_, byte channel) = BluetoothRegistry.ReadServices(address);
        if (channel == 0)
        {
            Log.Info("hfp", $"{address:X12}: HD voice: no cached Hands-Free channel");
            return;
        }
        var decline = new HandsFreeDecline(NextCidLocked(), channel) { Gateway = new HandsFreeAg() };
        link.Decline = decline;
        Log.Info("hfp", $"{address:X12}: HD voice: running Hands-Free on RFCOMM channel {channel}");
        ConnectDeclineLocked(link, decline);
    }

    private void OnGatewayDataLocked(Link link, HandsFreeDecline hf, HandsFreeAg ag, ReadOnlySpan<byte> info)
    {
        hf.Received++;
        foreach ((byte[] reply, AgEvent e, string command) in ag.Feed(info))
        {
            Log.Info("hfp", $"{link.Address:X12}: AT in: {command}");
            SendGatewayLocked(link, hf, reply);
            switch (e)
            {
                case AgEvent.Connected:
                    Log.Info("hfp", $"{link.Address:X12}: Hands-Free connected, codecs {string.Join(",", ag.Codecs)}");
                    if (link.VoiceWanted is { } waiting)
                    {
                        link.VoiceWanted = null;
                        StartVoiceLocked(waiting);
                    }
                    break;
                case AgEvent.CodecConfirmed when link.Voice is { Step: VoiceStep.Selecting } v && v.Current.Codec == ag.Codec:
                    SetupVoiceLocked(link, v);
                    break;
                case AgEvent.CodecRefused when link.Voice is { Step: VoiceStep.Selecting } v:
                    Log.Info("hfp", $"{link.Address:X12}: codec {v.Current.Codec} refused");
                    NextAttemptLocked(link, v, sameCodec: false);
                    break;
            }
        }
    }

    // Each reply gives back the credits of the AirPods' frames received since the last one; a reply longer than the
    // negotiated frame size (AT+CIND=?) goes in several frames.
    private void SendGatewayLocked(Link link, HandsFreeDecline hf, byte[] text)
    {
        for (int at = 0; at < text.Length; at += Rfcomm.MaxFrame)
        {
            ReadOnlySpan<byte> part = text.AsSpan(at, Math.Min(Rfcomm.MaxFrame, text.Length - at));
            byte credits = (byte)Math.Min(hf.Received, 255);
            hf.Received -= credits;
            if (--hf.RemoteCredits < 0)
            {
                Log.Warn("hfp", $"{link.Address:X12}: no RFCOMM credits left from the AirPods, sending anyway");
            }
            SendL2capLocked(link, hf.RemoteCid, credits > 0 ? Rfcomm.DataWithCredits(hf.Dlci, credits, part) : Rfcomm.Data(hf.Dlci, part));
        }
    }

    private void StartVoiceLocked(AapChannel ch)
    {
        Link link = ch.Link;
        if (link.Voice is { } running)
        {
            running.StopWanted = false;
            return;
        }
        if (link.Decline is not { Gateway: { } ag } || !ag.Connected)
        {
            // Started once Hands-Free is connected.
            link.VoiceWanted = ch;
            Log.Info("hfp", $"{link.Address:X12}: voice link wanted, Hands-Free not connected yet");
            return;
        }
        if (_voiceLink is not null || _scoLinks.Count > 0)
        {
            Log.Warn("hfp", $"{link.Address:X12}: voice link not started: another call audio link is up");
            ch.SendVoiceState(2, 0, 0, 0, 0);
            return;
        }
        // CVSD is mandatory for every Hands-Free device, the others only when the AirPods listed them.
        var attempts = new List<VoiceAttempt>();
        foreach (VoiceAttempt a in VoiceAttempts)
        {
            if (a.Codec == 1 || ag.Codecs.Contains(a.Codec))
            {
                attempts.Add(a);
            }
        }
        var v = new VoiceLink(ch, attempts);
        link.Voice = v;
        _voiceLink = link;
        Log.Info("hfp", $"{link.Address:X12}: starting the voice link, trying codecs {string.Join(",", attempts.Select(a => a.Codec).Distinct())}");
        if (HoldA2dpLocked(link))
        {
            v.WaitingForA2dp = true;
            return;
        }
        TryAttemptLocked(link, v);
    }

    private void ContinueVoiceAfterSuspendLocked(Link link)
    {
        if (link.Voice is { WaitingForA2dp: true } v)
        {
            v.WaitingForA2dp = false;
            TryAttemptLocked(link, v);
        }
    }

    private void TryAttemptLocked(Link link, VoiceLink v)
    {
        if (v.StopWanted)
        {
            EndVoiceLocked(link, v, 0, "stopped");
            return;
        }
        if (v.Index >= v.Attempts.Count)
        {
            EndVoiceLocked(link, v, 2, "no codec worked");
            return;
        }
        HandsFreeDecline? hf = link.Decline;
        if (hf?.Gateway is not { } ag || hf.Step != DeclineStep.Open)
        {
            EndVoiceLocked(link, v, 2, "Hands-Free closed");
            return;
        }
        VoiceAttempt a = v.Current;
        if (ag.Codec == a.Codec && v.Index > 0 && v.Attempts[v.Index - 1].Codec == a.Codec)
        {
            SetupVoiceLocked(link, v);
            return;
        }
        v.Step = VoiceStep.Selecting;
        int generation = ++v.Generation;
        Log.Info("hfp", $"{link.Address:X12}: selecting codec {a.Codec}");
        SendGatewayLocked(link, hf, ag.SelectCodec(a.Codec));
        _ = VoiceTimeoutAsync(link, v, generation, CodecAnswerTimeout, "no codec answer");
    }

    private void SetupVoiceLocked(Link link, VoiceLink v)
    {
        VoiceAttempt a = v.Current;
        v.Step = VoiceStep.Setting;
        v.Generation++;
        var command = new byte[3 + 17];
        BinaryPrimitives.WriteUInt16LittleEndian(command, 0x0428);
        command[2] = 17;
        Span<byte> p = command.AsSpan(3);
        BinaryPrimitives.WriteUInt16LittleEndian(p, (ushort)link.Handle);
        BinaryPrimitives.WriteUInt32LittleEndian(p[2..], a.Bandwidth);
        BinaryPrimitives.WriteUInt32LittleEndian(p[6..], a.Bandwidth);
        BinaryPrimitives.WriteUInt16LittleEndian(p[10..], a.MaxLatency);
        BinaryPrimitives.WriteUInt16LittleEndian(p[12..], a.VoiceSetting);
        p[14] = a.Retransmission;
        BinaryPrimitives.WriteUInt16LittleEndian(p[15..], a.PacketTypes);
        Log.Info("hfp", $"{link.Address:X12}: voice link setup, codec {a.Codec}: {Convert.ToHexString(p)}");
        SendOwnCommandLocked(command);
    }

    private void SendOwnCommandLocked(byte[] command) => _sendCommand?.Invoke(command);

    // Moves to the next attempt: the same codec's next settings, or (sameCodec false) the next codec.
    private void NextAttemptLocked(Link link, VoiceLink v, bool sameCodec)
    {
        int codec = v.Current.Codec;
        v.Index++;
        if (!sameCodec)
        {
            while (v.Index < v.Attempts.Count && v.Attempts[v.Index].Codec == codec)
            {
                v.Index++;
            }
        }
        TryAttemptLocked(link, v);
    }

    private async Task VoiceTimeoutAsync(Link link, VoiceLink v, int generation, TimeSpan delay, string why)
    {
        await Task.Delay(delay).ConfigureAwait(false);
        lock (_gate)
        {
            if (link.Voice != v || v.Generation != generation)
            {
                return;
            }
            Log.Info("hfp", $"{link.Address:X12}: codec {v.Current.Codec}: {why}");
            if (v.Step == VoiceStep.Checking)
            {
                // The link carries nothing (e.g. packets larger than the adapter passes): closed, then the next one.
                v.Retrying = true;
                DisconnectVoiceLocked(v);
                return;
            }
            NextAttemptLocked(link, v, sameCodec: false);
        }
    }

    private void DisconnectVoiceLocked(VoiceLink v)
    {
        v.Step = VoiceStep.Stopping;
        v.Generation++;
        var command = new byte[3 + 3];
        BinaryPrimitives.WriteUInt16LittleEndian(command, 0x0406);
        command[2] = 3;
        BinaryPrimitives.WriteUInt16LittleEndian(command.AsSpan(3), (ushort)v.Handle);
        command[5] = 0x13;
        SendOwnCommandLocked(command);
    }

    private void StopVoiceLocked(Link link, string why)
    {
        link.VoiceWanted = null;
        if (link.Voice is not { } v)
        {
            return;
        }
        Log.Info("hfp", $"{link.Address:X12}: stopping the voice link ({why})");
        switch (v.Step)
        {
            case VoiceStep.Checking or VoiceStep.Up:
                v.Retrying = false;
                DisconnectVoiceLocked(v);
                break;
            case VoiceStep.Setting:
                v.StopWanted = true;
                break;
            case VoiceStep.Stopping:
                // A retry's Disconnect is in flight: the link ends once it is down instead of trying the next.
                v.Retrying = false;
                break;
            case VoiceStep.Selecting:
                EndVoiceLocked(link, v, 0, why);
                break;
        }
    }

    private void EndVoiceLocked(Link link, VoiceLink v, byte state, string why)
    {
        if (link.Voice != v)
        {
            return;
        }
        link.Voice = null;
        if (_voiceLink == link)
        {
            _voiceLink = null;
        }
        if (_links.TryGetValue(link.Handle, out Link? current) && current == link)
        {
            ReleaseA2dpLocked(link);
        }
        else
        {
            link.A2dpHeld = false;
        }
        Log.Info("hfp", $"{link.Address:X12}: voice link {(state == 2 ? "failed" : "down")} ({why})");
        v.Owner.SendVoiceState(state, v.Index < v.Attempts.Count ? v.Current.Codec : 0, 0, 0, 0);
    }

    // Our command's status, our link's Synchronous_Connection_Complete and Disconnection_Complete: consumed (Windows
    // never asked for them). Null = handled here.
    private byte[]? OnVoiceEventLocked(byte[] e)
    {
        int answered = e[0] == 0x0F && e.Length >= 6 ? BinaryPrimitives.ReadUInt16LittleEndian(e.AsSpan(4))
            : e[0] == 0x0E && e.Length >= 6 ? BinaryPrimitives.ReadUInt16LittleEndian(e.AsSpan(3)) : -1;
        int own = Volatile.Read(ref _ownOpcode);
        if (own != 0 && answered == own)
        {
            Volatile.Write(ref _ownOpcode, 0);
            OnOwnStatusLocked(own, e[0] == 0x0F ? e[2] : e[5]);
            return null;
        }
        if (_voiceLink is not { Voice: { } voice } owner)
        {
            return e;
        }
        if (e[0] == 0x2C && e.Length >= 19 && voice.Step == VoiceStep.Setting && ReadAddress(e.AsSpan(5)) == owner.Address)
        {
            if (e[2] != 0)
            {
                Log.Info("hfp", $"{owner.Address:X12}: codec {voice.Current.Codec}: setup failed, status 0x{e[2]:X2}");
                NextAttemptLocked(owner, voice, sameCodec: true);
                return null;
            }
            voice.Handle = BinaryPrimitives.ReadUInt16LittleEndian(e.AsSpan(3)) & 0x0FFF;
            voice.Slots = e[12];
            voice.RxLength = BinaryPrimitives.ReadUInt16LittleEndian(e.AsSpan(14));
            voice.TxLength = BinaryPrimitives.ReadUInt16LittleEndian(e.AsSpan(16));
            voice.Step = VoiceStep.Checking;
            int generation = ++voice.Generation;
            Log.Info("hfp", $"{owner.Address:X12}: codec {voice.Current.Codec}: eSCO 0x{voice.Handle:X3}, every {voice.Slots} slots, {voice.RxLength}/{voice.TxLength} bytes, air mode {e[18]}");
            VoiceLinkChanged?.Invoke(new VoiceLinkInfo(voice.Handle, voice.TxLength, voice.Current.VoiceSetting == 0x0003));
            if (voice.StopWanted)
            {
                DisconnectVoiceLocked(voice);
            }
            else
            {
                _ = VoiceTimeoutAsync(owner, voice, generation, FirstVoiceDataTimeout, "no voice data");
            }
            return null;
        }
        if (e[0] == 0x05 && e.Length >= 6 && e[2] == 0 && (BinaryPrimitives.ReadUInt16LittleEndian(e.AsSpan(3)) & 0x0FFF) == voice.Handle)
        {
            VoiceLinkChanged?.Invoke(null);
            voice.Handle = -1;
            if (voice.Retrying && !voice.StopWanted)
            {
                voice.Retrying = false;
                NextAttemptLocked(owner, voice, sameCodec: true);
            }
            else
            {
                EndVoiceLocked(owner, voice, 0, $"link closed, reason 0x{e[5]:X2}");
            }
            return null;
        }
        return e;
    }

    /// <summary>Our command could not be written to the radio: handled as refused.</summary>
    public void OwnCommandFailed(ushort opcode)
    {
        lock (_gate)
        {
            Volatile.Write(ref _ownOpcode, 0);
            // Unspecified Error.
            OnOwnStatusLocked(opcode, 0x1F);
        }
    }

    private void OnOwnStatusLocked(int opcode, byte status)
    {
        if (_voiceLink is not { Voice: { } v } link || status == 0)
        {
            return;
        }
        if (opcode == 0x0428 && v.Step == VoiceStep.Setting)
        {
            Log.Info("hfp", $"{link.Address:X12}: codec {v.Current.Codec}: setup refused at once, status 0x{status:X2}");
            NextAttemptLocked(link, v, sameCodec: true);
        }
        else if (opcode == 0x0406 && v.Step == VoiceStep.Stopping)
        {
            VoiceLinkChanged?.Invoke(null);
            EndVoiceLocked(link, v, 0, $"disconnect refused, status 0x{status:X2}");
        }
    }

    private void EndVoiceOnLinkLossLocked(Link link)
    {
        link.VoiceWanted = null;
        if (link.Voice is { } v)
        {
            if (v.Handle >= 0)
            {
                VoiceLinkChanged?.Invoke(null);
            }
            EndVoiceLocked(link, v, 0, "ACL link lost");
        }
    }
}

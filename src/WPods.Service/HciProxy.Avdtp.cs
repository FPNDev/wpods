using System.Buffers.Binary;

namespace WPods.Service;

/// <summary>
/// HD voice: AirPods do not take Windows' A2DP stream and our voice link at once (a voice link setup times out while
/// it streams). While our voice link is up, Windows' A2DP stream to the pair is held: suspended at the AirPods with our
/// own AVDTP SUSPEND, Windows' media packets dropped (their buffers reported completed), and Windows' own START and
/// SUSPEND answered here; afterwards the stream is started again if Windows still plays.
/// </summary>
internal sealed partial class HciProxy
{
    private const ushort AvdtpPsm = 0x0019;
    private const byte AvdtpOpen = 0x06;
    private const byte AvdtpStart = 0x07;
    private const byte AvdtpClose = 0x08;
    private const byte AvdtpSuspend = 0x09;
    private const byte AvdtpSetConfiguration = 0x03;
    private static readonly TimeSpan AvdtpAnswerTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Windows' ACL OUT transfer: notes its AVDTP channels and stream state; true when it must not reach the radio (held
    /// media, or a START/SUSPEND answered here), in which case Windows gets it completed as sent.
    /// </summary>
    public bool FilterWindowsAcl(byte[] packets)
    {
        lock (_gate)
        {
            bool drop = true;
            bool any = false;
            ForEachPacket(packets, (handle, pb, payload) =>
            {
                any = true;
                if (!_links.TryGetValue(handle, out Link? link) || !OnWindowsPacketLocked(link, pb, payload))
                {
                    drop = false;
                }
            });
            if (!any || !drop)
            {
                return false;
            }
            ForEachPacket(packets, (handle, _, _) => CompleteDroppedLocked(handle));
            return true;
        }
    }

    // True when the packet is dropped.
    private bool OnWindowsPacketLocked(Link link, int pb, ReadOnlySpan<byte> payload)
    {
        if (pb == 1)
        {
            return link.DroppingPdu;
        }
        link.DroppingPdu = false;
        if (payload.Length < 4)
        {
            return false;
        }
        ushort cid = BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]);
        ReadOnlySpan<byte> data = payload[4..];
        if (cid == 0x0001)
        {
            NoteWindowsSignalingLocked(link, data);
            return false;
        }
        if (link.Avdtp is not { } a)
        {
            return false;
        }
        if (cid == a.MediaRemote && link.A2dpHeld)
        {
            link.DroppingPdu = BinaryPrimitives.ReadUInt16LittleEndian(payload) + 4 > payload.Length;
            return true;
        }
        if (cid != a.SignalRemote || data.Length < 2 || (data[0] & 0x0F) != 0)
        {
            // Not a single-packet AVDTP command.
            return false;
        }
        byte label = (byte)(data[0] >> 4);
        byte signal = (byte)(data[1] & 0x3F);
        a.LastWindowsLabel = label;
        if (signal is AvdtpSetConfiguration or AvdtpOpen or AvdtpStart or AvdtpSuspend && data.Length >= 3)
        {
            a.Seid = (byte)(data[2] >> 2);
        }
        switch (signal)
        {
            case AvdtpStart:
                a.WindowsStreaming = true;
                if (link.A2dpHeld)
                {
                    AnswerWindowsLocked(link, a, label, signal);
                    return true;
                }
                a.Streaming = true;
                break;
            case AvdtpSuspend:
                a.WindowsStreaming = false;
                if (link.A2dpHeld)
                {
                    AnswerWindowsLocked(link, a, label, signal);
                    return true;
                }
                a.Streaming = false;
                break;
            case AvdtpClose:
                a.WindowsStreaming = false;
                a.Streaming = false;
                break;
        }
        return false;
    }

    // L2CAP Connection Request / Response from Windows: which channels carry AVDTP (signaling first, then media).
    private static void NoteWindowsSignalingLocked(Link link, ReadOnlySpan<byte> sig)
    {
        for (int at = 0; at + 4 <= sig.Length;)
        {
            byte code = sig[at];
            byte id = sig[at + 1];
            int length = BinaryPrimitives.ReadUInt16LittleEndian(sig[(at + 2)..]);
            ReadOnlySpan<byte> d = sig.Slice(at + 4, Math.Min(length, sig.Length - at - 4));
            if (code == 0x02 && d.Length >= 4 && BinaryPrimitives.ReadUInt16LittleEndian(d) == AvdtpPsm)
            {
                link.AvdtpRequests[id] = BinaryPrimitives.ReadUInt16LittleEndian(d[2..]);
            }
            else if (code == 0x03 && d.Length >= 8 && BinaryPrimitives.ReadUInt16LittleEndian(d[4..]) == 0
                && link.AvdtpIncoming.Remove(BinaryPrimitives.ReadUInt16LittleEndian(d[2..])))
            {
                // Windows accepted the AirPods' AVDTP channel: DCID is Windows' end, SCID theirs.
                AddAvdtpChannel(link, BinaryPrimitives.ReadUInt16LittleEndian(d), BinaryPrimitives.ReadUInt16LittleEndian(d[2..]));
            }
            at += 4 + length;
        }
    }

    /// <summary>Inbound L2CAP signaling for Windows (start of the PDU): the AirPods' side of AVDTP channels.</summary>
    private void NoteInboundSignalingLocked(Link link, ReadOnlySpan<byte> sig)
    {
        if (sig.Length < 8)
        {
            return;
        }
        byte code = sig[0];
        byte id = sig[1];
        ReadOnlySpan<byte> d = sig[4..];
        if (code == 0x03 && link.AvdtpRequests.Remove(id, out ushort local))
        {
            // Connection Response to Windows' request: DCID is the AirPods' end (result checked by use).
            AddAvdtpChannel(link, local, BinaryPrimitives.ReadUInt16LittleEndian(d));
        }
        else if (code == 0x02 && BinaryPrimitives.ReadUInt16LittleEndian(d) == AvdtpPsm)
        {
            link.AvdtpIncoming.Add(BinaryPrimitives.ReadUInt16LittleEndian(d[2..]));
        }
    }

    private static void AddAvdtpChannel(Link link, ushort local, ushort remote)
    {
        if (link.Avdtp is not { } a || a.MediaRemote != 0)
        {
            // A new signaling channel starts a new stream session.
            link.Avdtp = new AvdtpState { SignalLocal = local, SignalRemote = remote };
            Log.Info("a2dp", $"{link.Address:X12}: AVDTP signaling 0x{local:X4}/0x{remote:X4}");
            return;
        }
        a.MediaRemote = remote;
        Log.Info("a2dp", $"{link.Address:X12}: AVDTP media 0x{local:X4}/0x{remote:X4}");
    }

    // An inbound AVDTP response to our own SUSPEND or START: never reaches Windows.
    private static bool IsOwnAvdtpAnswer(Link link, ushort cid, ReadOnlySpan<byte> l2data) =>
        link.Avdtp is { OwnLabel: >= 0 } a && cid == a.SignalLocal && l2data.Length >= 2
        && l2data[0] >> 4 == a.OwnLabel && (l2data[0] & 0x03) is 0x02 or 0x03;

    private void OnOwnAvdtpAnswerLocked(Link link, ReadOnlySpan<byte> l2data)
    {
        AvdtpState a = link.Avdtp!;
        a.OwnLabel = -1;
        bool accepted = (l2data[0] & 0x03) == 0x02;
        byte signal = (byte)(l2data[1] & 0x3F);
        Log.Info("a2dp", $"{link.Address:X12}: our AVDTP {(signal == AvdtpSuspend ? "SUSPEND" : "START")} {(accepted ? "accepted" : $"rejected, error 0x{(l2data.Length > 2 ? l2data[2] : 0):X2}")}");
        if (signal == AvdtpSuspend)
        {
            a.Streaming = !accepted;
            ContinueVoiceAfterSuspendLocked(link);
            if (accepted && !link.A2dpHeld && a.WindowsStreaming)
            {
                // The voice link ended while our SUSPEND was on its way: Windows still plays.
                SendOwnAvdtpLocked(link, a, AvdtpStart);
            }
        }
        else if (accepted)
        {
            a.Streaming = true;
        }
    }

    /// <summary>
    /// Holds the pair's A2DP stream for a voice link: true when a SUSPEND went out and the voice link waits for its
    /// answer (<see cref="ContinueVoiceAfterSuspendLocked"/>), false when it may start at once.
    /// </summary>
    private bool HoldA2dpLocked(Link link)
    {
        link.A2dpHeld = true;
        if (link.Avdtp is not { Streaming: true, Seid: > 0 } a)
        {
            return false;
        }
        SendOwnAvdtpLocked(link, a, AvdtpSuspend);
        return true;
    }

    // Lets the stream play again after a voice link: started again at the AirPods if Windows still plays.
    private void ReleaseA2dpLocked(Link link)
    {
        if (!link.A2dpHeld)
        {
            return;
        }
        link.A2dpHeld = false;
        link.DroppingPdu = false;
        if (link.Avdtp is { } a && a.WindowsStreaming && !a.Streaming && a.Seid > 0)
        {
            SendOwnAvdtpLocked(link, a, AvdtpStart);
        }
    }

    private void SendOwnAvdtpLocked(Link link, AvdtpState a, byte signal)
    {
        // A label Windows is unlikely to use next, so its answer is told apart from Windows' own.
        a.OwnLabel = (a.LastWindowsLabel + 8) & 0x0F;
        int sent = ++a.OwnSent;
        Log.Info("a2dp", $"{link.Address:X12}: our AVDTP {(signal == AvdtpSuspend ? "SUSPEND" : "START")} for SEID {a.Seid}");
        SendL2capLocked(link, a.SignalRemote, [(byte)(a.OwnLabel << 4), signal, (byte)(a.Seid << 2)]);
        _ = OwnAvdtpTimeoutAsync(link, a, signal, sent);
    }

    // No answer in time: the label is freed (else Windows' answer with it would be taken for ours). A SUSPEND counts as
    // done, so the stream is started again afterwards (a START to a stream that still plays is only refused).
    private async Task OwnAvdtpTimeoutAsync(Link link, AvdtpState a, byte signal, int sent)
    {
        await Task.Delay(AvdtpAnswerTimeout).ConfigureAwait(false);
        lock (_gate)
        {
            if (link.Avdtp != a || a.OwnLabel < 0 || a.OwnSent != sent)
            {
                return;
            }
            Log.Warn("a2dp", $"{link.Address:X12}: no answer to our AVDTP {(signal == AvdtpSuspend ? "SUSPEND" : "START")}");
            a.OwnLabel = -1;
            if (signal == AvdtpSuspend)
            {
                a.Streaming = false;
                ContinueVoiceAfterSuspendLocked(link);
            }
        }
    }

    // Answers Windows' START or SUSPEND as accepted while the stream is held.
    private void AnswerWindowsLocked(Link link, AvdtpState a, byte label, byte signal)
    {
        Log.Info("a2dp", $"{link.Address:X12}: Windows' AVDTP {(signal == AvdtpStart ? "START" : "SUSPEND")} answered here (stream held for the voice link)");
        var frame = new byte[4 + 4 + 2];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, (ushort)(link.Handle | (0b10 << 12)));
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), 4 + 2);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6), a.SignalLocal);
        frame[8] = (byte)((label << 4) | 0x02);
        frame[9] = signal;
        _aclOut.Add(frame);
        _aclOut.End();
    }

    // Windows counts every ACL packet it sends until the controller reports it completed: a dropped one is reported here.
    private void CompleteDroppedLocked(int handle)
    {
        if (!_links.TryGetValue(handle, out Link? link) || !Counted(link))
        {
            return;
        }
        byte[] e = [0x13, 5, 1, (byte)handle, (byte)(handle >> 8), 1, 0];
        _eventsOut.Add(e);
        _eventsOut.End();
    }

    /// <summary>Windows' view and the AirPods' real state of the A2DP stream on one link.</summary>
    internal sealed class AvdtpState
    {
        public ushort SignalLocal;
        public ushort SignalRemote;
        public ushort MediaRemote;
        /// <summary>ACP SEID of the stream (the AirPods' sink endpoint).</summary>
        public byte Seid;
        /// <summary>The AirPods stream (Windows' START went through, or ours was accepted).</summary>
        public bool Streaming;
        /// <summary>Windows believes it streams (its START answered, by the AirPods or here).</summary>
        public bool WindowsStreaming;
        public int LastWindowsLabel;
        /// <summary>Transaction label of our command waiting for its answer, -1 when none.</summary>
        public int OwnLabel = -1;
        /// <summary>Our commands sent so far, so a timeout only acts for the command it was started for.</summary>
        public int OwnSent;
    }
}

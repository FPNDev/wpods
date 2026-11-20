using System.Text;

namespace WPods.Service;

/// <summary>What one AT command from the AirPods changed for the Hands-Free gateway.</summary>
internal enum AgEvent
{
    None,
    /// <summary>Service level connection set up: codecs may be selected now.</summary>
    Connected,
    /// <summary>The AirPods confirmed the codec of the last <see cref="HandsFreeAg.SelectCodec"/>.</summary>
    CodecConfirmed,
    /// <summary>The AirPods confirmed another codec than asked.</summary>
    CodecRefused,
}

/// <summary>
/// The Hands-Free audio gateway's side of the AT command exchange (HFP 1.8) as far as a voice link needs it: features,
/// codec list, indicators and codec selection. Calls, Siri and the like are refused or acknowledged without effect.
/// </summary>
internal sealed class HandsFreeAg
{
    // Codec negotiation (bit 9) and eSCO S4 settings (bit 11); no calls, no voice recognition.
    private const int Features = 0x0A00;
    private const string Indicators = "(\"service\",(0,1)),(\"call\",(0,1)),(\"callsetup\",(0-3)),(\"callheld\",(0-2)),(\"signal\",(0-5)),(\"roam\",(0,1)),(\"battchg\",(0-5))";

    private readonly StringBuilder _line = new();
    private int _asked;

    /// <summary>Codec IDs the AirPods offered with AT+BAC, in their order.</summary>
    public List<int> Codecs { get; } = [];

    public bool Connected { get; private set; }

    /// <summary>The codec both sides confirmed last; 0 before.</summary>
    public int Codec { get; private set; }

    /// <summary>Feeds UIH data; returns the replies and the events of every complete command in it.</summary>
    public List<(byte[] Reply, AgEvent Event, string Command)> Feed(ReadOnlySpan<byte> data)
    {
        var results = new List<(byte[], AgEvent, string)>();
        foreach (byte b in data)
        {
            if (b is (byte)'\r' or (byte)'\n')
            {
                if (_line.Length > 0)
                {
                    string command = _line.ToString().Trim();
                    _line.Clear();
                    (string reply, AgEvent e) = Handle(command);
                    results.Add((Encoding.ASCII.GetBytes(reply), e, command));
                }
                continue;
            }
            if (_line.Length < 256)
            {
                _line.Append((char)b);
            }
        }
        return results;
    }

    /// <summary>The unsolicited +BCS that asks the AirPods to switch to <paramref name="codec"/>.</summary>
    public byte[] SelectCodec(int codec)
    {
        _asked = codec;
        return Encoding.ASCII.GetBytes($"\r\n+BCS: {codec}\r\n");
    }

    private (string Reply, AgEvent Event) Handle(string command)
    {
        string upper = command.ToUpperInvariant();
        if (upper.StartsWith("AT+BRSF=", StringComparison.Ordinal))
        {
            return ($"\r\n+BRSF: {Features}\r\n" + Ok, AgEvent.None);
        }
        if (upper.StartsWith("AT+BAC=", StringComparison.Ordinal))
        {
            Codecs.Clear();
            foreach (string id in command[7..].Split(','))
            {
                if (int.TryParse(id, out int codec))
                {
                    Codecs.Add(codec);
                }
            }
            return (Ok, AgEvent.None);
        }
        if (upper == "AT+CIND=?")
        {
            return ($"\r\n+CIND: {Indicators}\r\n" + Ok, AgEvent.None);
        }
        if (upper == "AT+CIND?")
        {
            return ("\r\n+CIND: 1,0,0,0,5,0,5\r\n" + Ok, AgEvent.None);
        }
        if (upper.StartsWith("AT+CMER=", StringComparison.Ordinal))
        {
            // Without three-way calling the service level connection is up once indicator reporting is set.
            Connected = true;
            return (Ok, AgEvent.Connected);
        }
        if (upper == "AT+CHLD=?")
        {
            return ("\r\n+CHLD: (0,1,2)\r\n" + Ok, AgEvent.None);
        }
        if (upper.StartsWith("AT+BCS=", StringComparison.Ordinal))
        {
            if (int.TryParse(command[7..], out int codec) && codec == _asked && _asked != 0)
            {
                Codec = codec;
                return (Ok, AgEvent.CodecConfirmed);
            }
            return (Error, AgEvent.CodecRefused);
        }
        if (upper.StartsWith("AT+BVRA", StringComparison.Ordinal) || upper.StartsWith("ATD", StringComparison.Ordinal)
            || upper == "ATA" || upper.StartsWith("AT+CHUP", StringComparison.Ordinal))
        {
            return (Error, AgEvent.None);
        }
        if (upper == "AT+COPS?")
        {
            return ("\r\n+COPS: 0\r\n" + Ok, AgEvent.None);
        }
        // Volume, NREC, BIA, BCC, CLCC, CNUM, XAPL, IPHONEACCEV and the like: acknowledged.
        return (Ok, AgEvent.None);
    }

    private const string Ok = "\r\nOK\r\n";
    private const string Error = "\r\nERROR\r\n";
}

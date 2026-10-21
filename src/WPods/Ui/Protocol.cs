using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using WPods.Core;
using WPods.Nearby;

namespace WPods.Ui;

/// <summary>A page -> host message (ui-protocol.md); one shape for every type, unused fields stay default.</summary>
internal sealed class PageMessage
{
    public string Type { get; set; } = "";
    public string? Id { get; set; }
    public string? Mode { get; set; }
    public string? Name { get; set; }
    public string? Side { get; set; }
    public string? Theme { get; set; }
    public string? Code { get; set; }
    public JsonElement Value { get; set; }
    public string[]? Ids { get; set; }
    public double Height { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PageMessage))]
internal sealed partial class PageJson : JsonSerializerContext;

/// <summary>Writes host -> page messages (one JSON object with "type") with a reused UTF-8 writer. UI thread only.</summary>
internal sealed class MessageWriter
{
    private static readonly DeviceCapabilities[] Capabilities = Enum.GetValues<DeviceCapabilities>();

    private readonly ArrayBufferWriter<byte> _buffer = new(4096);
    private readonly Utf8JsonWriter _json;

    public MessageWriter()
    {
        _json = new Utf8JsonWriter(_buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, SkipValidation = true });
    }

    /// <summary>Starts a message; write its fields to the returned writer, then call <see cref="End"/>.</summary>
    public Utf8JsonWriter Begin(string type)
    {
        _buffer.ResetWrittenCount();
        _json.Reset(_buffer);
        _json.WriteStartObject();
        _json.WriteString("type", type);
        return _json;
    }

    public string End()
    {
        _json.WriteEndObject();
        _json.Flush();
        return Encoding.UTF8.GetString(_buffer.WrittenSpan);
    }

    /// <summary>Lower-camel name of an enum member, e.g. NoiseCancellation -> "noiseCancellation".</summary>
    public static string Camel<T>(T value) where T : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    /// <summary>Parses a lower-camel enum name; false for unknown or numeric text.</summary>
    public static bool TryParse<T>(string? text, out T value) where T : struct, Enum =>
        Enum.TryParse(text, ignoreCase: true, out value) && Enum.IsDefined(value) && !char.IsAsciiDigit(text![0]);

    public static void WriteBattery(Utf8JsonWriter w, string name, Battery battery)
    {
        if (battery.Percent is not { } pct)
        {
            w.WriteNull(name);
            return;
        }
        w.WriteStartObject(name);
        w.WriteNumber("pct", pct);
        w.WriteBoolean("charging", battery.Status == BatteryStatus.Charging);
        w.WriteEndObject();
    }

    public static void WriteBatteries(Utf8JsonWriter w, string name, Battery left, Battery right, Battery @case, Battery headphone)
    {
        w.WriteStartObject(name);
        WriteBattery(w, "left", left);
        WriteBattery(w, "right", right);
        WriteBattery(w, "case", @case);
        WriteBattery(w, "headphone", headphone);
        w.WriteEndObject();
    }

    public static void WriteBatteries(Utf8JsonWriter w, DeviceState s) =>
        WriteBatteries(w, "battery", s.LeftBattery, s.RightBattery, s.CaseBattery, s.HeadphoneBattery);

    public static void WriteEar(Utf8JsonWriter w, DeviceState s)
    {
        w.WriteStartObject("ear");
        WriteEnum(w, "primary", s.PrimaryEar);
        WriteEnum(w, "secondary", s.SecondaryEar);
        // Core cannot tell which bud is primary yet (protocol: send null, the page hides the per-bud line).
        w.WriteNull("primaryLeft");
        w.WriteEndObject();
    }

    public static void WriteListeningMode(Utf8JsonWriter w, DeviceState s) => WriteEnum(w, "listeningMode", s.ListeningMode);

    public static void WriteHeartRate(Utf8JsonWriter w, DeviceState s)
    {
        w.WriteString("heartRate", Camel(s.HeartRate));
        WriteNumber(w, "heartRateBpm", s.HeartRateBpm);
    }

    /// <summary>Every device-side setting except the listening mode (fields of the "settings" message).</summary>
    public static void WriteSettingFields(Utf8JsonWriter w, DeviceState s)
    {
        WriteBool(w, "earDetection", s.EarDetection);
        WriteBool(w, "conversationAwareness", s.ConversationAwareness);
        WriteBool(w, "personalizedVolume", s.PersonalizedVolume);
        WriteBool(w, "allowOff", s.AllowOff);
        WriteBool(w, "oneBudAnc", s.OneBudAnc);
        WriteEnum(w, "micSide", s.MicSide);
        WriteEnum(w, "pressSpeed", s.PressSpeed);
        WriteEnum(w, "holdDuration", s.HoldDuration);
        WriteNumber(w, "toneVolume", s.ToneVolume);
        WriteBool(w, "hearingAidEnrolled", s.HearingAidEnrolled);
        w.WriteBoolean("hearingAidEnabled", s.HearingAidEnabled);
    }

    /// <summary>The "state" value: the attached session's DeviceState, or null.</summary>
    public static void WriteState(Utf8JsonWriter w, DeviceState? s)
    {
        if (s is null)
        {
            w.WriteNull("state");
            return;
        }
        w.WriteStartObject("state");
        WriteBatteries(w, s);
        WriteEar(w, s);
        WriteListeningMode(w, s);
        WriteSettingFields(w, s);
        WriteHeartRate(w, s);
        WriteCaps(w, s);
        WriteChecking(w, s);
        w.WriteEndObject();
    }

    /// <summary>"caps" of the attached pair: its effective capabilities plus "singleBattery" for AirPods Max.</summary>
    public static void WriteCaps(Utf8JsonWriter w, DeviceState s) =>
        WriteCaps(w, s.Capabilities, Models.FromModelNumber(s.ModelNumber)?.SingleBattery == true);

    /// <summary>"checking": true from attach until the initial report window ends (or the handshake got no answer).</summary>
    public static void WriteChecking(Utf8JsonWriter w, DeviceState s) =>
        w.WriteBoolean("checking", !s.InitialReportDone && s.Status != SessionStatus.NoResponse);

    public static void WriteHost(Utf8JsonWriter w, HostSettings host)
    {
        w.WriteStartObject("host");
        w.WriteBoolean("disableHandsFree", host.DisableHandsFree);
        w.WriteBoolean("lowLatency", host.LowLatency);
        w.WriteString("micMode", Camel(host.MicMode));
        w.WriteEndObject();
    }

    /// <summary>One Device object; <paramref name="batteries"/> only for a pair offered nearby.</summary>
    public static void WriteDevice(Utf8JsonWriter w, KnownDevice d, string conn, PodBatteries? batteries, HostSettings host)
    {
        AirPodsModel model = Models.FromModelNumber(d.ModelNumber) ?? Models.Unknown;
        w.WriteStartObject();
        w.WriteString("id", Mac.Format(d.Address));
        w.WriteString("name", d.Name);
        w.WriteString("model", model.Name);
        w.WriteString("art", model.Art);
        WriteCaps(w, d.Capabilities, model.SingleBattery);
        w.WriteString("conn", conn);
        w.WriteBoolean("hasKeys", d.HasKeys);
        if (batteries is { } b)
        {
            WriteBatteries(w, "batteries", b.Left, b.Right, b.Case, b.Headphone);
        }
        else
        {
            w.WriteNull("batteries");
        }
        WriteHost(w, host);
        w.WriteEndObject();
    }

    private static void WriteCaps(Utf8JsonWriter w, DeviceCapabilities capabilities, bool singleBattery)
    {
        w.WriteStartArray("caps");
        foreach (DeviceCapabilities capability in Capabilities)
        {
            if (capability != DeviceCapabilities.None && (capabilities & capability) != 0)
            {
                w.WriteStringValue(Camel(capability));
            }
        }
        if (singleBattery)
        {
            w.WriteStringValue("singleBattery");
        }
        w.WriteEndArray();
    }

    public static void WriteOffer(Utf8JsonWriter w, NearbyOffer offer)
    {
        w.WriteStartObject();
        w.WriteString("id", Mac.Format(offer.Address));
        w.WriteString("name", offer.Name);
        w.WriteString("art", offer.Model.Art);
        PodBatteries b = offer.Batteries;
        WriteBatteries(w, "batteries", b.Left, b.Right, b.Case, b.Headphone);
        w.WriteString("state", Camel(offer.State));
        w.WriteEndObject();
    }

    public static void WriteAccent(Utf8JsonWriter w, AccentRamp? ramp)
    {
        if (ramp is null)
        {
            w.WriteNull("accent");
            return;
        }
        w.WriteStartObject("accent");
        w.WriteString("accent", ramp.Accent);
        w.WriteString("dark1", ramp.Dark1);
        w.WriteString("dark2", ramp.Dark2);
        w.WriteString("dark3", ramp.Dark3);
        w.WriteString("light1", ramp.Light1);
        w.WriteString("light2", ramp.Light2);
        w.WriteString("light3", ramp.Light3);
        w.WriteEndObject();
    }

    /// <summary>labels, lang, rtl: the locale fields both inits share.</summary>
    public static void WriteLocale(Utf8JsonWriter w, ResolvedLocale locale)
    {
        w.WriteStartObject("labels");
        foreach ((string key, string text) in locale.Labels)
        {
            w.WriteString(key, text);
        }
        w.WriteEndObject();
        w.WriteString("lang", locale.Code);
        w.WriteBoolean("rtl", locale.Rtl);
    }

    private static void WriteEnum<T>(Utf8JsonWriter w, string name, T? value) where T : struct, Enum
    {
        if (value is { } v)
        {
            w.WriteString(name, Camel(v));
        }
        else
        {
            w.WriteNull(name);
        }
    }

    private static void WriteBool(Utf8JsonWriter w, string name, bool? value)
    {
        if (value is { } v)
        {
            w.WriteBoolean(name, v);
        }
        else
        {
            w.WriteNull(name);
        }
    }

    private static void WriteNumber(Utf8JsonWriter w, string name, byte? value)
    {
        if (value is { } v)
        {
            w.WriteNumber(name, v);
        }
        else
        {
            w.WriteNull(name);
        }
    }
}

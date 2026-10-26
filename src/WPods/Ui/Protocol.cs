using System.Buffers;
using System.Collections.Frozen;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using WPods.Audio;
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
    public string[]? Modes { get; set; }
    public double Height { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PageMessage))]
internal sealed partial class PageJson : JsonSerializerContext;

/// <summary>Writes host -> page messages (one JSON object with "type") with a reused UTF-8 writer. UI thread only.</summary>
internal sealed class MessageWriter
{
    private static readonly DeviceCapabilities[] Capabilities = Enum.GetValues<DeviceCapabilities>();

    // Protocol order of Device.stemModes.
    private static readonly StemModes[] StemModeOrder = [StemModes.Off, StemModes.NoiseCancellation, StemModes.Transparency, StemModes.Adaptive];

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
    public static string Camel<T>(T value) where T : struct, Enum =>
        CamelNames<T>.Map.TryGetValue(value, out string? name) ? name : CamelName(value);

    private static string CamelName<T>(T value) where T : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

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

    /// <summary>"battery"; after an automatic re-attach the replaced session's until this one reports.</summary>
    public static void WriteBatteries(Utf8JsonWriter w, DeviceState s)
    {
        DeviceState b = s.BatterySource;
        WriteBatteries(w, "battery", b.LeftBattery, b.RightBattery, b.CaseBattery, b.HeadphoneBattery);
    }

    /// <summary>
    /// "ear": per bud (null until the primary bud is known, always null for AirPods Max); after an automatic re-attach
    /// the replaced session's until this one knows a bud.
    /// </summary>
    public static void WriteEar(Utf8JsonWriter w, DeviceState s)
    {
        DeviceState e = s.EarSource;
        w.WriteStartObject("ear");
        WriteEar(w, "left", e.LeftEar);
        WriteEar(w, "right", e.RightEar);
        w.WriteEndObject();
    }

    // Protocol names differ from the enum for OutOfEar ("out"); Unknown is sent as null.
    private static void WriteEar(Utf8JsonWriter w, string name, EarStatus? ear)
    {
        string? value = ear switch
        {
            EarStatus.InEar => "inEar",
            EarStatus.OutOfEar => "out",
            EarStatus.InCase => "inCase",
            EarStatus.Disconnected => "disconnected",
            _ => null,
        };
        if (value is null)
        {
            w.WriteNull(name);
        }
        else
        {
            w.WriteString(name, value);
        }
    }

    public static void WriteListeningMode(Utf8JsonWriter w, DeviceState s) => WriteEnum(w, "listeningMode", Shown(s).ListeningMode);

    public static void WriteHeartRate(Utf8JsonWriter w, DeviceState s)
    {
        w.WriteString("heartRate", Camel(s.HeartRate));
        WriteNumber(w, "heartRateBpm", s.HeartRateBpm);
    }

    /// <summary>
    /// Every device-side setting except the listening mode (fields of the "settings" message). Ear detection, mic
    /// side and allow off are per-host values (stored, set from attach on); the others are pod-saved, see <see cref="Shown"/>.
    /// </summary>
    public static void WriteSettingFields(Utf8JsonWriter w, DeviceState s) => WriteSettingFields(w, Shown(s), s.EarDetection, s.AllowOff, s.MicSide);

    private static void WriteSettingFields(Utf8JsonWriter w, PodSettings pod, bool? earDetection, bool? allowOff, MicSide? micSide)
    {
        WriteBool(w, "earDetection", earDetection);
        WriteBool(w, "conversationAwareness", pod.ConversationAwareness);
        WriteBool(w, "personalizedVolume", pod.PersonalizedVolume);
        WriteBool(w, "oneBudAnc", pod.OneBudAnc);
        WriteBool(w, "allowOff", allowOff);
        WriteEnum(w, "micSide", micSide);
        WriteEnum(w, "pressSpeed", pod.PressSpeed);
        WriteEnum(w, "holdDuration", pod.HoldDuration);
        WriteNumber(w, "toneVolume", pod.ToneVolume);
        WriteBool(w, "hearingAidEnrolled", pod.HearingAidEnrolled);
        w.WriteBoolean("hearingAidEnabled", pod.HearingAidEnabled);
    }

    // Pod-saved settings to show: while checking, the remembered value for each one not reported yet (display only,
    // the session acts on DeviceState alone); afterwards only what the device reported.
    private static PodSettings Shown(DeviceState s) => s.GetPodSettings(IsChecking(s) ? s.RememberedSettings : PodSettings.None);

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

    /// <summary>
    /// "checking": true from attach until the initial report window ends (or the handshake got no answer).
    /// "ready": true once the handshake answered.
    /// </summary>
    public static void WriteChecking(Utf8JsonWriter w, DeviceState s)
    {
        w.WriteBoolean("checking", IsChecking(s));
        w.WriteBoolean("ready", s.Status == SessionStatus.Ready);
    }

    private static bool IsChecking(DeviceState s) => !s.InitialReportDone && s.Status != SessionStatus.NoResponse;

    public static void WriteHost(Utf8JsonWriter w, HostSettings host)
    {
        w.WriteStartObject("host");
        w.WriteBoolean("disableHandsFree", host.DisableHandsFree);
        w.WriteBoolean("lowLatency", host.LowLatency);
        w.WriteString("micMode", Camel(host.MicMode));
        w.WriteNumber("duckPercent", host.DuckPercent);
        w.WriteEndObject();
    }

    /// <summary>"micCatalog": each mode's models and the voice enhancements, in picker order.</summary>
    public static void WriteMicCatalog(Utf8JsonWriter w)
    {
        w.WriteStartObject("micCatalog");
        WriteMicOptions(w, "standard", MicCatalog.Standard);
        WriteMicOptions(w, "voiceIsolation", MicCatalog.VoiceIsolation);
        WriteMicOptions(w, "wideSpectrum", MicCatalog.WideSpectrum);
        WriteMicOptions(w, "enhancement", MicCatalog.Enhancements);
        w.WriteEndObject();
    }

    private static void WriteMicOptions(Utf8JsonWriter w, string name, MicOption[] options)
    {
        w.WriteStartArray(name);
        foreach (MicOption option in options)
        {
            w.WriteStartObject();
            w.WriteString("id", option.Id);
            w.WriteString("name", option.Name);
            w.WriteString("weight", option.Weight);
            w.WriteNumber("threads", option.Threads);
            w.WriteNumber("mmacs", option.Mmacs);
            w.WriteNumber("latencyMs", option.LatencyMs);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    /// <summary>A MicConfig object: modes (Apple's order), models per mode, enhancement (null = Off).</summary>
    public static void WriteMicConfig(Utf8JsonWriter w, string name, MicConfig config)
    {
        w.WriteStartObject(name);
        w.WriteStartArray("modes");
        foreach (MicMode mode in MicConfig.AllModes)
        {
            if (config.Modes.Contains(mode))
            {
                w.WriteStringValue(Camel(mode));
            }
        }
        w.WriteEndArray();
        w.WriteStartObject("models");
        foreach (MicMode mode in MicConfig.AllModes)
        {
            w.WriteString(Camel(mode), config.Models[mode]);
        }
        w.WriteEndObject();
        w.WriteString("enhancement", config.Enhancement);
        w.WriteEndObject();
    }

    /// <summary>
    /// One Device object; <paramref name="batteries"/> only for a pair offered nearby. "saved" = the settings remembered
    /// for the pair (display while not attached).
    /// </summary>
    public static void WriteDevice(Utf8JsonWriter w, KnownDevice d, string conn, PodBatteries? batteries, HostSettings host,
        PodSettings saved, bool earDetection, bool allowOff, MicSide micSide)
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
        w.WriteStartArray("stemModes");
        foreach (StemModes mode in StemModeOrder)
        {
            if ((d.StemModes & mode) != 0)
            {
                w.WriteStringValue(Camel(mode));
            }
        }
        w.WriteEndArray();
        w.WriteStartObject("saved");
        WriteEnum(w, "listeningMode", saved.ListeningMode);
        WriteSettingFields(w, saved, earDetection, allowOff, micSide);
        w.WriteEndObject();
        w.WriteEndObject();
    }

    /// <summary>Parses the page's stem mode names ("off", "noiseCancellation", "transparency", "adaptive"); false on an unknown name.</summary>
    public static bool TryParseStemModes(string[] names, out StemModes modes)
    {
        modes = StemModes.None;
        foreach (string name in names)
        {
            if (!TryParse(name, out StemModes mode) || mode == StemModes.None)
            {
                return false;
            }
            modes |= mode;
        }
        return true;
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

    // Names of the defined members, built once per enum type. Other values (a raw byte cast to the enum) take the slow path.
    private static class CamelNames<T> where T : struct, Enum
    {
        public static readonly FrozenDictionary<T, string> Map = Enum.GetValues<T>().ToFrozenDictionary(v => v, CamelName);
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

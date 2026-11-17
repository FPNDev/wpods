using System.Runtime.InteropServices;
using WPods.Core;

namespace WPods.Ui;

/// <summary>
/// Short confirmation sounds for hotkeys, synthesized on first use and played on Windows' default output (the
/// AirPods while WPods follows them) through WASAPI. A new sound cuts the one still playing.
/// </summary>
internal static class Sounds
{
    public enum Cue
    {
        On,
        Off,
        Standard,
        VoiceIsolation,
        WideSpectrum,
        Error,
    }

    private const float Peak = 0.25f;
    // An idle Bluetooth output needs a moment to wake, which would swallow the start of the sound.
    private const int WakeLeadMs = 300;
    private const int ERender = 0;
    private const int EMultimedia = 1;
    private const int ClsCtxAll = 0x17;
    private const int SharedMode = 0;
    private const short FormatFloat = 3;
    private const short FormatExtensible = unchecked((short)0xFFFE);

    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    // (frequency ratio, level) per partial.
    private static readonly (double Ratio, double Level)[] Warm = [(1, 1), (2, 0.18), (3, 0.04)];
    private static readonly (double Ratio, double Level)[] Soft = [(1, 1), (2, 0.1)];
    private static readonly (double Ratio, double Level)[] Pure = [(1, 1), (2, 0.04)];
    private static readonly (double Ratio, double Level)[] Thud = [(1, 1), (2, 0.25), (3, 0.06)];

    // Rendered at the rate of the output last used; another rate renders again.
    private static readonly Rendering?[] Rendered = new Rendering?[Enum.GetValues<Cue>().Length];
    private static readonly Lock Gate = new();
    // Under Gate: the stream playing now and the number of the last Play call.
    private static IAudioClient? _playing;
    private static int _generation;

    /// <summary>Returns at once; the sound is prepared and started on a thread-pool thread.</summary>
    public static void Play(Cue cue)
    {
        int generation;
        lock (Gate)
        {
            generation = ++_generation;
        }
        ThreadPool.QueueUserWorkItem(_ => PlayNow(cue, generation));
    }

    private static void PlayNow(Cue cue, int generation)
    {
        IAudioClient? client = null;
        try
        {
            var enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumerator();
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(ERender, EMultimedia, out IMMDevice device));
            bool silent = IsSilent(device);
            Guid iid = typeof(IAudioClient).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref iid, ClsCtxAll, 0, out object o));
            client = (IAudioClient)o;
            // Written in the output's own shared-mode format (32-bit float at its rate), so Windows converts nothing.
            Marshal.ThrowExceptionForHR(client.GetMixFormat(out nint mix));
            int rate;
            int channels;
            int frames;
            int lead;
            try
            {
                channels = Marshal.ReadInt16(mix, 2);
                rate = Marshal.ReadInt32(mix, 4);
                short tag = Marshal.ReadInt16(mix);
                bool isFloat = tag == FormatFloat
                    || (tag == FormatExtensible && Marshal.PtrToStructure<Guid>(mix + 24) == FloatSubFormat);
                if (!isFloat || Marshal.ReadInt16(mix, 14) != 32 || channels < 1)
                {
                    Log.Warn("hotkeys", $"sound {cue} not played: output format tag 0x{tag:X4} is not 32-bit float");
                    Close(client);
                    return;
                }
                lead = silent ? WakeLeadMs * rate / 1000 : 0;
                frames = lead + Samples(cue, rate).Length / 2;
                long duration = (frames + rate / 10) * 10_000_000L / rate;
                Marshal.ThrowExceptionForHR(client.Initialize(SharedMode, 0, duration, 0, mix, 0));
            }
            finally
            {
                Marshal.FreeCoTaskMem(mix);
            }
            Guid renderIid = typeof(IAudioRenderClient).GUID;
            Marshal.ThrowExceptionForHR(client.GetService(ref renderIid, out object r));
            var render = (IAudioRenderClient)r;
            Marshal.ThrowExceptionForHR(render.GetBuffer(frames, out nint buffer));
            unsafe
            {
                var target = new Span<float>((void*)buffer, frames * channels);
                target.Clear();
                float[] stereo = Samples(cue, rate);
                for (int i = 0; i < stereo.Length / 2; i++)
                {
                    int at = (lead + i) * channels;
                    if (channels == 1)
                    {
                        target[at] = (stereo[2 * i] + stereo[2 * i + 1]) / 2;
                    }
                    else
                    {
                        target[at] = stereo[2 * i];
                        target[at + 1] = stereo[2 * i + 1];
                    }
                }
            }
            Marshal.ThrowExceptionForHR(render.ReleaseBuffer(frames, 0));
            lock (Gate)
            {
                // A newer sound already started: this one is dropped.
                if (generation != _generation)
                {
                    Close(client);
                    return;
                }
                if (_playing is { } previous)
                {
                    Close(previous);
                }
                Marshal.ThrowExceptionForHR(client.Start());
                _playing = client;
            }
            Thread.Sleep(frames * 1000 / rate + 100);
            lock (Gate)
            {
                if (_playing == client)
                {
                    Close(client);
                    _playing = null;
                }
            }
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            Log.Warn("hotkeys", $"sound {cue} not played: {e.Message}");
            if (client is not null)
            {
                lock (Gate)
                {
                    if (_playing != client)
                    {
                        Close(client);
                    }
                }
            }
        }
    }

    private static void Close(IAudioClient client)
    {
        client.Stop();
        Marshal.FinalReleaseComObject(client);
    }

    // Nothing is playing on the output, so it may be asleep.
    private static bool IsSilent(IMMDevice device)
    {
        Guid iid = typeof(IAudioMeterInformation).GUID;
        if (device.Activate(ref iid, ClsCtxAll, 0, out object o) < 0)
        {
            return true;
        }
        var meter = (IAudioMeterInformation)o;
        bool silent = meter.GetPeakValue(out float peak) < 0 || peak <= 0;
        Marshal.FinalReleaseComObject(meter);
        return silent;
    }

    private sealed record Voice(double Start, double Freq, double Length, double Gain, double Decay,
        (double Ratio, double Level)[] Partials, double Pan = 0, double GlideFrom = 0, double GlideTime = 0);

    // Soft, low two-note chimes in the manner of the AirPods' own noise control tones. On rises a fifth (E4 to B4),
    // Off falls back down it. Standard is one warm G4; Voice Isolation glides down into a pure A4 (focusing in); Wide
    // Spectrum spreads C4, G4 and E5 across left, centre and right (opening up). Error is two low, muted taps.
    private static Voice[] Score(Cue cue) => cue switch
    {
        Cue.On => [new(0, 329.63, 0.34, 0.8, 0.2, Warm), new(0.09, 493.88, 0.46, 0.85, 0.26, Warm)],
        Cue.Off => [new(0, 493.88, 0.3, 0.7, 0.18, Soft), new(0.09, 329.63, 0.44, 0.8, 0.24, Soft)],
        Cue.Standard => [new(0, 392, 0.45, 0.9, 0.24, Warm)],
        Cue.VoiceIsolation => [new(0, 440, 0.48, 0.9, 0.26, Pure, GlideFrom: 587.33, GlideTime: 0.09)],
        Cue.WideSpectrum =>
        [
            new(0, 261.63, 0.56, 0.55, 0.3, Warm),
            new(0.05, 392, 0.52, 0.45, 0.28, Warm, Pan: -0.6),
            new(0.1, 659.25, 0.48, 0.35, 0.24, Warm, Pan: 0.6),
        ],
        _ => [new(0, 196, 0.16, 0.9, 0.07, Thud, GlideFrom: 207.65, GlideTime: 0.04), new(0.14, 174.61, 0.22, 0.8, 0.09, Thud, GlideFrom: 185, GlideTime: 0.04)],
    };

    private static float[] Samples(Cue cue, int rate)
    {
        if (Rendered[(int)cue] is { } done && done.Rate == rate)
        {
            return done.Samples;
        }
        float[] samples = Render(cue, rate);
        Rendered[(int)cue] = new Rendering(rate, samples);
        return samples;
    }

    // Interleaved float stereo at `rate`, peak-normalized to Peak.
    private static float[] Render(Cue cue, int rate)
    {
        Voice[] voices = Score(cue);
        double end = 0;
        foreach (Voice v in voices)
        {
            end = Math.Max(end, v.Start + v.Length);
        }
        int frames = (int)((end + 0.02) * rate);
        float[] left = new float[frames];
        float[] right = new float[frames];
        foreach (Voice v in voices)
        {
            Mix(left, right, v, rate);
        }
        float peak = 0;
        for (int i = 0; i < frames; i++)
        {
            peak = Math.Max(peak, Math.Max(Math.Abs(left[i]), Math.Abs(right[i])));
        }
        float scale = peak > 0 ? Peak / peak : 0;
        float[] samples = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            samples[2 * i] = left[i] * scale;
            samples[2 * i + 1] = right[i] * scale;
        }
        return samples;
    }

    // Sine partials with an 8 ms raised-cosine attack and an exponential decay (higher partials fade sooner), a 15 ms
    // fade at the end so nothing clicks, an optional pitch glide into the note and an equal-power pan.
    private static void Mix(float[] left, float[] right, Voice v, int rate)
    {
        const double Attack = 0.008;
        const double Fade = 0.015;
        int first = (int)(v.Start * rate);
        int count = Math.Min((int)(v.Length * rate), left.Length - first);
        double angle = (v.Pan + 1) * Math.PI / 4;
        double gainLeft = Math.Cos(angle) * v.Gain;
        double gainRight = Math.Sin(angle) * v.Gain;
        Span<double> phases = stackalloc double[v.Partials.Length];
        for (int n = 0; n < count; n++)
        {
            double t = (double)n / rate;
            double freq = v.GlideTime > 0 && t < v.GlideTime
                ? v.GlideFrom + (v.Freq - v.GlideFrom) * Math.Sin(t / v.GlideTime * Math.PI / 2)
                : v.Freq;
            double envelope = t < Attack ? 0.5 - 0.5 * Math.Cos(Math.PI * t / Attack) : 1;
            double remaining = v.Length - t;
            if (remaining < Fade)
            {
                envelope *= remaining / Fade;
            }
            double sample = 0;
            for (int p = 0; p < v.Partials.Length; p++)
            {
                (double ratio, double level) = v.Partials[p];
                phases[p] += 2 * Math.PI * freq * ratio / rate;
                sample += Math.Sin(phases[p]) * level * Math.Exp(-t * (1 + 0.6 * (ratio - 1)) / v.Decay);
            }
            sample *= envelope;
            left[first + n] += (float)(sample * gainLeft);
            right[first + n] += (float)(sample * gainRight);
        }
    }

    private sealed record Rendering(int Rate, float[] Samples);

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private sealed class MMDeviceEnumerator;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, int mask, out nint devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int context, nint parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int flags, long bufferDuration, long periodicity, nint format, nint session);
        [PreserveSig] int GetBufferSize(out int frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out int frames);
        [PreserveSig] int IsFormatSupported(int shareMode, nint format, out nint closest);
        [PreserveSig] int GetMixFormat(out nint format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(nint handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(int frames, out nint data);
        [PreserveSig] int ReleaseBuffer(int frames, int flags);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }
}

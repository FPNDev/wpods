namespace WPods.Audio.Enhance;

/// <summary>
/// Voice enhancements built on WebRTC AGC2 (<see cref="WebRtcApm.Agc2"/>, in webrtc_apm.dll). Like
/// <see cref="VoiceEnhancements"/> they run after the mode's model on the x4 signal and are loudness matched to
/// enhancement Off at the measured AirPods speech level (about -24 dBFS RMS after x4) through their target (headroom).
/// AGC2's adaptive gain never goes below 0 dB, so two families: on the x4 signal (raises only speech quieter than Off,
/// starts at 0 dB) and full range (the x4 undone first, as AGC2 would see the raw microphone: raises and lowers,
/// starts at the WebRTC default 15 dB). No latency.
/// </summary>
internal static class WebRtcEnhancements
{
    // On the x4 signal: a booster for quiet speech, Off-level speech passes at 0 dB.
    public static IMicStage Agc2CleanEq() => new MicChain(
        VoiceEnhancements.CleanEq(), WebRtcApm.Agc2(headroomDb: 8f, initialGainDb: 0f));

    // Full range: input back at the x1 level, AGC2 sets the final level.
    public static IMicStage Agc2FullCleanEq() => new MicChain(
        new Gain(-12.04f), VoiceEnhancements.CleanEq(), WebRtcApm.Agc2(headroomDb: 6.8f));
}

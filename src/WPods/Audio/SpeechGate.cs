namespace WPods.Audio;

/// <summary>
/// Mutes a denoised mic signal between words (Voice Isolation test modes 2 and 4). Driven per 480-sample frame by
/// RNNoise's voice probability: opens at <see cref="OpenThreshold"/>, stays open for <see cref="HangoverFrames"/>
/// frames after the last voiced frame, and moves the gain per sample (5 ms attack, 50 ms release) so it never clicks.
/// Values (tuning to verify by ear):
/// - 0.90 threshold: middle of the 85-95 % range the RNNoise VAD plugin documentation calls fine for most cases.
/// - 250 ms hangover: at least the 200 ms minimum grace period of that plugin; the extra 50 ms bridges short
///   unvoiced consonants and gaps between syllables so words are not chopped.
/// - 5 ms attack: keeps word onsets (the gate decision comes from the current input frame while the denoised
///   output lags by at least one frame, so the gate opens 10 ms or more before the word), yet the 240-sample ramp
///   has no step, so no click.
/// - 50 ms release: word tails decay naturally and a slow fade-out is masked, while a fast one sounds like a cut.
/// - Closed gain 0: silence between words is the point of the mode (keyboard clicks, coughs).
/// One caller at a time.
/// </summary>
internal sealed unsafe class SpeechGate
{
    private const float OpenThreshold = 0.90f;
    private const int HangoverFrames = 25; // 250 ms of 10 ms frames
    private const float AttackStep = 1f / 240; // 5 ms at 48 kHz
    private const float ReleaseStep = 1f / 2400; // 50 ms at 48 kHz

    private int _hold;
    private float _gain;

    /// <summary>Applies the gate to one frame in place.</summary>
    public void Apply(float* samples, float voiceProbability)
    {
        if (voiceProbability >= OpenThreshold)
        {
            _hold = HangoverFrames;
        }
        else if (_hold > 0)
        {
            _hold--;
        }
        float gain = _gain;
        if (_hold > 0)
        {
            if (gain >= 1f)
            {
                return;
            }
            for (int i = 0; i < Denoiser.FrameSize; i++)
            {
                gain = MathF.Min(gain + AttackStep, 1f);
                samples[i] *= gain;
            }
        }
        else if (gain <= 0f)
        {
            new Span<float>(samples, Denoiser.FrameSize).Clear();
        }
        else
        {
            for (int i = 0; i < Denoiser.FrameSize; i++)
            {
                gain = MathF.Max(gain - ReleaseStep, 0f);
                samples[i] *= gain;
            }
        }
        _gain = gain;
    }
}

// Minimal C API over the WebRTC AudioProcessing module (webrtc-audio-processing) for WPods: capture-side noise
// suppression, high-pass filter and AGC2 (adaptive digital and/or fixed digital gain + limiter; no input volume
// controller), no AEC, 48 kHz mono float frames of 480 samples (10 ms), in place.
#include <webrtc/modules/audio_processing/include/audio_processing.h>

namespace {

struct Apm {
  rtc::scoped_refptr<webrtc::AudioProcessing> apm;
};

}  // namespace

// noise_level: 0 = low, 1 = moderate, 2 = high, 3 = very high, -1 = noise suppression off.
// high_pass: non-zero enables the high-pass filter. Returns null on bad arguments.
extern "C" __declspec(dllexport) void* wapm_create(int noise_level, int high_pass) {
  if (noise_level < -1 || noise_level > 3) {
    return nullptr;
  }
  rtc::scoped_refptr<webrtc::AudioProcessing> apm = webrtc::AudioProcessingBuilder().Create();
  if (!apm) {
    return nullptr;
  }
  webrtc::AudioProcessing::Config config;
  config.high_pass_filter.enabled = high_pass != 0;
  config.noise_suppression.enabled = noise_level >= 0;
  if (noise_level >= 0) {
    config.noise_suppression.level = static_cast<webrtc::AudioProcessing::Config::NoiseSuppression::Level>(noise_level);
  }
  apm->ApplyConfig(config);
  return new Apm{apm};
}

// AGC2 only (noise suppression off): adaptive != 0 enables the adaptive digital controller with the given
// parameters (AudioProcessing::Config::GainController2::AdaptiveDigital), fixed_gain_db is the fixed digital gain
// before the limiter (0..90 dB), high_pass != 0 enables the high-pass filter. Returns null when the config is invalid.
extern "C" __declspec(dllexport) void* wapm_create_agc2(int high_pass, int adaptive, float headroom_db, float max_gain_db,
                                                        float initial_gain_db, float max_gain_change_db_per_second,
                                                        float max_output_noise_level_dbfs, float fixed_gain_db) {
  webrtc::AudioProcessing::Config config;
  config.high_pass_filter.enabled = high_pass != 0;
  config.gain_controller2.enabled = true;
  config.gain_controller2.adaptive_digital.enabled = adaptive != 0;
  config.gain_controller2.adaptive_digital.headroom_db = headroom_db;
  config.gain_controller2.adaptive_digital.max_gain_db = max_gain_db;
  config.gain_controller2.adaptive_digital.initial_gain_db = initial_gain_db;
  config.gain_controller2.adaptive_digital.max_gain_change_db_per_second = max_gain_change_db_per_second;
  config.gain_controller2.adaptive_digital.max_output_noise_level_dbfs = max_output_noise_level_dbfs;
  config.gain_controller2.fixed_digital.gain_db = fixed_gain_db;
  rtc::scoped_refptr<webrtc::AudioProcessing> apm = webrtc::AudioProcessingBuilder().Create();
  if (!apm) {
    return nullptr;
  }
  // ApplyConfig replaces an invalid AGC2 config with the default one.
  apm->ApplyConfig(config);
  if (apm->GetConfig().gain_controller2 != config.gain_controller2) {
    return nullptr;
  }
  return new Apm{apm};
}

// frame: 480 samples, 48 kHz mono, -1..1, processed in place. Returns 0 on success (webrtc::AudioProcessing::kNoError).
extern "C" __declspec(dllexport) int wapm_process(void* state, float* frame) {
  const webrtc::StreamConfig config(48000, 1);
  float* channels[1] = {frame};
  return static_cast<Apm*>(state)->apm->ProcessStream(channels, config, config, channels);
}

extern "C" __declspec(dllexport) void wapm_destroy(void* state) {
  delete static_cast<Apm*>(state);
}

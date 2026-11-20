// LC3 encoder and decoder (liblc3) for the AirPods' HD voice: mono float PCM, resampled by liblc3 between the codec
// rate and the PCM rate.
#include <cstdlib>

#include <lc3.h>

#include "wpods_native.h"

namespace {

// liblc3's state follows this header in the same allocation (alignas keeps it aligned for liblc3).
struct alignas(16) Codec {
    void* handle;
    int samples;
};

bool valid_frame(int frame_us)
{
    return frame_us == 7500 || frame_us == 10000;
}

Codec* allocate(int frame_us, int pcm_rate, unsigned size)
{
    if (size == 0) {
        return nullptr;
    }
    auto* const codec = static_cast<Codec*>(std::malloc(sizeof(Codec) + size));
    if (codec) {
        codec->handle = nullptr;
        codec->samples = lc3_frame_samples(frame_us, pcm_rate);
    }
    return codec;
}

} // namespace

void* __cdecl wpn_lc3_decoder_create(int frame_us, int sample_rate, int pcm_rate)
{
    if (!valid_frame(frame_us)) {
        return nullptr;
    }
    Codec* const codec = allocate(frame_us, pcm_rate, lc3_decoder_size(frame_us, pcm_rate));
    if (!codec) {
        return nullptr;
    }
    codec->handle = lc3_setup_decoder(frame_us, sample_rate, pcm_rate, codec + 1);
    if (!codec->handle) {
        std::free(codec);
        return nullptr;
    }
    return codec;
}

int __cdecl wpn_lc3_decode(void* decoder, const unsigned char* frame, int nbytes, float* pcm)
{
    auto* const codec = static_cast<Codec*>(decoder);
    if (!codec || !pcm) {
        return -1;
    }
    // A frame that fails to decode is concealed by liblc3 (result 1), which still fills pcm.
    if (lc3_decode(static_cast<lc3_decoder_t>(codec->handle), frame, frame ? nbytes : 0, LC3_PCM_FORMAT_FLOAT, pcm, 1) < 0) {
        return -1;
    }
    return codec->samples;
}

void __cdecl wpn_lc3_decoder_free(void* decoder)
{
    std::free(decoder);
}

void* __cdecl wpn_lc3_encoder_create(int frame_us, int sample_rate, int pcm_rate)
{
    if (!valid_frame(frame_us)) {
        return nullptr;
    }
    Codec* const codec = allocate(frame_us, pcm_rate, lc3_encoder_size(frame_us, pcm_rate));
    if (!codec) {
        return nullptr;
    }
    codec->handle = lc3_setup_encoder(frame_us, sample_rate, pcm_rate, codec + 1);
    if (!codec->handle) {
        std::free(codec);
        return nullptr;
    }
    return codec;
}

int __cdecl wpn_lc3_encode(void* encoder, const float* pcm, int nbytes, unsigned char* frame)
{
    auto* const codec = static_cast<Codec*>(encoder);
    if (!codec || !pcm || !frame || nbytes < LC3_MIN_FRAME_BYTES || nbytes > LC3_MAX_FRAME_BYTES) {
        return -1;
    }
    return lc3_encode(static_cast<lc3_encoder_t>(codec->handle), LC3_PCM_FORMAT_FLOAT, pcm, 1, nbytes, frame) < 0 ? -1 : 0;
}

void __cdecl wpn_lc3_encoder_free(void* encoder)
{
    std::free(encoder);
}

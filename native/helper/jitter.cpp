// Jitter buffer for the WPods mic, shared by the service's virtual USB mic and the mixer's direct source. The AirPods
// send 30 ms of audio per packet, unevenly; every block pushed carries its position on the AirPods' clock. Playout runs
// a measured delay behind that clock (covering the latest arrival of the last 10 s), holds it by playing up to 0.5 %
// faster or slower, and conceals only audio that has not arrived when it is due.
#include <windows.h>

#include <algorithm>
#include <cmath>
#include <cstdarg>
#include <cwchar>
#include <deque>
#include <limits>
#include <new>
#include <utility>

#include "wpods_native.h"

namespace {

constexpr int Rate = 48000;
constexpr int RingSize = 1 << 15; // ~680 ms
constexpr int64_t RingMask = RingSize - 1;
// Arrivals the delay is sized from: one late burst keeps the delay up this long, then it eases back.
constexpr double WindowSeconds = 10.0;
constexpr double MarginSeconds = 0.002;
constexpr double MaxDelaySeconds = 0.3;
// The first seconds of a stream use at least the last stream's spread, at most StartSpreadSeconds (also the first
// stream's), until the window has seen enough arrivals.
constexpr double WarmUpSeconds = 2.0;
constexpr double StartSpreadSeconds = 0.04;
// The playhead this far from where it should be jumps there instead of being steered.
constexpr double ResyncSeconds = 0.15;
// Steering corrects an error over about this long, never changing the rate by more than MaxRateChange.
constexpr double SteerSeconds = 2.0;
constexpr double MaxRateChange = 0.005;
// An arrival this far off the clock seen so far starts a new stream (the AirPods restarted their clock).
constexpr double ClockJumpSeconds = 1.0;
constexpr int ShortGap = Rate * 40 / 1000; // a longer gap is the AirPods' silence, not a dropout
constexpr int Fade = 96; // 2 ms
constexpr int HistoryLength = 1440; // 30 ms
constexpr int PitchWindow = 480; // 10 ms
constexpr int MinPeriod = Rate / 400; // 400 Hz
constexpr int MaxPeriod = Rate / 66; // 66 Hz
constexpr double VoicedCorrelation = 0.5;
constexpr int ConcealHold = Rate * 30 / 1000;
constexpr int ConcealDecay = Rate * 30 / 1000;
constexpr float SilentRms = 33.0f; // -60 dBFS
constexpr int Past = PitchWindow + MaxPeriod + Fade;
constexpr int MinuteSamples = Rate * 60;

struct Lines {
    wchar_t text[4][160];
    int count = 0;
};

double now_seconds()
{
    static double const frequency = [] {
        LARGE_INTEGER f;
        QueryPerformanceFrequency(&f);
        return static_cast<double>(f.QuadPart);
    }();
    LARGE_INTEGER t;
    QueryPerformanceCounter(&t);
    return static_cast<double>(t.QuadPart) / frequency;
}

float conceal_gain(int concealed)
{
    return concealed < ConcealHold ? 1.0f : std::max(0.0f, 1.0f - ((concealed - ConcealHold) / static_cast<float>(ConcealDecay)));
}

// Smallest or largest value of the last WindowSeconds, in O(1) amortized per value.
template <bool Largest>
class WindowExtreme {
public:
    void clear() { m_values.clear(); }
    double value() const { return m_values.front().second; }

    void add(double time, double v)
    {
        while (!m_values.empty() && (Largest ? m_values.back().second <= v : m_values.back().second >= v)) {
            m_values.pop_back();
        }
        m_values.emplace_back(time, v);
        while (m_values.front().first < time - WindowSeconds) {
            m_values.pop_front();
        }
    }

private:
    std::deque<std::pair<double, double>> m_values;
};

class Jitter {
public:
    Jitter(wpn_jitter_log_cb log, void* ctx) : m_log(log), m_ctx(ctx) { clear(); }

    void reset()
    {
        AcquireSRWLockExclusive(&m_lock);
        m_startSpread = std::min(m_spread, StartSpreadSeconds);
        clear();
        ReleaseSRWLockExclusive(&m_lock);
    }

    void push(int16_t const* pcm, uint32_t count, int64_t pos)
    {
        AcquireSRWLockExclusive(&m_lock);
        double const now = now_seconds();
        // When the block's first sample was sent, on this PC's clock, plus a constant: only differences matter.
        double const offset = now - (static_cast<double>(pos) / Rate);
        if (m_anchored && std::abs(offset - m_earliest.value()) > ClockJumpSeconds) {
            m_earliest.clear();
            m_latest.clear();
            m_anchored = false;
        }
        if (!m_anchored) {
            // Until the next pull's resync the playhead sits at this block, so the block is kept.
            m_anchored = true;
            m_anchoredAt = now;
            m_resync = true;
            m_play = static_cast<double>(pos);
        }
        m_earliest.add(now, offset);
        m_latest.add(now, offset);
        for (uint32_t k = 0; k < count; ++k) {
            int64_t const p = pos + k;
            if (p < m_play) {
                ++m_late;
                ++m_minuteLate;
                continue;
            }
            m_ring[p & RingMask] = pcm[k];
            m_slot[p & RingMask] = p;
        }
        m_received += count;
        ReleaseSRWLockExclusive(&m_lock);
    }

    void pull(float* out, uint32_t count)
    {
        Lines lines;
        AcquireSRWLockExclusive(&m_lock);
        m_lines = &lines;
        double step = 1.0;
        if (m_anchored) {
            double const now = now_seconds();
            // The spread, the samples one pull takes (all of them must have arrived) and a margin.
            m_spread = spread(now);
            m_delay = std::min(m_spread + (count / static_cast<double>(Rate)) + MarginSeconds, MaxDelaySeconds);
            // Where the playhead should be: each sample plays its delay after the earliest arrivals' clock.
            double const wanted = (now - m_earliest.value() - m_delay) * Rate;
            double const error = wanted - m_play;
            // A new clock always jumps: pulls before its first audio moved the playhead by an arbitrary amount.
            if (m_resync || std::abs(error) > ResyncSeconds * Rate) {
                m_play = wanted;
                m_resync = false;
            } else {
                step = 1.0 + std::clamp(error / (SteerSeconds * Rate), -MaxRateChange, MaxRateChange);
            }
        }
        for (uint32_t i = 0; i < count; ++i) {
            out[i] = m_anchored ? next() : remember(conceal());
            m_play += step;
        }
        m_sent += count;
        m_minuteSent += count;
        if (m_minuteSent >= MinuteSamples) {
            if (m_minuteDropouts > 0 || m_minuteLate > 0) {
                log(L"last minute: %d dropouts concealed (longest %d ms), %.0f ms arrived too late, delay %.0f ms",
                    m_minuteDropouts, m_minuteLongest * 1000 / Rate, m_minuteLate * 1000.0 / Rate, m_delay * 1000);
            }
            m_minuteSent = 0;
            m_minuteDropouts = 0;
            m_minuteLongest = 0;
            m_minuteLate = 0;
        }
        m_lines = nullptr;
        ReleaseSRWLockExclusive(&m_lock);
        if (m_log) {
            for (int i = 0; i < lines.count; ++i) {
                m_log(m_ctx, lines.text[i]);
            }
        }
    }

    void stats(wpn_jitter_stats* stats)
    {
        AcquireSRWLockExclusive(&m_lock);
        stats->received = m_received;
        stats->sent = m_sent;
        stats->concealed = m_missing;
        stats->dropouts = m_dropouts;
        stats->trimmed = m_late;
        stats->delayMs = static_cast<int32_t>(std::lround(m_delay * 1000));
        ReleaseSRWLockExclusive(&m_lock);
    }

private:
    void clear()
    {
        std::fill(std::begin(m_slot), std::end(m_slot), -1);
        m_earliest.clear();
        m_latest.clear();
        m_anchored = false;
        m_resync = false;
        m_play = 0;
        m_delay = 0;
        m_received = m_sent = m_missing = m_dropouts = m_late = 0;
        m_concealing = true;
        m_gap = ShortGap;
        m_blend = Fade;
        m_periodLength = 0;
        std::fill(std::begin(m_history), std::end(m_history), 0.0f);
        m_minuteSent = m_minuteDropouts = m_minuteLongest = 0;
        m_minuteLate = 0;
    }

    // Between the earliest and the latest arrivals of the window (the link's unevenness); never under the starting
    // spread while warming up.
    double spread(double now) const
    {
        double const spread = m_latest.value() - m_earliest.value();
        return now - m_anchoredAt < WarmUpSeconds ? std::max(spread, m_startSpread) : spread;
    }

    void log(wchar_t const* format, ...)
    {
        if (!m_lines || m_lines->count == static_cast<int>(std::size(m_lines->text))) {
            return;
        }
        va_list args;
        va_start(args, format);
        vswprintf_s(m_lines->text[m_lines->count++], std::size(m_lines->text[0]), format, args);
        va_end(args);
    }

    bool has(int64_t p) const { return p >= 0 && m_slot[p & RingMask] == p; }

    // The sample at the playhead (cubic over its four neighbours, the edge sample standing in for one not received),
    // crossfaded out of the concealment after a gap; concealment while it has not arrived.
    float next()
    {
        int64_t const i = static_cast<int64_t>(std::floor(m_play));
        if (has(i) && has(i + 1)) {
            float const t = static_cast<float>(m_play - i);
            float const x1 = m_ring[i & RingMask];
            float const x2 = m_ring[(i + 1) & RingMask];
            float const x0 = has(i - 1) ? m_ring[(i - 1) & RingMask] : x1;
            float const x3 = has(i + 2) ? m_ring[(i + 2) & RingMask] : x2;
            float const k1 = 0.5f * (x2 - x0);
            float const k2 = x0 - (2.5f * x1) + (2 * x2) - (0.5f * x3);
            float const k3 = (0.5f * (x3 - x0)) + (1.5f * (x1 - x2));
            float v = (((k3 * t) + k2) * t + k1) * t + x1;
            if (m_concealing) {
                m_concealing = false;
                m_blend = 0;
                if (m_gap > 0 && m_gap < ShortGap) {
                    ++m_dropouts;
                    ++m_minuteDropouts;
                    m_minuteLongest = std::max(m_minuteLongest, m_gap);
                }
            }
            if (m_blend < Fade) {
                float const w = ++m_blend / (Fade + 1.0f);
                v = (conceal() * (1 - w)) + (v * w);
            }
            // The cubic overshoots next to full-scale peaks; the virtual mic converts to int16.
            return remember(std::clamp(v, -32768.0f, 32767.0f));
        }
        if (!m_concealing) {
            m_concealing = true;
            m_gap = 0;
            begin_conceal();
        }
        m_gap = std::min(m_gap + 1, ShortGap);
        ++m_missing;
        return remember(conceal());
    }

    float remember(float v)
    {
        m_history[m_historyPos] = v;
        m_historyPos = (m_historyPos + 1) % HistoryLength;
        return v;
    }

    // Loops the last pitch period sent (best normalised autocorrelation over 10 ms), or 15 ms when the sound is not
    // voiced; none for silence. The loop's end is crossfaded into the samples before its start, so it wraps seamlessly.
    void begin_conceal()
    {
        m_concealed = 0;
        m_periodPos = 0;
        m_periodLength = 0;
        // Oldest first: the last PitchWindow + MaxPeriod + Fade samples sent.
        float* const past = m_pitchScratch;
        int const first = m_historyPos - Past + HistoryLength;
        for (int k = 0; k < Past; ++k) {
            past[k] = m_history[(first + k) % HistoryLength];
        }
        float const* const recent = past + Past - PitchWindow;
        double energy = 0;
        for (int n = 0; n < PitchWindow; ++n) {
            energy += recent[n] * recent[n];
        }
        if (std::sqrt(energy / PitchWindow) < SilentRms) {
            return;
        }
        int best = MinPeriod;
        double bestScore = std::numeric_limits<double>::lowest();
        for (int lag = MinPeriod; lag <= MaxPeriod; ++lag) {
            float const* const lagged = past + Past - PitchWindow - lag;
            double cross = 0;
            double power = 0;
            for (int n = 0; n < PitchWindow; ++n) {
                cross += recent[n] * lagged[n];
                power += lagged[n] * lagged[n];
            }
            double const score = power > 0 ? cross / std::sqrt(energy * power) : 0;
            if (score > bestScore) {
                bestScore = score;
                best = lag;
            }
        }
        if (bestScore < VoicedCorrelation) {
            best = MaxPeriod;
        }
        m_periodLength = best;
        std::copy(past + Past - best, past + Past, m_period);
        int const overlap = std::min(best / 4, Fade);
        float const* const before = past + Past - best - overlap;
        for (int k = 0; k < overlap; ++k) {
            float const w = (k + 1) / (overlap + 1.0f);
            int const i = best - overlap + k;
            m_period[i] = (m_period[i] * (1 - w)) + (before[k] * w);
        }
    }

    float conceal()
    {
        if (m_periodLength == 0) {
            return 0;
        }
        float const gain = conceal_gain(m_concealed);
        if (gain <= 0) {
            m_periodLength = 0;
            return 0;
        }
        float const v = m_period[m_periodPos] * gain;
        m_periodPos = (m_periodPos + 1) % m_periodLength;
        ++m_concealed;
        return v;
    }

    SRWLOCK m_lock = SRWLOCK_INIT;
    wpn_jitter_log_cb const m_log;
    void* const m_ctx;
    Lines* m_lines = nullptr; // the running pull's log lines

    // Samples by position on the AirPods' clock; a slot holds the position it was written for (-1: none).
    int16_t m_ring[RingSize] = {};
    int64_t m_slot[RingSize] = {};
    // Arrival clock offsets of the window: the earliest set the clock, the spread to the latest sets the delay.
    WindowExtreme<false> m_earliest;
    WindowExtreme<true> m_latest;
    bool m_anchored = false;
    bool m_resync = false; // anchored since the last pull
    double m_anchoredAt = 0;
    double m_startSpread = StartSpreadSeconds;
    double m_spread = StartSpreadSeconds; // the latest pull's, kept for the next stream's warm-up
    double m_delay = 0;
    double m_play = 0; // playhead, in positions on the AirPods' clock
    int64_t m_received = 0;
    int64_t m_sent = 0;
    int64_t m_missing = 0;
    int64_t m_dropouts = 0;
    int64_t m_late = 0;
    // Concealing since the playhead found nothing (or the stream start); m_gap counts up to ShortGap.
    bool m_concealing = true;
    int m_gap = ShortGap;
    int m_blend = Fade;
    float m_history[HistoryLength] = {};
    float m_period[MaxPeriod] = {};
    float m_pitchScratch[Past] = {};
    int m_historyPos = 0;
    int m_periodLength = 0;
    int m_periodPos = 0;
    int m_concealed = 0;
    int m_minuteSent = 0;
    int m_minuteDropouts = 0;
    int m_minuteLongest = 0;
    int64_t m_minuteLate = 0;
};

} // namespace

void* __stdcall wpn_jitter_create(wpn_jitter_log_cb log, void* ctx)
{
    return new (std::nothrow) Jitter(log, ctx);
}

void __stdcall wpn_jitter_reset(void* jitter)
{
    static_cast<Jitter*>(jitter)->reset();
}

void __stdcall wpn_jitter_push(void* jitter, const int16_t* pcm, uint32_t count, int64_t pos)
{
    static_cast<Jitter*>(jitter)->push(pcm, count, pos);
}

void __stdcall wpn_jitter_pull(void* jitter, float* out, uint32_t count)
{
    static_cast<Jitter*>(jitter)->pull(out, count);
}

void __stdcall wpn_jitter_get_stats(void* jitter, wpn_jitter_stats* stats)
{
    static_cast<Jitter*>(jitter)->stats(stats);
}

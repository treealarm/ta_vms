// Outer bitrate controller for §15. Pure arithmetic -- no codec.

#include "bitrate_budget.h"

#include <algorithm>
#include <cmath>

namespace smartvideo {

namespace {

constexpr int32_t kMaxBackgroundExtra = 20;
constexpr int32_t kMaxRoiSoften = 12;
/// Need a short window of output before reacting, or the first I-frame alone would
/// always look like an overshoot.
constexpr double kMinSeconds = 0.5;
/// How far back the average looks. A lifetime average never recovers from a hot start.
constexpr double kWindowSec = 2.0;

} // namespace

BitrateBudget::BitrateBudget(const RateControlPolicy& policy)
    : m_target_bps(policy.target_bitrate)
    , m_critical_importance(policy.critical_importance)
{
    // Zero on the wire means "use the default deadband", not "zero tolerance".
    m_avg_overshoot = policy.max_average_overshoot > 0.0f
        ? policy.max_average_overshoot
        : 0.1f;
}

void BitrateBudget::observe(int64_t packet_bytes, double duration_sec)
{
    if (!active() || packet_bytes <= 0 || duration_sec <= 0.0) {
        return;
    }
    m_chunks.push_back({duration_sec, packet_bytes});
    m_window_sec += duration_sec;
    m_window_bytes += packet_bytes;
    m_total_sec += duration_sec;

    while (m_window_sec > kWindowSec && m_chunks.size() > 1) {
        m_window_sec -= m_chunks.front().duration;
        m_window_bytes -= m_chunks.front().bytes;
        m_chunks.erase(m_chunks.begin());
    }
    recompute();
}

void BitrateBudget::recompute()
{
    if (!active() || m_total_sec < kMinSeconds || m_window_sec <= 0.0) {
        return;
    }
    // One step per quarter-second of media, not per packet: otherwise a 30 fps stream
    // would climb the whole ladder in under a second of overshoot.
    if (m_total_sec - m_last_step_sec < 0.25) {
        return;
    }
    m_last_step_sec = m_total_sec;

    const double avg_bps =
        (static_cast<double>(m_window_bytes) * 8.0) / m_window_sec;
    const double target = static_cast<double>(m_target_bps);
    const double hi = target * (1.0 + static_cast<double>(m_avg_overshoot));
    const double lo = target * (1.0 - static_cast<double>(m_avg_overshoot));

    if (avg_bps > hi) {
        // Background first: it is what object-first spends from. Softening ROI is the
        // last resort once background is already crushed hard.
        if (m_background_extra < kMaxBackgroundExtra) {
            ++m_background_extra;
        } else if (m_roi_soften < kMaxRoiSoften) {
            ++m_roi_soften;
        }
    } else if (avg_bps < lo) {
        if (m_roi_soften > 0) {
            --m_roi_soften;
        } else if (m_background_extra > 0) {
            --m_background_extra;
        }
    }
}

Quality BitrateBudget::apply(const Quality& base) const
{
    Quality out = base;
    if (m_background_extra != 0) {
        out.background_qp_delta = std::clamp(
            out.background_qp_delta + m_background_extra, -51, 51);
    }
    return out;
}

void BitrateBudget::soften_detections(std::vector<Detection>& detections) const
{
    if (m_roi_soften <= 0) {
        return;
    }
    for (Detection& d : detections) {
        if (d.importance >= m_critical_importance) {
            continue;
        }
        if (d.qp_delta >= 0) {
            continue;
        }
        // Move toward zero; never past it into a background penalty.
        d.qp_delta = std::min(0, d.qp_delta + m_roi_soften);
        d.pad_qp_delta = d.qp_delta / 2;
    }
}

} // namespace smartvideo

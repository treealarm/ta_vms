// libx264 / libx265 -- the fallback that is always available.
//
// Its ROI support is the most capable of the three in one respect: the offsets go in as a
// per-macroblock quantOffsets map, so there is no region count to run out of. The catch is
// that they are applied through adaptive quantisation and are ignored outright when
// aq-mode is 0 -- which is why it is set here and not left to the preset.

#include "backend/common/backend_common.h"
#include "preset.h"

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <format>
#include <string>

extern "C" {
#include <libavutil/opt.h>
}

namespace smartvideo {

namespace {

class SoftwareBackend final : public LibavBackend {
public:
    Encoder kind() const override { return Encoder::Cpu; }

    EncoderCapabilities GetCapabilities(Codec codec) const override
    {
        EncoderCapabilities c;
        c.h264 = true;
        c.h265 = true;
        c.roi_rectangles = true;
        // The offsets are a per-block quantOffsets map underneath, which is what makes the
        // region count unlimited: rectangles are just how FFmpeg describes them on the way
        // in. H.264 cells are macroblocks; for HEVC we paint 32x32 before merge (see
        // EncoderCapabilities::qp_map_block_*).
        c.qp_map = true;
        if (codec == Codec::H264) {
            c.qp_map_block_width = 16;
            c.qp_map_block_height = 16;
        } else {
            c.qp_map_block_width = 32;
            c.qp_map_block_height = 32;
        }
        c.runtime_roi_update = true;
        c.sei = true; // libx264/libx265 udu_sei + AV_FRAME_DATA_SEI_UNREGISTERED
        // CBR+filler when target_bitrate is set: mux bitrate matches; ROI offsets
        // redistribute coded quality inside the budget (§15–16). QSV/VA-API stay on CQP.
        c.native_bitrate = true;
        c.max_roi_regions = 0;  // no limit worth naming
        return c;
    }

    const char* codec_name(Codec codec) const override
    {
        return codec == Codec::H264 ? "libx264" : "libx265";
    }

    Status configure_context(LibavSession& out, const EncoderConfig& p) const override
    {
        out.enc.p->pix_fmt = AV_PIX_FMT_YUV420P;
        out.sw_pix = AV_PIX_FMT_YUV420P;
        av_opt_set(out.enc.p->priv_data, "preset", libx26x_preset(p.preset), 0);
        av_opt_set(out.enc.p->priv_data, "aq-mode", "1", 0);
        // Accept AV_FRAME_DATA_SEI_UNREGISTERED as user-data-unregistered SEI (§19).
        av_opt_set_int(out.enc.p->priv_data, "udu_sei", 1, 0);

        if (p.rate_control.target_bitrate > 0) {
            // Native bitrate mode (TZ v3 §15–16). Do not also set CRF.
            //
            // CBR + filler: both baseline and adaptive land on the same mux bitrate, which
            // is what §16's 2% gate needs. Soft ABR alone underspends on easy frames and
            // overshoots when ROI offsets demand bits — the two sides never meet.
            // Strict VBV without filler underflows on strong negative ROI deltas.
            // Filler NALUs pad the shortfall so the average matches; coded quality is still
            // steered by ROI QP offsets + BitrateBudget.
            const int64_t target = static_cast<int64_t>(p.rate_control.target_bitrate);
            // Peak ceiling (default +20%): both baseline and adaptive lock to the same
            // HRD rate with filler, which is what §16's 2% gate needs. Setting maxrate
            // equal to target leaves ROI-heavy encodes above the request with no shared
            // ceiling to meet on.
            const float peak_frac = p.rate_control.max_peak_overshoot > 0.0f
                ? p.rate_control.max_peak_overshoot
                : 0.2f;
            const int64_t maxrate = std::max<int64_t>(target,
                static_cast<int64_t>(std::llround(
                    static_cast<double>(target) * (1.0 + static_cast<double>(peak_frac)))));
            const int64_t buf = std::min<int64_t>(
                target * 2, static_cast<int64_t>(INT32_MAX));
            out.enc.p->bit_rate = target;
            out.enc.p->rc_max_rate = maxrate;
            out.enc.p->rc_buffer_size = static_cast<int>(buf);
            if (p.codec == Codec::H264) {
                // Filler makes undershoot meet the HRD rate so baseline≈adaptive (§16).
                av_opt_set(out.enc.p->priv_data, "x264-params",
                    "nal-hrd=cbr:filler=1", 0);
            } else {
                av_opt_set(out.enc.p->priv_data, "x265-params",
                    std::format("vbv-maxrate={}:vbv-bufsize={}:strict-cbr=1",
                        maxrate / 1000, buf / 1000)
                        .c_str(),
                    0);
            }
        } else {
            av_opt_set(out.enc.p->priv_data, "crf", std::to_string(p.crf).c_str(), 0);
        }
        return Status::Ok();
    }
};

} // namespace

const IEncoderBackend& software_backend()
{
    static const SoftwareBackend instance;
    return instance;
}

} // namespace smartvideo

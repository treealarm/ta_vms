// libx264 / libx265 -- the fallback that is always available.
//
// Its ROI support is the most capable of the three in one respect: the offsets go in as a
// per-macroblock quantOffsets map, so there is no region count to run out of. The catch is
// that they are applied through adaptive quantisation and are ignored outright when
// aq-mode is 0 -- which is why it is set here and not left to the preset.

#include "backend/common/backend_common.h"
#include "preset.h"

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
        av_opt_set(out.enc.p->priv_data, "crf", std::to_string(p.crf).c_str(), 0);
        av_opt_set(out.enc.p->priv_data, "aq-mode", "1", 0);
        // Accept AV_FRAME_DATA_SEI_UNREGISTERED as user-data-unregistered SEI (§19).
        av_opt_set_int(out.enc.p->priv_data, "udu_sei", 1, 0);
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

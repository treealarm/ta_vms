#include "smartvideo/session.h"

#include "av_raii.h"
#include "bitrate_budget.h"
#include "box_smoother.h"
#include "frame_input.h"
#include "object_normalizer.h"
#include "packet_sink.h"
#include "qp_slew.h"
#include "roi_lag.h"
#include "roi_encoder.h"
#include "smartvideo/metadata.h"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <limits>
#include <map>
#include <span>

namespace smartvideo {

namespace {

// Regions belong to the frame they were submitted with. A decoder reorders, so
// once frames go in compressed the caller's pointers cannot simply be applied
// to whatever comes out next -- they have to be carried across and matched up.
struct HeldRegions {
    std::vector<uint8_t> mask_bytes;
    Mask mask;
    std::vector<Box> boxes;
    QpDeltaMap qp_map;
    bool has_mask = false;

    Regions view() const
    {
        Regions r;
        if (!qp_map.deltas.empty()) {
            r.qp_map = qp_map;
        } else if (has_mask) {
            r.mask = mask;
        } else if (!boxes.empty()) {
            r.boxes = boxes;
        }
        return r;
    }
};

HeldRegions hold(const Regions& in)
{
    HeldRegions h;
    if (!in.qp_map.deltas.empty()) {
        h.qp_map = in.qp_map;
    } else if (in.mask.consistent()) {
        const int32_t stride = in.mask.stride > 0 ? in.mask.stride : in.mask.width;
        h.mask_bytes.resize(
            static_cast<size_t>(in.mask.width) * in.mask.height);
        for (int32_t y = 0; y < in.mask.height; ++y) {
            std::copy_n(in.mask.data.data() + static_cast<size_t>(y) * stride,
                in.mask.width,
                h.mask_bytes.begin() + static_cast<size_t>(y) * in.mask.width);
        }
        h.mask.data = h.mask_bytes;
        h.mask.width = in.mask.width;
        h.mask.height = in.mask.height;
        h.mask.stride = in.mask.width;
        h.has_mask = true;
    } else if (!in.boxes.empty()) {
        h.boxes.assign(in.boxes.begin(), in.boxes.end());
    }
    return h;
}

float center_distance_diag(const Box& a, const Box& b)
{
    const float ax = a.x + 0.5f * a.width;
    const float ay = a.y + 0.5f * a.height;
    const float bx = b.x + 0.5f * b.width;
    const float by = b.y + 0.5f * b.height;
    const float dx = bx - ax;
    const float dy = by - ay;
    constexpr float diagonal = 1.41421356f; // sqrt(2) in normalized space
    return static_cast<float>(std::sqrt(dx * dx + dy * dy) / diagonal);
}

void fill_lag_report(const std::vector<RoiLagSample>& samples, Report& report)
{
    report.roi_lag_measured = !samples.empty();
    report.roi_lag_samples = static_cast<int64_t>(samples.size());
    if (samples.empty()) {
        return;
    }

    std::vector<double> frames;
    std::vector<double> ms;
    frames.reserve(samples.size());
    ms.reserve(samples.size());
    float min_iou = std::numeric_limits<float>::max();
    for (const RoiLagSample& s : samples) {
        min_iou = std::min(min_iou, s.iou);
        frames.push_back(static_cast<double>(s.lag_frames));
        ms.push_back(s.lag_ms);
    }
    report.roi_lag_min_iou = min_iou;
    report.roi_lag_p95_frames = percentile(frames, 0.95);
    report.roi_lag_p99_frames = percentile(frames, 0.99);
    report.roi_lag_p95_ms = percentile(ms, 0.95);
    report.roi_lag_p99_ms = percentile(ms, 0.99);
    report.roi_lag_acceptance = roi_lag_acceptance(samples);
}

} // namespace

// Sits between the encoder and wherever packets are going: a file it owns, or
// the caller's handler. Counts them on the way through so finish() can report
// without the caller tallying.
struct Session::Impl : PacketSink {
    SessionParams params;
    std::unique_ptr<FileMuxSink> file;
    PacketHandler* handler = nullptr;
    RoiEncoder encoder;
    BitrateBudget bitrate;
    QpSlewLimiter qp_slew;
    BoxSmoother box_smooth;
    FrameDecoder decoder;
    RoiMapBuilder builder;
    roiav::Frame wrapper;
    Report report;
    std::vector<RoiLagSample> lag_samples;
    bool finished = false;
    // Submitted-but-not-yet-decoded regions, keyed by the pts they arrived with.
    std::map<int64_t, HeldRegions> pending;

    // Matches a decoded frame to the regions submitted with it. Falls back to
    // the oldest entry when the stream carries no usable timestamps, which is
    // the right guess for a decoder that is not reordering.
    HeldRegions take_pending(int64_t pts, bool have_pts)
    {
        if (pending.empty()) {
            return HeldRegions{};
        }
        auto it = have_pts ? pending.find(pts) : pending.end();
        if (it == pending.end()) {
            if (have_pts) {
                return HeldRegions{};
            }
            it = pending.begin();
        }
        HeldRegions held = std::move(it->second);
        // Anything older can no longer be claimed: the decoder emits in
        // presentation order, so those frames were dropped or already handled.
        pending.erase(pending.begin(), std::next(it));
        return held;
    }

    void begin(AVCodecContext* enc) override
    {
        if (file) {
            file->begin(enc);
        }
        if (handler) {
            StreamInfo info;
            info.width = enc->width;
            info.height = enc->height;
            if (enc->extradata && enc->extradata_size > 0) {
                info.extradata = std::span<const uint8_t>(enc->extradata,
                    static_cast<std::size_t>(enc->extradata_size));
            }
            info.encoder_used = report.encoder_used;
            info.encoder_name = report.encoder_name;
            handler->on_open(info);
        }
    }

    void on_packet(AVPacket* pkt, AVRational tb) override
    {
        ++report.packets;
        report.bytes += pkt->size;
        if (bitrate.active()) {
            double dt = 0.0;
            if (pkt->duration > 0 && tb.den > 0) {
                dt = static_cast<double>(pkt->duration) * tb.num / tb.den;
            }
            if (dt <= 0.0) {
                const int32_t den = std::max(params.fps_den, 1);
                dt = params.fps_num > 0
                    ? static_cast<double>(den) / static_cast<double>(params.fps_num)
                    : 1.0 / 25.0;
            }
            bitrate.observe(pkt->size, dt);
        }
        if (handler) {
            EncodedPacket out;
            out.data = std::span<const uint8_t>(
                pkt->data, static_cast<std::size_t>(pkt->size));
            out.pts = pkt->pts;
            out.dts = pkt->dts;
            out.duration = pkt->duration;
            // The units the timestamps above are in. Filling this is not optional: the
            // field defaults to 1/1, and a handler that believed it while the session runs
            // in milliseconds -- which is the default -- would be out by a factor of a
            // thousand, consistently, in a way that looks like working code.
            out.time_base = Rational{tb.num, tb.den};
            out.key = (pkt->flags & AV_PKT_FLAG_KEY) != 0;
            handler->on_packet(out);
        }
        if (file) {
            file->on_packet(pkt, tb);
        }
    }

    void end() override
    {
        if (file) {
            file->end();
        }
        if (handler) {
            handler->on_end();
        }
    }

    /// How many rectangle regions may actually be sent this frame.
    ///
    /// Two numbers meet here: what the caller asked for, and what the backend that opened
    /// will accept (TZ v2.0 §6.4). The smaller wins. Before this the setting was obeyed
    /// alone, which meant a request for 200 regions was silently truncated by the driver
    /// instead of by us -- and the count in Report.regions_dropped was wrong, because we
    /// did not know the loss had happened.
    ///
    /// When the backend exposes qp_map, the budget is not a rectangle cap: return 0 so
    /// callers treat the map as unlimited (rects_from_qp_map / truncate skip on 0).
    int region_budget() const
    {
        const EncoderCapabilities caps = encoder.capabilities();
        if (caps.qp_map) {
            return 0;
        }
        const int asked = params.settings.quality.max_regions;
        const uint32_t limit = caps.max_roi_regions;
        if (limit == 0) {
            return asked;  // the backend names no limit; the caller's word stands
        }
        const int cap = static_cast<int>(limit);
        return asked > 0 ? std::min(asked, cap) : cap;
    }

    int32_t coding_block() const
    {
        const int32_t b = qp_map_block(encoder.capabilities());
        // Rectangle-only backends still paint through a grid before merge; 16 is
        // fine for both codecs (finer than a CTU, still cheap to merge).
        return b > 0 ? b : 16;
    }

    std::vector<Rect> rects_for(const Regions& regions)
    {
        const int32_t w = params.width;
        const int32_t h = params.height;
        const int32_t block = coding_block();
        const int budget = region_budget();

        if (!regions.qp_map.deltas.empty()) {
            int32_t dropped = 0;
            auto rects = rects_from_qp_map(regions.qp_map, budget, &dropped);
            report.regions_dropped += dropped;
            return rects;
        }

        if (regions.mask.consistent()) {
            // The mask has no kind names: it interpolates between the
            // background and the strongest strength the settings name.
            const Quality quality = bitrate.apply(params.settings.quality);
            MaskMapping mapping;
            mapping.qp_delta_at_0 = quality.background_qp_delta;
            mapping.qp_delta_at_255 = quality.mask_qp_delta_at_255;
            mapping.levels = quality.mask_levels;
            const QpDeltaMap grid =
                qp_map_from_mask(regions.mask, mapping, w, h, block);
            int32_t dropped = 0;
            auto rects = rects_from_qp_map(grid, budget, &dropped);
            report.regions_dropped += dropped;
            return rects;
        }

        const std::vector<Box> box_list(regions.boxes.begin(), regions.boxes.end());
        std::vector<Box> smoothed = box_list;
        box_smooth.apply(smoothed, params.settings.temporal);
        for (const Box& applied : smoothed) {
            if (applied.track_id == 0) {
                continue;
            }
            const auto it = std::find_if(box_list.begin(), box_list.end(),
                [&](const Box& b) {
                    return b.track_id == applied.track_id && b.kind == applied.kind;
                });
            if (it == box_list.end()) {
                continue;
            }
            int32_t lag_frames = 0;
            const float speed = std::max(it->track.speed, 0.0f);
            if (speed > 1e-6f) {
                const int32_t den = std::max(params.fps_den, 1);
                const double frame_sec = params.fps_num > 0
                    ? static_cast<double>(den) / static_cast<double>(params.fps_num)
                    : 1.0 / 25.0;
                const double per_frame = static_cast<double>(speed) * frame_sec;
                const double dist = static_cast<double>(center_distance_diag(applied, *it));
                lag_frames = static_cast<int32_t>(std::lround(dist / per_frame));
                lag_samples.push_back(measure_roi_lag(applied, *it, lag_frames, frame_sec));
            } else {
                lag_samples.push_back(measure_roi_lag(applied, *it, 0, 0.0));
            }
        }
        auto dets =
            builder.detections_from(smoothed, params.settings.quality, w, h);
        bitrate.soften_detections(dets);
        // See transcode_file: slew would choke BitrateBudget under a target bitrate.
        const int32_t slew = bitrate.active()
            ? 0
            : params.settings.temporal.max_qp_delta_per_frame;
        qp_slew.apply(dets, slew);
        const Quality quality = bitrate.apply(params.settings.quality);
        auto rects = builder.build(w, h, dets, quality, block, budget);
        if (budget > 0 && static_cast<int>(rects.size()) > budget) {
            int32_t lost = 0;
            truncate_roi_rects(rects, budget, w, h, &lost);
            report.regions_dropped += lost;
        }
        return rects;
    }

    std::vector<uint8_t> sei_bytes(const Regions& regions, int64_t pts)
    {
        if (!params.settings.metadata.bitstream_sei) {
            return {};
        }
        if (!encoder.capabilities().sei) {
            return {};
        }
        SeiFrame frame;
        frame.frame_id = static_cast<uint64_t>(report.frames);
        frame.pts = pts;
        if (regions.qp_map.deltas.empty() && !regions.mask.consistent()) {
            frame.objects = sei_objects_from_boxes(
                regions.boxes, params.settings.metadata.max_objects);
        }
        return encode_sei_side_data(frame, params.settings.metadata.max_objects);
    }

    // Pixels the caller owns, handed straight to the encoder. No decode, no wrapping:
    // this is the path a factory frame takes, and the one where zero copies is possible.
    void encode_buffer(IFrameBuffer& pixels, const Regions& regions, int64_t pts)
    {
        const auto sei = sei_bytes(regions, pts);
        encoder.encode_buffer(pixels, rects_for(regions), pts, sei);
        ++report.frames;
    }

    void encode(AVFrame* frame, const Regions& regions, int64_t pts)
    {
        const auto sei = sei_bytes(regions, pts);
        encoder.encode(frame, rects_for(regions), pts, sei);
        ++report.frames;
    }
};

Session::Session(const SessionParams& params, PacketHandler* handler)
    : m_impl(std::make_unique<Impl>())
{
    if (params.width <= 0 || params.height <= 0) {
        roiav::fail("session needs a frame size");
    }
    if (params.output_path.empty() && !handler) {
        roiav::fail("session needs either an output_path or a packet handler");
    }

    m_impl->params = params;
    m_impl->handler = handler;
    if (!params.output_path.empty()) {
        m_impl->file = std::make_unique<FileMuxSink>(
            params.output_path, params.settings.movflags);
    }

    m_impl->decoder.open(params.codec,
        params.extradata.empty() ? nullptr : params.extradata.data(),
        static_cast<int>(params.extradata.size()));

    EncoderConfig ep;
    ep.width = params.width;
    ep.height = params.height;
    ep.fps = Rational{params.fps_num, std::max(params.fps_den, 1)};
    ep.time_base =
        Rational{params.time_base_num, std::max(params.time_base_den, 1)};
    ep.kind = params.settings.encoder;
    ep.policy = params.settings.policy;
    ep.temporal = params.settings.temporal;
    ep.rate_control = params.settings.rate_control;
    // SameAsSource, answered from what the caller said it is submitting. Raw frames have no
    // source codec to mirror, so they take the same H.264 fallback an unrecognised one does.
    ep.codec = params.settings.codec != Codec::SameAsSource ? params.settings.codec
        : (params.codec == InputCodec::H265 ? Codec::H265 : Codec::H264);
    bool cpu_h265_target_guard = false;
    if (ep.kind == Encoder::Cpu && ep.codec == Codec::H265) {
        // Guard: CPU HEVC target-bitrate path is unstable on our stack.
        cpu_h265_target_guard = ep.rate_control.target_bitrate > 0;
        ep.rate_control.target_bitrate = 0;
        if (cpu_h265_target_guard) {
            std::fprintf(stderr,
                "guard: cpu h265 target-bitrate disabled; using CRF/CQP\n");
        }
    }
    ep.crf = params.settings.crf;
    ep.gop = params.settings.gop;
    ep.preset = params.settings.preset;

    // The report is filled before begin() runs, because StreamInfo carries the
    // backend and the sink is what reports it.
    m_impl->encoder.open_deferred(ep);
    m_impl->report.encoder_used = m_impl->encoder.kind();
    m_impl->report.encoder_name = m_impl->encoder.label();
    m_impl->report.cpu_h265_target_guard = cpu_h265_target_guard;
    m_impl->bitrate = BitrateBudget(ep.rate_control);
    m_impl->encoder.attach(*m_impl);
}

Session::~Session() = default;

Encoder Session::encoder_used() const
{
    return m_impl->report.encoder_used;
}

std::string Session::encoder_name() const
{
    return m_impl->report.encoder_name;
}

void Session::submit(IFrameBuffer& pixels, const Regions& regions, int64_t pts)
{
    if (m_impl->params.codec != InputCodec::Raw) {
        roiav::fail("this session was opened with a codec; submit an access unit");
    }
    m_impl->encode_buffer(pixels, regions, pts);
}

void Session::submit(
    std::span<const uint8_t> access_unit, const Regions& regions, int64_t pts)
{
    if (m_impl->params.codec == InputCodec::Raw) {
        roiav::fail("this session was opened for raw frames; submit pixels");
    }
    if (access_unit.empty()) {
        return;
    }
    m_impl->pending.emplace(pts, hold(regions));
    m_impl->decoder.push(access_unit.data(), access_unit.size(), pts,
        [&](AVFrame* decoded) {
            const bool have_pts =
                decoded->best_effort_timestamp != AV_NOPTS_VALUE;
            const int64_t decoded_pts =
                have_pts ? decoded->best_effort_timestamp : pts;
            const HeldRegions held = m_impl->take_pending(decoded_pts, have_pts);
            m_impl->encode(decoded, held.view(), decoded_pts);
        });
}

Report Session::finish()
{
    if (!m_impl->finished) {
        m_impl->finished = true;
        m_impl->decoder.flush([&](AVFrame* decoded) {
            const bool have_pts =
                decoded->best_effort_timestamp != AV_NOPTS_VALUE;
            const int64_t pts = have_pts ? decoded->best_effort_timestamp : 0;
            const HeldRegions held = m_impl->take_pending(pts, have_pts);
            m_impl->encode(decoded, held.view(), pts);
        });
        m_impl->encoder.flush();
        m_impl->end();
        m_impl->report.frames_converted = m_impl->encoder.frames_converted();
        m_impl->report.frames_uploaded = m_impl->encoder.frames_uploaded();
        fill_lag_report(m_impl->lag_samples, m_impl->report);
    }
    return m_impl->report;
}

} // namespace smartvideo

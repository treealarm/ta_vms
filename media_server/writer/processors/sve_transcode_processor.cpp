#include "writer/processors/sve_transcode_processor.h"

#include <algorithm>
#include <ranges>
#include <cstdlib>
#include <cstring>
#include <format>
#include <optional>
#include <stdexcept>
#include <span>
#include <string>
#include <vector>

extern "C" {
#include <libavutil/mathematics.h>
#include <libavutil/mem.h>
}

#include "sve/backend_selection.h"
#include "sve/packet_handler.h"
#include "sve/report.h"
#include "sve/session.h"
#include "sve/types.h"

#include "interfaces/media_packet.h"
#include "logger/logger.h"

namespace {

// VMS_VIDEO_ENCODER names the backend chain, in whatever spelling sve::backend_choice_from_name
// accepts ("auto", "vaapi", "x264", "cpu", ...). Unknown text throws rather than defaulting: a
// misspelled backend quietly becoming a different one is the whole failure this guards against.
//
// Unset is not the same as "auto". sve ships no default order on purpose -- §8.4's position is
// that the only honest default is the one whoever deploys the system chooses for their own
// hardware -- so this is ours, and it names one backend: VA-API, which is the only hardware
// encoder ta-deps builds ffmpeg with. Writing `auto` explicitly still gets the library's full
// order, QSV and NVENC included, and they will decline in turn on a build without them.
sve::BackendChoice encoder_choice_from_env()
{
  const char* raw = std::getenv("VMS_VIDEO_ENCODER");
  if (raw && *raw != '\0') {
    return sve::backend_choice_from_name(raw);
  }

  sve::BackendChoice choice;
  choice.kind = sve::Backend::Vaapi;
  return choice;
}

bool is_hardware(sve::Backend backend)
{
  switch (backend) {
    case sve::Backend::Vaapi:
    case sve::Backend::IntelOneVpl:
    case sve::Backend::NvidiaNvenc:
      return true;
    default:
      return false;
  }
}

// Whether this choice can reach hardware at all.
//
// It decides whether sve is used for the stream or whether compat_sink keeps the CPU processor
// it had, and the reason is measured rather than stylistic: sve's software backend sets libx264's
// `preset` but no `tune`, so it runs with x264's default lookahead -- 34 frames in flight on a
// 1080p25 clip, about 1.4 seconds, against 4 frames through h264_vaapi. video_transcode_processor
// asks for `tune=zerolatency` and does not have that delay, and for live video a second and a
// half is not a trade worth making for anything.
bool can_reach_hardware(const sve::BackendChoice& choice)
{
  if (choice.kind != sve::Backend::Auto) {
    return is_hardware(choice.kind);
  }
  return std::ranges::any_of(choice.selection.preferred, is_hardware);
}

std::optional<sve::InputCodec> input_codec_of(AVCodecID id)
{
  switch (id) {
    case AV_CODEC_ID_H264: return sve::InputCodec::H264;
    case AV_CODEC_ID_HEVC: return sve::InputCodec::H265;
    case AV_CODEC_ID_MJPEG: return sve::InputCodec::Jpeg;
    default: return std::nullopt;
  }
}

// The average bitrate to aim for, in bits per second.
//
// video_transcode_processor computed a *peak* here and fed it to rc_max_rate/rc_buffer_size, to
// stop keyframes bursting ten to twenty times the average and stalling the WebRTC client's
// jitter buffer -- the periodic freeze on HEVC->H.264 streams. sve's knob is the average, and it
// derives its own ~1s peak allowance from it, so the same reasoning arrives at half the old
// number: the old ceiling was explicitly twice what the source averaged.
int64_t target_bitrate_for(const AVCodecParameters& params, double fps)
{
  const int64_t source = params.bit_rate > 0 ? params.bit_rate : 0;
  const int64_t target = source > 0
    ? source
    : static_cast<int64_t>(static_cast<double>(params.width) * params.height * fps * 0.075);

  return std::clamp<int64_t>(target, 500'000, 10'000'000);
}

avcodec_parameters_ptr make_output_parameters(const sve::StreamInfo& info)
{
  avcodec_parameters_ptr params(avcodec_parameters_alloc(),
    [](AVCodecParameters* p) { avcodec_parameters_free(&p); });
  if (!params) {
    return nullptr;
  }

  params->codec_type = AVMEDIA_TYPE_VIDEO;
  params->codec_id = AV_CODEC_ID_H264;
  params->width = info.width;
  params->height = info.height;

  // The RTSP muxer puts this in the SDP, so a stream without it decodes nowhere.
  if (!info.extradata.empty()) {
    const std::size_t size = info.extradata.size();
    auto* buffer = static_cast<uint8_t*>(av_mallocz(size + AV_INPUT_BUFFER_PADDING_SIZE));
    if (!buffer) {
      return nullptr;
    }
    std::memcpy(buffer, info.extradata.data(), size);
    params->extradata = buffer;
    params->extradata_size = static_cast<int>(size);
  }

  return params;
}

}  // namespace

// Encoded packets on their way back out to compat_sink's sinks.
class sve_transcode_processor::packet_sink final : public sve::PacketHandler {
public:
  packet_sink(sve_transcode_processor& owner, emit_fn emit)
    : m_owner(owner), m_emit(std::move(emit))
  {
  }

  void on_open(const sve::StreamInfo& info) override
  {
    m_out_codec_parameters = make_output_parameters(info);
    if (!m_out_codec_parameters) {
      logger::instance().error("sve_transcode_processor: failed to build output codec parameters");
    }

    logger::instance().info(std::format(
      "sve_transcode_processor: encoding {}x{} with backend '{}'",
      info.width, info.height, info.backend_name));
  }

  void on_packet(const sve::EncodedPacket& packet) override
  {
    if (!m_emit || packet.payload.empty()) {
      return;
    }

    avpacket_ptr pkt(av_packet_alloc(), avpacket_deleter{});
    if (!pkt || av_new_packet(pkt.get(), static_cast<int>(packet.payload.size())) < 0) {
      logger::instance().warning("sve_transcode_processor: failed to allocate output packet");
      return;
    }

    std::memcpy(pkt->data, packet.payload.data(), packet.payload.size());
    // The encoder's own timestamps are discarded in favour of the sanitized timeline, which is
    // the only one that survives a camera resyncing its clock. dts equals pts because the encoder
    // runs with max_b_frames = 0, so output order is display order.
    const int64_t pts = m_owner.take_output_pts();
    pkt->pts = pts;
    pkt->dts = pts;
    pkt->duration = m_owner.m_frame_step;
    pkt->stream_index = 0;
    if (packet.key_frame) {
      pkt->flags |= AV_PKT_FLAG_KEY;
    }

    auto out_media = std::make_shared<media_packet>();
    out_media->packet = std::move(pkt);
    out_media->stream_index = 0;
    out_media->time_base = m_owner.m_out_time_base;
    out_media->media_type = AVMEDIA_TYPE_VIDEO;
    // Every emitted packet carries them: rtsp_output_sink initializes lazily from the packet
    // itself rather than from output_codec_parameters(), and drops anything that arrives without.
    out_media->codec_parameters = m_out_codec_parameters;

    ++m_emitted;
    m_emit(out_media);
  }

  const avcodec_parameters_ptr& output_codec_parameters() const { return m_out_codec_parameters; }
  int64_t emitted() const { return m_emitted; }

private:
  sve_transcode_processor& m_owner;
  emit_fn m_emit;
  avcodec_parameters_ptr m_out_codec_parameters;
  int64_t m_emitted = 0;
};

// Ten seconds at 25 fps: enough that no legitimate decoder delay reaches it, small enough that
// a starved session cannot grow the queue into a leak.
constexpr std::size_t kMaxPendingTimestamps = 250;
// Roughly ten seconds of failures at 25 fps.
constexpr int64_t kSubmitFailureLogInterval = 250;

sve_transcode_processor::sve_transcode_processor() = default;

sve_transcode_processor::~sve_transcode_processor()
{
  flush();
}

void sve_transcode_processor::validate_configuration()
{
  try {
    const sve::BackendChoice choice = encoder_choice_from_env();
    std::string order;
    for (const sve::Backend backend : choice.selection.preferred) {
      if (!order.empty()) {
        order += " -> ";
      }
      order += sve::backend_name(backend);
    }
    logger::instance().info(std::format(
      "sve_transcode_processor: video encoder backend order is {}",
      order.empty() ? sve::backend_name(choice.kind) : order));
  }
  catch (const std::exception& ex) {
    // Re-thrown only to name the variable. sve's own refusal already lists the spellings it
    // accepts, which is why no copy of that list is kept here to go stale.
    throw std::invalid_argument(std::format("VMS_VIDEO_ENCODER: {}", ex.what()));
  }
}

bool sve_transcode_processor::init(
  const avcodec_parameters_ptr& codec_parameters,
  AVRational time_base,
  AVRational frame_rate,
  emit_fn emit)
{
  if (!codec_parameters) {
    logger::instance().warning("sve_transcode_processor: empty codec parameters");
    return false;
  }

  const auto input_codec = input_codec_of(codec_parameters->codec_id);
  if (!input_codec) {
    logger::instance().error(std::format(
      "sve_transcode_processor: unsupported input codec id {}",
      static_cast<int>(codec_parameters->codec_id)));
    return false;
  }

  if (codec_parameters->width <= 0 || codec_parameters->height <= 0) {
    logger::instance().error("sve_transcode_processor: input has no frame size");
    return false;
  }

  m_emit = std::move(emit);

  // Keep the source (90kHz) time_base for PTS precision; rate control's fps comes separately.
  // Deriving fps from that clock would give ~90000 fps and wreck the bitrate model.
  m_in_time_base = (time_base.num > 0 && time_base.den > 0) ? time_base : AVRational{ 1, 25 };
  const AVRational fps = (frame_rate.num > 0 && frame_rate.den > 0) ? frame_rate : AVRational{ 25, 1 };

  sve::SessionParams params;
  params.width = codec_parameters->width;
  params.height = codec_parameters->height;
  params.time_base_num = m_in_time_base.num;
  params.time_base_den = m_in_time_base.den;
  params.fps_num = fps.num;
  params.fps_den = fps.den;
  params.codec = *input_codec;
  if (codec_parameters->extradata && codec_parameters->extradata_size > 0) {
    params.extradata.assign(
      codec_parameters->extradata,
      codec_parameters->extradata + codec_parameters->extradata_size);
  }

  const sve::BackendChoice choice = encoder_choice_from_env();
  if (!can_reach_hardware(choice)) {
    // Not a failure: compat_sink falls back to the CPU processor, which is the better software
    // encoder of the two. Logged at debug so it does not repeat as noise on every stream.
    logger::instance().debug(
      "sve_transcode_processor: no hardware backend requested, leaving this stream to the CPU path");
    return false;
  }

  params.settings.encoder = choice.kind;
  params.settings.backend_selection = choice.selection;
  params.settings.codec = sve::Codec::H264;
  // libx264's own default, which is what the CPU path used by never setting one. sve's default
  // of 28 is tuned for archive re-compression, where a visibly softer picture buys disk space.
  params.settings.crf = 23;
  // gop 0 is two seconds, which is what a WebRTC viewer wants: a late joiner waits that long for
  // a picture, and a lost keyframe costs that long to recover.
  params.settings.gop = 0;
  params.settings.preset = sve::Preset::Fastest;
  // sve exists to spend fewer bits away from the regions a detector found, and its default
  // penalises the background by 6 QP. We submit no regions at all, so that default would apply
  // to the whole picture and hand the viewer a visibly softer stream than the CPU path did.
  // Zero is "leave the background alone", which for a plain transcode is the entire job.
  params.settings.quality.background_qp_delta = 0;
  params.settings.rate_control.target_bitrate =
    static_cast<uint64_t>(target_bitrate_for(*codec_parameters, av_q2d(fps)));

  m_sink = std::make_unique<packet_sink>(*this, m_emit);

  try {
    m_session = std::make_unique<sve::Session>(params, m_sink.get());
  }
  catch (const std::exception& ex) {
    // Nothing above this catches: compat_sink is driven straight from the RTSP read loop, and an
    // exception let out here takes the whole service down over one misconfigured camera.
    logger::instance().error(std::format("sve_transcode_processor: failed to open session: {}", ex.what()));
    m_session.reset();
    m_sink.reset();
    return false;
  }

  // What was asked for is not what opened. `VMS_VIDEO_ENCODER=auto` asks sve for its whole chain,
  // and on a host without /dev/dri that chain ends at X264 -- inside a session that has already
  // opened, so nothing above would notice. That is the one outcome this processor must not
  // produce: sve's software backend sets libx264's preset but no tune, and measured 34 frames in
  // flight against 4 through h264_vaapi. Handing the stream back lets compat_sink build
  // video_transcode_processor, which asks for tune=zerolatency and does not have that delay.
  //
  // The encoder alone decides this, and the decoder deliberately does not enter into it. sve now
  // decodes on the encoder's device when it has one, but a decoder that falls back to software --
  // a profile the silicon does not implement -- still leaves a hardware encode worth having: that
  // was the whole 43% before the decode was accelerated at all. Handing the stream back over the
  // decoder would trade a real saving for none.
  if (!is_hardware(m_session->encoder_used())) {
    logger::instance().info(std::format(
      "sve_transcode_processor: backend chain settled on '{}', which is software -- leaving this "
      "stream to the CPU path, which encodes with tune=zerolatency",
      m_session->backend_name()));
    m_session.reset();
    m_sink.reset();
    return false;
  }

  logger::instance().info(std::format(
    "sve_transcode_processor: {} -> h264, backend '{}', target {} bit/s",
    avcodec_get_name(codec_parameters->codec_id),
    m_session->backend_name(),
    params.settings.rate_control.target_bitrate));

  m_out_time_base = m_in_time_base;
  m_frame_step = std::max<int64_t>(1, av_rescale_q(1, av_inv_q(fps), m_in_time_base));
  m_last_out_pts = AV_NOPTS_VALUE;
  m_last_in_ts = AV_NOPTS_VALUE;
  m_wallclock_base_us = -1;
  m_wallclock_base_pts = 0;
  m_pending_pts.clear();
  m_last_emitted_pts = AV_NOPTS_VALUE;
  m_submit_failures = 0;
  m_path_reported = false;
  m_submitted = 0;
  m_starvation_reported = false;
  return true;
}

int64_t sve_transcode_processor::take_output_pts()
{
  // Empty means the encoder handed back more packets than access units went in -- flush() can,
  // and a decoder that emitted two frames for one submission would. Extrapolating one frame
  // forward keeps the timeline strictly increasing, which is all the muxer requires.
  if (m_pending_pts.empty()) {
    m_last_emitted_pts = m_last_emitted_pts == AV_NOPTS_VALUE
      ? 0
      : m_last_emitted_pts + m_frame_step;
    return m_last_emitted_pts;
  }
  m_last_emitted_pts = m_pending_pts.front();
  m_pending_pts.pop_front();
  return m_last_emitted_pts;
}

void sve_transcode_processor::note_input_timestamp(const AVPacket* packet, AVRational time_base)
{
  const int64_t in_ts = packet->pts != AV_NOPTS_VALUE ? packet->pts : packet->dts;
  if (in_ts == AV_NOPTS_VALUE) {
    return;
  }

  // A backward jump of more than 5s means the source timeline restarted (RTSP
  // reconnect): let the output pts restart with it instead of clamping every
  // following frame to last+1.
  const int64_t restart_threshold = av_rescale_q(5, AVRational{ 1, 1 }, time_base);
  if (m_last_in_ts != AV_NOPTS_VALUE && in_ts + restart_threshold < m_last_in_ts) {
    m_last_out_pts = AV_NOPTS_VALUE;
    m_wallclock_base_us = -1;
  }
  m_last_in_ts = in_ts;
}

int64_t sve_transcode_processor::next_output_pts(const std::shared_ptr<media_packet>& media)
{
  // Live path: the receive clock is authoritative when available. Overloaded cameras stamp
  // frames at the nominal frame interval (e.g. 50ms at "20fps") while actually delivering
  // slower (~75ms), then resync their clock with a multi-second forward pts jump — any
  // pts-honoring player freezes on every jump. Arrival time has jitter but neither drift
  // nor jumps.
  const int64_t arrival_us = media->arrival_us;
  if (arrival_us >= 0) {
    if (m_wallclock_base_us < 0) {
      m_wallclock_base_us = arrival_us;
      m_wallclock_base_pts = m_last_out_pts == AV_NOPTS_VALUE ? 0 : m_last_out_pts + m_frame_step;
    }
    int64_t out_pts = m_wallclock_base_pts + av_rescale_q(
      arrival_us - m_wallclock_base_us, AVRational{ 1, 1'000'000 }, m_in_time_base);
    if (m_last_out_pts != AV_NOPTS_VALUE && out_pts <= m_last_out_pts) {
      out_pts = m_last_out_pts + 1;
    }
    m_last_out_pts = out_pts;
    return out_pts;
  }

  const AVPacket* packet = media->packet.get();
  int64_t src_pts = packet->pts;
  if (src_pts == AV_NOPTS_VALUE) {
    src_pts = packet->dts;
  }

  int64_t out_pts;
  if (src_pts == AV_NOPTS_VALUE) {
    // No usable timestamp — extrapolate one frame forward instead of feeding
    // av_rescale_q(AV_NOPTS_VALUE) garbage to the encoder.
    out_pts = m_last_out_pts == AV_NOPTS_VALUE ? 0 : m_last_out_pts + m_frame_step;
  } else {
    out_pts = av_rescale_q(src_pts, media->time_base, m_in_time_base);
    // Encoders require strictly increasing pts; on equal/backward stamps (timestamp jitter,
    // duplicated frames) the frame is rejected and the viewer sees a stutter with nothing
    // in the log.
    if (m_last_out_pts != AV_NOPTS_VALUE && out_pts <= m_last_out_pts) {
      out_pts = m_last_out_pts + 1;
    }
  }

  m_last_out_pts = out_pts;
  return out_pts;
}

void sve_transcode_processor::process(const std::shared_ptr<media_packet>& media)
{
  if (!media || !media->packet || !m_session) {
    return;
  }

  const AVPacket* packet = media->packet.get();
  if (!packet->data || packet->size <= 0) {
    return;
  }

  note_input_timestamp(packet, media->time_base);
  // Queued for the packet this access unit eventually becomes, not handed to the decoder: see
  // m_pending_pts in the header for why stamping the input is wrong once B-frames are involved.
  // The cap bounds the queue if the encoder emits nothing at all -- the starvation guard below
  // is what reports that; this only keeps it from growing without limit meanwhile.
  m_pending_pts.push_back(next_output_pts(media));
  if (m_pending_pts.size() > kMaxPendingTimestamps) {
    m_pending_pts.pop_front();
  }

  const int64_t src_ts = packet->pts != AV_NOPTS_VALUE ? packet->pts : packet->dts;
  const int64_t submit_pts = src_ts == AV_NOPTS_VALUE
    ? AV_NOPTS_VALUE
    : av_rescale_q(src_ts, media->time_base, m_in_time_base);

  try {
    m_session->submit(
      std::span<const uint8_t>(packet->data, static_cast<std::size_t>(packet->size)),
      sve::Regions{},
      submit_pts);
  }
  catch (const std::exception& ex) {
    // One bad access unit must cost one frame, not the stream: compat_sink reinitializes on a
    // codec change, and the encoder is expected to survive a corrupt packet in between.
    //
    // Throttled, because a session that opened but cannot encode fails on every frame: at 25 fps
    // an untrottled line here is 25 a second for as long as the camera is viewed, which buries
    // the rest of the log rather than explaining anything the first line did not.
    if (m_submit_failures == 0 || m_submit_failures % kSubmitFailureLogInterval == 0) {
      logger::instance().warning(std::format(
        "sve_transcode_processor: submit failed (failure {} for this stream): {}",
        m_submit_failures + 1, ex.what()));
    }
    ++m_submit_failures;
  }

  // Said once, on the first frame out, because that is the first moment it is knowable:
  // libavcodec negotiates the decoder's pixel format on the first packet. The pair is what
  // matters -- a hardware encoder behind a software decoder is the case that looks like success
  // and costs several times the CPU.
  if (!m_path_reported && m_sink->emitted() > 0) {
    m_path_reported = true;
    const std::string decoder = m_session->decoder_hardware();
    logger::instance().info(std::format(
      "sve_transcode_processor: decoding on {}, encoding on '{}'",
      decoder.empty() ? "the CPU" : decoder, m_session->backend_name()));
  }

  // Two seconds of input at any sane frame rate. Silence past that is not startup latency.
  ++m_submitted;
  if (!m_starvation_reported && m_submitted >= 100 && m_sink->emitted() == 0) {
    m_starvation_reported = true;
    logger::instance().error(std::format(
      "sve_transcode_processor: {} access unit(s) submitted and nothing encoded — the source is "
      "most likely length-prefixed (AVCC/HVCC) rather than Annex-B, which the decoder's parser "
      "cannot read", m_submitted));
  }
}

void sve_transcode_processor::flush()
{
  if (!m_session) {
    return;
  }

  try {
    const sve::Report report = m_session->finish();
    logger::instance().info(std::format(
      "sve_transcode_processor: closed after {} frame(s), {} packet(s) on backend '{}'",
      report.frames, report.packets, report.backend_name));
  }
  catch (const std::exception& ex) {
    logger::instance().warning(std::format("sve_transcode_processor: finish failed: {}", ex.what()));
  }

  m_session.reset();
}

const avcodec_parameters_ptr& sve_transcode_processor::output_codec_parameters() const
{
  static const avcodec_parameters_ptr none;
  return m_sink ? m_sink->output_codec_parameters() : none;
}

AVRational sve_transcode_processor::output_time_base() const
{
  return m_out_time_base;
}

std::string sve_transcode_processor::encoder_name() const
{
  // The backend that actually opened, not the one asked for: under a chain they differ, and the
  // whole point of showing this is to see which one won.
  if (!m_session) {
    return {};
  }
  const std::string encoder = sve::backend_name(m_session->encoder_used());
  // Both halves of the path, because they fail apart and the failure is invisible: a hardware
  // encoder fed by a software decoder works perfectly and costs several times the CPU of one
  // whose frames never leave the device. Withheld until a frame has actually come out --
  // libavcodec settles the decoder's format on the first packet, so before that "cpu" would be
  // a guess reported as a fact.
  if (!m_sink || m_sink->emitted() == 0) {
    return encoder;
  }
  const std::string decoder = m_session->decoder_hardware();
  return std::format("{} ({} decode)", encoder, decoder.empty() ? "cpu" : decoder);
}

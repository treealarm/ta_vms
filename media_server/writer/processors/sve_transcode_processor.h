#pragma once

#include <cstdint>
#include <deque>
#include <memory>
#include <string>

#include "writer/processors/stream_processor.h"

namespace ta_roienc {
class Session;
}

// Live video transcode through the sve encoder library (roitrc/sve).
//
// It replaces video_transcode_processor on the WebRTC compatibility path, where every stream a
// camera publishes in something other than H.264 is decoded and re-encoded in real time -- one
// encode per viewed stream, continuously, which is the largest sustained CPU cost in the service.
//
// The reason to go through sve rather than call avcodec_find_encoder_by_name here is that
// libavcodec has no hardware auto-selection: it always hands back libx264 for AV_CODEC_ID_H264,
// and picking h264_vaapi means naming it, opening an AVHWDeviceContext, running a probe encode
// and falling back when any of that fails. sve already does exactly that, with a probe and an
// ordered fallback chain, and it is a library this repository owns.
//
// The software path is sve's own Backend::X264, not this class falling back to the old
// processor. A second fallback mechanism would be a second place for the fallback to be wrong.
class sve_transcode_processor : public stream_processor {
public:
  sve_transcode_processor();
  ~sve_transcode_processor() override;

  // Parses VMS_VIDEO_ENCODER and throws std::invalid_argument if it names no known backend.
  // Called from main() so a misspelled backend is a startup failure rather than something
  // discovered hours later, on the first camera that happens to publish HEVC.
  static void validate_configuration();

  bool init(const avcodec_parameters_ptr&, AVRational time_base, AVRational frame_rate, emit_fn) override;
  void process(const std::shared_ptr<media_packet>&) override;
  void flush() override;
  const avcodec_parameters_ptr& output_codec_parameters() const override;
  AVRational output_time_base() const override;
  std::string encoder_name() const override;

private:
  // ta_roienc::PacketHandler, defined in the .cpp so no sve header reaches this one.
  class packet_sink;

  void note_input_timestamp(const AVPacket* packet, AVRational time_base);
  int64_t next_output_pts(const std::shared_ptr<media_packet>& media);
  int64_t take_output_pts();

  emit_fn m_emit;
  std::unique_ptr<packet_sink> m_sink;
  std::unique_ptr<ta_roienc::Session> m_session;

  // PTS sanitizer state, carried over from video_transcode_processor.
  //
  // The sanitized timeline is generated per arriving access unit but applied to the *emitted*
  // packet, through m_pending_pts, and the indirection is load-bearing. sve decodes internally,
  // and a decoder reorders: with B-frames in the source -- which is precisely the HEVC this path
  // exists to re-encode -- frames come out in display order carrying the timestamp of the packet
  // they arrived in, which is decode order. Stamping the input and trusting it to come back
  // therefore produced a non-monotonic output timeline. (max_b_frames = 0 makes the *encoder*
  // emit in submission order; it says nothing about the decoder in front of it.) Handing the
  // k-th emitted packet the k-th sanitized value keeps the timeline monotonic and correctly
  // spaced whatever the reordering, and the original camera timestamps go to the decoder
  // untouched, which is what it wants for that reordering in the first place.
  AVRational m_in_time_base{};
  AVRational m_out_time_base{};
  int64_t m_last_out_pts = AV_NOPTS_VALUE;
  int64_t m_last_in_ts = AV_NOPTS_VALUE;
  int64_t m_frame_step = 1;
  // Wallclock re-timing anchor: output pts follows packet arrival time, because overloaded
  // cameras stamp frames at the nominal interval and periodically resync with multi-second pts
  // jumps that freeze pts-honoring players.
  int64_t m_wallclock_base_us = -1;
  int64_t m_wallclock_base_pts = 0;
  // Sanitized timestamps waiting for the packets they belong to. Single-threaded by
  // construction: sve calls the handler synchronously from inside submit() and finish().
  std::deque<int64_t> m_pending_pts;
  int64_t m_last_emitted_pts = AV_NOPTS_VALUE;

  // Submitted access units, and whether the "nothing came back" warning has been said once.
  // sve fits an Annex-B parser to H.264/H.265 input, so a camera that ever published
  // length-prefixed (AVCC/HVCC) access units would decode to nothing at all -- a black tile
  // with not one line in the log to say why. Counting is what turns that into a diagnosis.
  int64_t m_submitted = 0;
  bool m_starvation_reported = false;
  // Whether the decode/encode pair has been said once. Not known at open: the decoder's
  // format is negotiated on the first packet.
  bool m_path_reported = false;
  // Consecutive submit() failures, counted so the warning can be throttled: a session that opened
  // but cannot encode fails on every single frame.
  int64_t m_submit_failures = 0;
};

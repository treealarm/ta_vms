#pragma once

#include "writer/processors/stream_processor.h"

struct AVCodecContext;
struct AVFrame;
struct SwsContext;

class video_transcode_processor : public stream_processor {
public:
  explicit video_transcode_processor(AVCodecID codec_id = AV_CODEC_ID_H264);
  ~video_transcode_processor() override;

  bool init(const avcodec_parameters_ptr&, AVRational time_base, AVRational frame_rate, emit_fn) override;
  void process(const std::shared_ptr<media_packet>&) override;
  void flush() override;
  const avcodec_parameters_ptr& output_codec_parameters() const override;
  AVRational output_time_base() const override;

private:
  void encode_frame(AVFrame* frame);
  void drain_encoder_packets();
  void note_input_timestamp(const AVPacket* packet, AVRational time_base);
  int64_t next_output_pts(const AVFrame* decoded, AVRational src_time_base, int64_t arrival_us);

private:
  AVCodecID m_codec_id = AV_CODEC_ID_H264;
  emit_fn m_emit;
  AVCodecContext* m_decoder_ctx = nullptr;
  AVCodecContext* m_encoder_ctx = nullptr;
  SwsContext* m_sws_ctx = nullptr;
  AVFrame* m_scaled_frame = nullptr;
  avcodec_parameters_ptr m_out_codec_parameters;
  AVRational m_out_time_base{};
  // PTS sanitizer state: last emitted pts (encoder time_base), last input
  // timestamp (source time_base) for restart detection, and one frame's
  // duration in encoder time_base ticks.
  AVRational m_in_time_base{};
  int64_t m_last_out_pts = AV_NOPTS_VALUE;
  int64_t m_last_in_ts = AV_NOPTS_VALUE;
  int64_t m_frame_step = 1;
  // Wallclock re-timing anchor: output pts follows packet arrival time, because
  // overloaded cameras stamp frames at the nominal interval and periodically
  // resync with multi-second pts jumps that freeze pts-honoring players.
  int64_t m_wallclock_base_us = -1;
  int64_t m_wallclock_base_pts = 0;
};

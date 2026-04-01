#pragma once

#include <deque>
#include <memory>
#include <mutex>
#include <cstdint>

#include "interfaces/media_packet.h"
#include "writer/processors/stream_processor.h"

class sync_packet_transcoder
{
public:
  struct codec_config
  {
    AVCodecID video_codec = AV_CODEC_ID_H264;
    AVCodecID audio_codec = AV_CODEC_ID_OPUS;
    bool allow_hevc_video_passthrough = false;
  };

  explicit sync_packet_transcoder(codec_config config = {});
  ~sync_packet_transcoder();

  std::shared_ptr<media_packet> transcode_packet(const std::shared_ptr<media_packet>& media);
  void close();

private:
  struct processor_state
  {
    std::unique_ptr<stream_processor> processor;
    avcodec_parameters_ptr in_codec_parameters;
    AVRational in_time_base{};
    int32_t current_stream_index = -1;
    AVMediaType current_media_type = AVMEDIA_TYPE_UNKNOWN;
    AVRational current_frame_rate{ 0, 1 };
    int32_t current_sample_rate = 0;
  };

  std::unique_ptr<stream_processor> create_processor(const avcodec_parameters_ptr& codec_parameters) const;
  bool should_reinitialize(const processor_state& state, const std::shared_ptr<media_packet>& media) const;
  bool init_state(processor_state& state, const std::shared_ptr<media_packet>& media);
  processor_state* state_for_media_type(AVMediaType media_type);

private:
  codec_config m_config;
  mutable std::mutex m_mutex;
  processor_state m_video_state;
  processor_state m_audio_state;
  std::deque<std::shared_ptr<media_packet>> m_pending_packets;
};

#pragma once

#include <chrono>
#include <mutex>
#include <string>
#include <vector>

extern "C" {
#include <libavformat/avformat.h>
}

#include "interfaces/media_sink.h"

class rtsp_output_sink : public media_sink {
public:
  struct stream_config {
    AVMediaType media_type = AVMEDIA_TYPE_UNKNOWN;
    avcodec_parameters_ptr codec_parameters;
    AVRational time_base{};
  };

  explicit rtsp_output_sink(std::string rtsp_url);
  ~rtsp_output_sink() override;

  void on_packet(const std::shared_ptr<media_packet>& media) override;
  void close();
private:
  struct stream_state {
    AVMediaType media_type = AVMEDIA_TYPE_UNKNOWN;
    AVStream* out_stream = nullptr;
    AVRational input_time_base{};
    int64_t last_dts = AV_NOPTS_VALUE;
    int64_t last_pts = AV_NOPTS_VALUE;
    bool pending_timestamp_offset = false;
  };
  bool init(const std::vector<stream_config>& streams);
  void close_unlocked();
  stream_state* find_stream(AVMediaType media_type);
  bool update_stream_config_from_packet(const std::shared_ptr<media_packet>& media);
  std::vector<stream_config> collect_known_streams() const;

private:
  std::string m_rtsp_url;

  avformat_context_ptr m_ctx = nullptr;
  std::vector<stream_state> m_streams;
  bool m_is_initialized = false;

  /// Not before this. A failed publish used to be retried on the very next packet, which at 30 fps
  /// is thirty connection attempts a second — wasteful against a peer that is simply not up yet,
  /// and worse than wasteful when it is on another host, where each attempt blocks this sink's
  /// thread for a connect timeout. Three seconds is short enough that nobody waits for the stream
  /// and long enough that a peer coming up is met on its first or second try.
  static constexpr std::chrono::seconds k_reinit_backoff{ 3 };
  std::chrono::steady_clock::time_point m_next_init_attempt{};

  /// init() behind that backoff. Returns false while the wait is still on, so the caller treats it
  /// exactly as it treats a failed init: drop this packet and carry on.
  bool try_init(const std::vector<stream_config>& streams);
  bool m_wait_keyframe = true;
  int m_consecutive_write_errors = 0;
  stream_config m_video_config;
  stream_config m_audio_config;

  int64_t m_last_master_pts = AV_NOPTS_VALUE;
  AVRational m_master_time_base{ 1, 90000 }; // video is the master clock
  mutable std::mutex m_mutex;

  // DIAG: cadence of packets handed to the muxer, to spot delivery gaps/bursts
  // towards MediaMTX (e.g. interleave buffering starved by a sparse audio track).
  std::chrono::steady_clock::time_point m_last_video_write_at{};
  std::chrono::steady_clock::time_point m_last_audio_write_at{};
};

#pragma once

#include <atomic>
#include <chrono>
#include <filesystem>
#include <memory>
#include <string>
#include <thread>
#include <unordered_map>
#include <unordered_set>
#include <vector>

#include "interfaces/media_packet.h"
#include "interfaces/media_sink.h"
#include "interfaces/reader.h"
#include "sink_container/sink_container_impl.h"

extern "C" {
#include <libavformat/avformat.h>
}

// Loops a local file (or, for a directory, a sorted-by-filename playlist of
// files) as if it were a live camera feed — same media_sink pipeline as
// rtsp_reader (recording, WebRTC push, snapshots), just sourced from disk
// instead of a real camera. Used for testing/demoing without hardware.
class file_camera_reader final : public reader
{
public:
  file_camera_reader(const std::string& cam_id, const std::string& stream_name);
  ~file_camera_reader() override;

  bool open(const std::string& source) override;
  void add_sink(std::shared_ptr<media_sink> sink) override;
  void remove_sink(std::shared_ptr<media_sink> sink) override;

  void start() override;
  void stop() override;

  void pause() override
  {
  };
  void resume() override
  {
  };
  bool is_running() const override
  {
    return m_running;
  }
  void step(int32_t /*steps*/) override
  {
  };
  void set_speed(double /*speed*/) override
  {
  };
  AVStream* video_stream() const override;
  observed_video observed_video_params() const override;

  // True if `source` is not an RTSP/other-scheme URL and resolves to an
  // existing local file or directory — i.e. should be handled by this
  // reader instead of rtsp_reader.
  static bool is_file_source(const std::string& source);

private:
  static std::filesystem::path normalize_source_path(const std::string& source);
  bool refresh_playlist(std::string& error_detail);
  bool connect();
  void disconnect();
  void read_loop();
  void advance_playlist_index();
  std::string current_source_display() const;

  void note_unavailable(const std::string& error_class, const std::string& detail);
  void note_recovered();
  void reset_connect_backoff();
  void increase_connect_backoff(const std::string& error_class);

  bool validate_source_has_keyframe();
  bool restart_current_file();
  void begin_new_source_file();
  void reset_source_gate();
  bool pass_source_keyframe_gate(AVPacket* pkt, int stream_index);

  // Make per-stream PTS/DTS continuous across source files so the archive muxer
  // does not collapse each new file (which restarts at PTS 0) into a 1-tick burst.
  void remux_continuous_timestamps(AVPacket* pkt, int stream_index);
  void reset_continuity();

  std::shared_ptr<media_packet> build_media_packet(avpacket_ptr pkt);
  void apply_playback_pacing(
    const std::shared_ptr<media_packet>& media,
    bool& playback_clock_initialized,
    int64_t& playback_base_ts_us,
    std::chrono::steady_clock::time_point& playback_base_wall) const;

  struct stream_continuity_state {
    int64_t offset = 0;
    int64_t last_out_pts = AV_NOPTS_VALUE;
    int64_t last_out_dts = AV_NOPTS_VALUE;
    uint64_t generation = 0;
  };

  std::string m_source;
  avformat_context_ptr m_ctx = nullptr;
  int m_video_index = -1;
  std::unordered_set<int> m_forward_stream_indexes;
  std::unordered_map<int, avcodec_parameters_ptr> m_stream_codec_parameters;
  std::vector<std::filesystem::path> m_playlist;
  size_t m_playlist_index = 0;

  sink_container_impl m_sinks_helper;
  std::thread m_thread;
  std::atomic_bool m_running{ false };

  // Observed video parameters. Written only by the reader thread, read by the reporting timer —
  // same contract as rtsp_reader's. The file camera is how the system is exercised without
  // hardware, so the monitor's adaptive stream choice has to be testable through it too.
  std::atomic_int m_observed_width{ 0 };
  std::atomic_int m_observed_height{ 0 };
  std::atomic_int m_observed_codec_id{ AV_CODEC_ID_NONE };
  std::atomic<double> m_observed_fps{ 0.0 };
  std::atomic_int64_t m_observed_bitrate_bps{ 0 };
  std::atomic_int64_t m_observed_at_us{ 0 };

  /// Sliding window for the bitrate, reader thread only.
  int64_t m_bitrate_window_bytes = 0;
  int64_t m_bitrate_window_start_us = 0;

  /// Reader thread only; publishes through the atomics above.
  void note_observed_video(int stream_index, int packet_size);

  std::string m_cam_id;
  std::string m_stream_name;

  bool m_unavailable = false;
  std::chrono::steady_clock::time_point m_unavailable_since{};
  std::chrono::steady_clock::time_point m_next_unavailable_log{};
  std::string m_unavailable_error_class;
  uint64_t m_unavailable_suppressed_events = 0;
  std::chrono::milliseconds m_connect_backoff{ 1000 };
  std::string m_last_connect_error_class;

  bool m_wait_source_keyframe = true;

  std::unordered_map<int, stream_continuity_state> m_continuity;
  uint64_t m_file_generation = 0;
};

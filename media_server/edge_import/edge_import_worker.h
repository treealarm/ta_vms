#pragma once

#include <atomic>
#include <chrono>
#include <cstdint>
#include <filesystem>
#include <functional>
#include <memory>
#include <string>
#include <unordered_map>

extern "C" {
#include <libavformat/avformat.h>
}

#include "edge_import/replay_source.h"
#include "interfaces/media_packet.h"

/// One pull of a camera-side (edge / SD card) recording.
///
/// Clips are stamped from the requested wall clock plus the media offset, which is what makes an
/// imported range line up with the rest of the archive timeline instead of with the time the copy
/// ran. Packets are remuxed, never transcoded.
struct edge_import_settings
{
  std::string job_id;
  std::string camera_id;
  std::string stream_name;
  std::string replay_uri;
  std::string user;
  std::string password;
  std::chrono::system_clock::time_point start_ts{};
  std::chrono::system_clock::time_point end_ts{};
  /// Start of the recording the replay URI addresses; used to compute the RTSP seek.
  std::chrono::system_clock::time_point recording_start_ts{};
  /// Mux the whole window into one downloadable mp4 instead of writing archive clips.
  bool export_to_file = false;
  std::chrono::seconds segment_duration{ 60 };
  /// Length of one exported file; exports rotate for the same reason archive clips do.
  std::chrono::seconds export_chunk_duration{ 60 };
};

struct edge_import_progress
{
  std::chrono::system_clock::time_point position{};
  uint32_t clips_written = 0;
  uint64_t bytes_written = 0;
  /// Relative to VMS_EXPORT_BASE_PATH; empty unless export_to_file was requested.
  std::string export_path;
};

struct edge_import_outcome
{
  bool success = false;
  bool cancelled = false;
  /// The device refused another RTSP session (it is out of them). Retryable, unlike a real error.
  bool device_busy = false;
  std::string message;
  edge_import_progress progress;
};

class edge_import_worker
{
public:
  edge_import_worker(edge_import_settings settings,
                     const std::atomic_bool& cancel_flag,
                     std::function<void(const edge_import_progress&)> on_progress);
  ~edge_import_worker();

  edge_import_worker(const edge_import_worker&) = delete;
  edge_import_worker& operator=(const edge_import_worker&) = delete;

  /// Runs the whole import on the calling thread.
  [[nodiscard]] edge_import_outcome run();

private:
  struct output_stream_state
  {
    AVStream* stream = nullptr;
    int64_t last_pts = AV_NOPTS_VALUE;
    int64_t last_dts = AV_NOPTS_VALUE;
  };

  [[nodiscard]] bool resolve_target(std::string& error);
  [[nodiscard]] bool read_loop(std::string& error);

  [[nodiscard]] bool open_segment(std::chrono::system_clock::time_point ts, std::string& error);
  void close_segment();
  [[nodiscard]] bool write_packet(AVPacket* packet);
  [[nodiscard]] bool is_video_stream(int stream_index) const;

  edge_import_settings m_settings;
  const std::atomic_bool& m_cancel;
  std::function<void(const edge_import_progress&)> m_on_progress;

  std::unique_ptr<replay_source> m_source;
  avformat_context_ptr m_output = nullptr;
  std::unordered_map<int, output_stream_state> m_streams;

  std::chrono::milliseconds m_window{};
  bool m_window_covered = false;
  /// The device ended the stream cleanly — it simply had nothing more in the range.
  bool m_reached_eof = false;
  /// Shared origin for output timestamps, in the source stream's own time base per stream. Taken
  /// once for the whole import so audio and video keep their relative offsets (a per-stream zero
  /// would silently shift one against the other).
  std::chrono::microseconds m_pts_origin{ -1 };
  std::chrono::milliseconds m_segment_start_offset{ 0 };
  std::chrono::system_clock::time_point m_segment_start_ts{};
  std::chrono::system_clock::time_point m_segment_last_ts{};
  /// 1-based index of the exported file being written, so parts sort in playback order.
  uint32_t m_export_part = 0;
  std::filesystem::path m_segment_abs_path;
  std::filesystem::path m_segment_rel_path;
  std::string m_storage_id;
  std::filesystem::path m_target_root;

  edge_import_progress m_progress;
};

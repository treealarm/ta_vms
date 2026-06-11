#pragma once

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <filesystem>
#include <memory>
#include <mutex>
#include <queue>
#include <string>
#include <thread>
#include <unordered_map>

extern "C" {
#include <libavformat/avformat.h>
}

#include "interfaces/media_packet.h"
#include "interfaces/media_sink.h"
#include "writer/sync_packet_transcoder.h"

class segment_writer
  : public media_sink
{
public:
  segment_writer(
    const std::string& camera_id,
    const std::string& stream_name);

  ~segment_writer() override;

  // Called from the RTSP reader thread — enqueues and returns immediately.
  void on_packet(const std::shared_ptr<media_packet>& media) override;

  // Change the segment duration used for periodic rotation. Takes effect for the
  // current segment too (checked on the next packet). Does not interrupt writing.
  void set_segment_duration(std::chrono::seconds new_segment_duration);

  // Ask the writer thread to close the current segment (if any) and start a fresh one
  // on the next keyframe, optionally switching to a new segment duration.
  // Non-blocking — the close is processed asynchronously, in order with queued packets.
  void request_close_segment(std::chrono::seconds new_segment_duration);

  // Absolute path of the segment currently being written, or empty if none is open.
  // Thread-safe: callable from any thread (e.g. the debug entity-state publisher).
  std::filesystem::path current_segment_path() const;

private:
  struct input_stream_info
  {
    avcodec_parameters_ptr codec_parameters;
    AVRational time_base{};
    AVMediaType media_type = AVMEDIA_TYPE_UNKNOWN;
  };

  // Signal the writer thread to stop. Used only from the destructor, before join().
  void stop_async();

  // All methods below run exclusively on m_writer_thread.
  void process_queue();
  // A null entry is a "close segment" control marker (see request_close_segment).
  void process_batch(std::queue<std::shared_ptr<media_packet>>& batch);
  void process_packet(const std::shared_ptr<media_packet>& media);
  bool ensure_input_stream(const std::shared_ptr<media_packet>& media);
  void write_packet(const std::shared_ptr<media_packet>& media);
  void open_new_segment();
  void close_segment();
  void close();

  std::string m_camera_id;
  std::string m_stream_name;
  std::atomic<std::chrono::seconds> m_segment_duration;

  std::unordered_map<int, input_stream_info> m_input_streams;

  avformat_context_ptr m_ctx = nullptr;

  std::unordered_map<int, AVStream*> m_output_streams;

  std::chrono::steady_clock::time_point m_segment_start_time;
  std::chrono::system_clock::time_point m_segment_start_ts{};
  std::filesystem::path m_segment_abs_path;
  std::filesystem::path m_segment_rel_path;
  std::string m_segment_storage_id;

  // Snapshot of m_segment_abs_path, kept in sync for thread-safe reads via
  // current_segment_path() (m_segment_abs_path itself is writer-thread only).
  mutable std::mutex m_segment_path_mutex;
  std::filesystem::path m_current_segment_path;

  sync_packet_transcoder m_packet_transcoder;

  struct stream_timing_state {
    int64_t first_pts = AV_NOPTS_VALUE;
    int64_t first_dts = AV_NOPTS_VALUE;
    int64_t last_pts = AV_NOPTS_VALUE;
    int64_t last_dts = AV_NOPTS_VALUE;
  };

  std::unordered_map<int, stream_timing_state> m_timing;

  // Async packet queue — reader thread enqueues, m_writer_thread consumes.
  static constexpr std::size_t k_max_queue_depth = 500;
  std::queue<std::shared_ptr<media_packet>> m_queue;
  std::mutex m_queue_mutex;
  std::condition_variable m_queue_cv;
  std::atomic<bool> m_stopping{false};
  std::thread m_writer_thread;
};

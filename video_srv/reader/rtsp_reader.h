#pragma once

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <functional>
#include <unordered_set>
#include <memory>
#include <mutex>
#include <string>
#include <thread>

#include "interfaces/media_packet.h"
#include "interfaces/media_sink.h"
#include "interfaces/reader.h"

#include "reader/onvif_metadata_assembler.h"
#include "sink_container/sink_container_impl.h"

extern "C" {
#include <libavformat/avformat.h>
#include <libavutil/time.h>
}

class rtsp_reader final : public reader {
public:
  rtsp_reader(const std::string& cam_id, const std::string& profile_token, const std::string& user, const std::string& pass);
  ~rtsp_reader() override;

  bool open(const std::string& url) override;
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
  source_liveness liveness() const override;
  observed_video observed_video_params() const override;
  void set_source_lost_handler(source_lost_handler handler) override { m_on_source_lost = std::move(handler); }

  using metadata_handler = std::function<void(std::string&&, std::chrono::system_clock::time_point)>;
  /// Receives every ONVIF metadata document reassembled off the stream's metadata track, together
  /// with the wall clock of the first packet that carried it — the same stamping the video packets
  /// get, so a box can be placed on the frame it describes. Set once before start(), like
  /// set_source_lost_handler; without it the track is read and discarded.
  void set_metadata_handler(metadata_handler handler) { m_on_metadata = std::move(handler); }
  void step(int32_t /*steps*/) override {};
  void set_speed(double /*speed*/) override {};
  AVStream* video_stream() const override;

private:
  bool connect();
  void disconnect();
  void read_loop();
  /// Waits until `until`, or less once stop() has been called: the read loop's pauses would
  /// otherwise hold this camera's shutdown, and with it the process's, for a watchdog interval.
  void pause_until(std::chrono::steady_clock::time_point until);
  /// FFmpeg's interrupt callback: aborts a blocking open or read once the reader is stopping.
  static int interrupted(void* self);

  std::string m_rtsp_url;
  avformat_context_ptr m_ctx = nullptr;
  int m_video_index = -1;
  /// ONVIF metadata track, when the camera publishes one. Deliberately NOT part of
  /// m_forward_stream_indexes: that set feeds the sinks, and every sink (recording, WebRTC push,
  /// RTSP output, JPEG) branches on VIDEO/AUDIO only.
  int m_metadata_index = -1;
  std::unordered_set<int> m_forward_stream_indexes;

  sink_container_impl m_sinks_helper;

  std::thread m_thread;
  std::atomic_bool m_running{ false };
  /// Wakes pause_until when stop() clears m_running.
  std::mutex m_wake_mutex;
  std::condition_variable m_wake;

  // Read by the reporting timer from another thread, written only by the read loop — hence atomic.
  // Kept here rather than derived from m_ctx because those members are plain and reader-thread-only.
  std::atomic_bool m_source_alive{ false };
  // Microseconds since the epoch; 0 = no packet has ever arrived. A time_point is not trivially
  // copyable enough for std::atomic on every toolchain, so the count travels instead.
  std::atomic_int64_t m_last_frame_us{ 0 };
  // First packet after the source came back, which is where the outage actually ended. Not the same
  // as m_last_frame_us: that one keeps advancing, so by the time a heartbeat goes out it points at
  // whatever arrived last, and the recorded gap would stretch to the next report.
  std::atomic_int64_t m_recovered_frame_us{ 0 };

  // Observed video parameters, same contract as the liveness members above: written only by the
  // read loop, read by the reporting timer. video_stream() cannot be used for this from another
  // thread — the reader thread is the only one allowed to touch it.
  std::atomic_int m_observed_width{ 0 };
  std::atomic_int m_observed_height{ 0 };
  std::atomic_int m_observed_codec_id{ AV_CODEC_ID_NONE };
  std::atomic<double> m_observed_fps{ 0.0 };
  std::atomic_int64_t m_observed_bitrate_bps{ 0 };
  std::atomic_int64_t m_observed_at_us{ 0 };

  /// Sliding window for the bitrate, reader thread only. RTSP containers rarely declare a bitrate,
  /// so it is measured from the video packets that actually arrive.
  int64_t m_bitrate_window_bytes = 0;
  int64_t m_bitrate_window_start_us = 0;

  /// Reader thread only. Records what the video track currently looks like and refreshes the
  /// bitrate estimate; publishes through the atomics above.
  void note_observed_video(const AVPacket* pkt);

  /// False until the first packet of the current session has been stamped. Reader thread only.
  bool m_stamped_since_connect = false;

  /// Set once before start(), then only read by the reader thread — no synchronisation needed.
  source_lost_handler m_on_source_lost;
  metadata_handler m_on_metadata;
  /// Reader thread only. Created lazily on the first metadata packet of a session.
  std::unique_ptr<onvif_metadata_assembler> m_metadata_assembler;

  std::string m_cam_id;
  std::string m_profile_token;
  std::string m_user;
  std::string m_password;
};

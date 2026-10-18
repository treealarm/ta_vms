#pragma once

#include <string>
#include <deque>
#include <thread>
#include <atomic>
#include <condition_variable>
#include <mutex> 

#include "interfaces/media_sink.h"
#include "writer/compat_sink.h"
#include "writer/rtsp_output_sink.h"


class webrtc_output_sink : public media_sink {
public:
  webrtc_output_sink(std::string rtsp_url, bool transcode_hevc);
  ~webrtc_output_sink() override;
  void on_packet(const std::shared_ptr<media_packet>& media) override;
  /// Which encoder the video path is using, for the monitor's stream line. Empty when the camera
  /// already publishes H.264 and the packets are passed through without being re-compressed.
  std::string video_encoder_name() const { return m_video_compat_sink.video_encoder_name(); }
private:
  void close();
  void send_to_sinks(const std::shared_ptr<media_packet>& media);
  std::shared_ptr<rtsp_output_sink> m_rtsp_output_sink;
  compat_sink m_video_compat_sink;
  compat_sink m_audio_compat_sink;

  std::mutex m_queue_mutex;
  std::condition_variable m_cv;

  // ~25fps: m_drop_to_live_at is ~1.8s of backlog, m_max_queue is a ~3.6s hard cap.
  size_t m_max_queue = 90;
  size_t m_drop_to_live_at = 45;
  // Set when the queue overflowed mid-GOP; packets are discarded until the
  // next keyframe, which then triggers a clean drop-to-live.
  bool m_dropping_until_keyframe = false; // guarded by m_queue_mutex
  // Optional VMS_WEBRTC_PUBLISH_AUDIO=0 disables audio in the WebRTC publish.
  bool m_publish_audio = true;
  std::atomic_bool m_running{ true };

  std::thread m_worker;
  std::deque<std::shared_ptr<media_packet>> m_queue;
};

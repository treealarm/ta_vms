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
  explicit webrtc_output_sink(std::string rtsp_url);
  ~webrtc_output_sink() override;
  void on_packet(const std::shared_ptr<media_packet>& media) override;
private:
  void close();
  void send_to_sinks(const std::shared_ptr<media_packet>& media);
  std::shared_ptr<rtsp_output_sink> m_rtsp_output_sink;
  compat_sink m_video_compat_sink;
  compat_sink m_audio_compat_sink;

  std::mutex m_queue_mutex;
  std::condition_variable m_cv;

  size_t m_max_queue = 200; // ןמהבטנאועסÿ
  std::atomic_bool m_running{ true };

  std::thread m_worker;
  std::deque<std::shared_ptr<media_packet>> m_queue;
};

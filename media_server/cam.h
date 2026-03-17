#pragma once

#include <memory>
#include <mutex>
#include <string>
#include <vector>

#include "media/media_sink.h"
#include "reader/reader.h"
#include "stream/jpeg_streamer.h"
#include "writer/segment_writer.h"
#include "writer/webrtc_output_sink.h"

class cam final : public media_sink {
public:
  cam(const std::string& source,
      const std::string& cam_id,
      const std::string& storage_base_path = "");

  ~cam() override;

  void start_recording();
  void stop_recording();

  bool start_web_rtc(std::string& error);
  bool stop_web_rtc(std::string& error);

  void shutdown();

  bool start_jpeg_stream(std::string& error);
  bool stop_jpeg_stream(std::string& error);
  bool wait_next_jpeg_frame(std::vector<uint8_t>& frame,
                            uint64_t& frame_number,
                            std::string& error);

  void on_packet(const std::shared_ptr<media_packet>& pkt) override;

private:
  void update_reader_subscription();

  std::string m_cam_id;
  std::string m_source;

  std::unique_ptr<reader> m_reader;
  segment_writer m_writer;
  std::unique_ptr<webrtc_output_sink> m_webrtc_output_sink;
  std::unique_ptr<jpeg_streamer> m_jpeg_streamer;

  mutable std::mutex m_sinks_mutex;
  bool m_is_running = false;
  bool m_is_recording = false;
  bool m_is_web_rtc_running = false;
  bool m_is_attached_to_reader = false;
};

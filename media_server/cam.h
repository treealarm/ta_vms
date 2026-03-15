#pragma once

#include <memory>
#include <mutex>
#include <string>
#include <vector>

#include "media/media_sink.h"
#include "reader/reader.h"
#include "stream/jpeg_streamer.h"
#include "writer/ffmpeg_compat_sink.h"
#include "writer/rtsp_output_sink.h"

class cam final : public media_sink {
public:
  cam(const std::string& source,
      const std::string& cam_id,
      const std::string& storage_base_path = "");

  ~cam() override;

  void start_recording();
  void stop_recording();
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
  rtsp_output_sink m_rtsp_output_sink;
  ffmpeg_compat_sink m_ffmpeg_compat_sink;
  std::unique_ptr<jpeg_streamer> m_jpeg_streamer;

  mutable std::mutex m_sinks_mutex;
  bool m_is_running = false;
  bool m_is_recording = false;
  bool m_is_writer_initialized = false;
  bool m_is_attached_to_reader = false;
};

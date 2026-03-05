#pragma once
#include <memory>
#include <string>
#include "reader/rtsp_reader.h"
#include "stream/jpeg_streamer.h"
#include "writer/segment_writer.h"

class cam {
public:
  cam(const std::string& rtsp_url,
      const std::string& cam_id,
      const std::string& storage_base_path = "");
  ~cam();

  void start_recording();
  void stop_recording();
  void shutdown();

  bool start_jpeg_stream(std::string& error);
  bool stop_jpeg_stream(std::string& error);
  bool wait_next_jpeg_frame(std::vector<uint8_t>& frame, uint64_t& frame_number, std::string& error);

private:
  std::string m_cam_id;
  std::string m_rtsp_url;

  rtsp_reader m_reader;
  segment_writer m_writer;
  std::unique_ptr<jpeg_streamer> m_jpeg_streamer;
  bool m_is_running = false;
  bool m_is_recording = false;
};

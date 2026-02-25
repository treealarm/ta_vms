#pragma once
#include <string>
#include <memory>
#include <cstdint>
#include "reader/rtsp_reader.h"
#include "writer/segment_writer.h"
#include "stream/jpeg_streamer.h"

class cam {
public:
  cam(const std::string& rtsp_url, const std::string& cam_id);
  ~cam();

  void start();
  void stop();

  bool start_jpeg_stream(const std::string& host, uint16_t port, std::string& error);
  bool stop_jpeg_stream(std::string& error);

private:
  std::string m_cam_id;
  std::string m_rtsp_url;

  rtsp_reader m_reader;
  segment_writer m_writer;
  std::unique_ptr<jpeg_streamer> m_jpeg_streamer;
  bool m_is_running = false;
};
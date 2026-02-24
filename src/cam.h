#pragma once
#include <string>
#include "reader/rtsp_reader.h"
#include "writer/segment_writer.h"

class cam {
public:
  cam(const std::string& rtsp_url, const std::string& cam_id);
  ~cam();

  void start();
  void stop();

private:
  std::string m_cam_id;
  std::string m_rtsp_url;

  rtsp_reader m_reader;
  segment_writer m_writer;
};
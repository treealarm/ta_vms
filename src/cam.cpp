#include "cam.h"
#include <stdexcept>

cam::cam(const std::string& rtsp_url, const std::string& cam_id)
  : m_rtsp_url(rtsp_url), m_cam_id(cam_id),
  m_writer("records", cam_id, 60) // сегменты по 10 секунд
{
  if (!m_reader.open(m_rtsp_url))
    throw std::runtime_error("Failed to open RTSP");

  m_writer.init(m_reader.video_stream());
  m_reader.add_sink(&m_writer);
}

cam::~cam() {
  stop();
}

void cam::start() {
  m_reader.start();
}

void cam::stop() {
  m_reader.stop();
  m_writer.close();
}
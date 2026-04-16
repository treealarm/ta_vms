#pragma once

#include <memory>
#include <mutex>
#include <string>
#include <vector>

#include "interfaces/media_sink.h"
#include "interfaces/reader.h"
#include "stream/jpeg_streamer.h"
#include "arch_writer/segment_writer.h"
#include "writer/webrtc_output_sink.h"

class cam final
{
public:
  cam(const std::string& source,
      const std::string& cam_id,
    const std::string& user,
    const std::string& pass);

  virtual ~cam();

  void start_recording();
  void stop_recording();

  bool start_web_rtc(std::string& error);
  bool stop_web_rtc(std::string& error);

  void shutdown();
  std::string get_id() { return m_cam_id; }
  std::string get_source() { return m_source; }
  std::string get_user() { return m_user; }
  std::string get_pass() { return m_pass; }
  bool is_recording() { return m_writer != nullptr; }
  bool is_web_rtc_streaming() { return m_webrtc_output_sink != nullptr; }
private:

  std::string m_source;
  std::string m_cam_id;

  std::string m_user;
  std::string m_pass;

  std::unique_ptr<reader> m_reader;
  std::shared_ptr<segment_writer> m_writer;
  std::shared_ptr<webrtc_output_sink> m_webrtc_output_sink;
  mutable std::mutex m_sinks_mutex;
};

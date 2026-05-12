#pragma once

#include <map>
#include <memory>
#include <string>

#include "cam_stream.h"
#include "recording_types.h"

class cam_device
{
public:
  explicit cam_device(const std::string& camera_id);
  ~cam_device();

  bool add_stream(
    const std::string& stream_name,
    const std::string& profile_token,
    const std::string& rtsp_url,
    const std::string& user,
    const std::string& pass,
    const recording_policy_spec& policy,
    bool web_rtc,
    std::string& error);

  bool remove_stream(const std::string& stream_name, std::string& error);

  void shutdown();

  const std::string& camera_id() const { return m_camera_id; }
  bool empty() const { return m_streams.empty(); }

  cam_stream*       get_stream(const std::string& stream_name);
  const cam_stream* get_stream(const std::string& stream_name) const;
  cam_stream*       first_stream();
  const cam_stream* first_stream() const;

  const std::map<std::string, std::shared_ptr<cam_stream>>& streams() const { return m_streams; }

private:
  std::string m_camera_id;
  std::map<std::string, std::shared_ptr<cam_stream>> m_streams;
};

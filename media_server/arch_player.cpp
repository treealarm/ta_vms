#include "arch_player.h"

#include <cstdlib>
#include <memory>
#include <stdexcept>
#include <filesystem>

#include "file_system/recording_storage.h"

#include "reader/file_reader.h"
#include "interfaces/storage_utils.h"
#include "interfaces/web_rtc_utils.h"


arch_player::arch_player(
  const std::string& client_id,
  std::string& error)
    : 
  m_client_id(client_id)
{

}

arch_player::~arch_player() 
{
  shutdown();
}

std::optional<std::string> arch_player::find_file(
  const std::string& camera_id,
  const std::filesystem::path& base_path,
  const std::chrono::system_clock::time_point& start_time)
{
  recording_storage storage(base_path);
  auto rec = storage.find_nearest(camera_id, start_time);

  if (!rec)
    return std::nullopt;

  return rec->file_path.string();
}

bool arch_player::open(const std::chrono::system_clock::time_point& start_time,
                           std::string& error) 
{
  auto base_path = resolve_storage_base_path();

  auto playback_reader = std::make_unique<file_reader>(
      m_camera_id,
      base_path);

  const auto file_name = find_file(m_camera_id, base_path, start_time);

  if (!file_name)
  {
    error = "Unable to find file";
    return false;
  }

  if (!playback_reader->open(*file_name))
  {
    error = "Failed to open media source";
    return false;
  }

  playback_reader->start();

  m_reader = std::move(playback_reader);
  m_start_time = start_time;

  return true;
}

bool arch_player::set_state(
  const std::chrono::system_clock::time_point& start_time,
  const std::string& camera_id,
  std::string& error) 
{
  m_camera_id = camera_id;
  shutdown();
  if (!open(start_time, error))
  {
    return false;
  }

  if (!m_reader) 
  {
    error = "player is not initialized";
    return false;
  }

  if (!start_web_rtc(error))
  {
    return false;
  }
  return true;
}

const std::string& arch_player::camera_id() const
{
  return m_camera_id;
}

void arch_player::shutdown() 
{
  if (m_reader) 
  {
    m_reader->stop();
  }

  m_reader.reset();
}

bool arch_player::start_web_rtc(std::string& error)
{
  std::shared_ptr<webrtc_output_sink>  sink;
  {
    std::scoped_lock lock(m_sinks_mutex);
    sink = m_webrtc_output_sink;
    if (!sink)
    {      
      m_webrtc_output_sink =
        std::make_shared<webrtc_output_sink>(resolve_default_webrtc_rtsp_url(m_client_id));
    }
    else
    {
      error = "web rtc stream already running";
    }
    sink = m_webrtc_output_sink;
  }
  m_reader->add_sink(sink);
  
  return true;
}

bool arch_player::stop_web_rtc(std::string& error)
{
  std::shared_ptr<webrtc_output_sink>  sink;
  {
    std::scoped_lock lock(m_sinks_mutex);

    if (!m_webrtc_output_sink)
    {
      error = "web rtc stream is not running";
      return false;
    }

    if (m_webrtc_output_sink)
    {
      sink = m_webrtc_output_sink;
      m_webrtc_output_sink.reset();
    }
  }
  if (sink)
  {
    m_reader->remove_sink(sink);
  }
  return true;
}
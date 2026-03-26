#include "arch_player.h"

#include <cstdlib>
#include <memory>
#include <stdexcept>
#include <filesystem>



#include "reader/file_reader.h"

#include "interfaces/web_rtc_utils.h"


arch_player::arch_player(
  const std::string& client_id,
  const std::string& camera_id,
  std::string& error)
    : 
  m_client_id(client_id),
  m_camera_id(camera_id)
{

}

arch_player::~arch_player() 
{
  shutdown();
}

bool arch_player::on_eof(const std::string& file)
{
  auto path = std::filesystem::path(file);
  auto ts = m_storage.parse_time(path.filename().string());

  if (!ts)
  {
    return false;
  }
  auto my_utc = recording_storage::timepoint_to_utc_string(*ts);
  auto info = m_storage.find_next(camera_id(), *ts);

  if (!info)
  {
    return false;
  }
  std::string error;

  bool retval = open_file_reader(info->file_path.string(), error);
  if (retval)
  {
    m_start_time = info->start_time;
  }
  return retval;
}

bool arch_player::open(const std::chrono::system_clock::time_point& start_time,
                           std::string& error) 
{
  auto rec = m_storage.find_nearest(camera_id(), start_time);

  if (!rec)
  {
    error = "Unable to find file";
    return false;
  }

  bool retval =  open_file_reader(rec->file_path.string(), error);  
  if (retval)
  {
    m_start_time = start_time;
  }
  return retval;
}

bool arch_player::open_file_reader(const std::string& file_path,
  std::string& error)
{
  m_cur_file_path = file_path;


  auto playback_reader = std::make_shared<file_reader>(
    m_camera_id,
    [this](const std::string& file) ->bool
    {
      return this->on_eof(file);
    });

  if (!playback_reader->open(*m_cur_file_path))
  {
    error = "Failed to open media source";
    return false;
  }

  playback_reader->start();

  m_reader = std::move(playback_reader);  

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
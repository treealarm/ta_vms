#include "camera_manager.h"

bool camera_manager::add_camera(
  const std::string& camera_id,
  const std::string& rtsp_url,
  const std::string& user,
  const std::string& pass,
  bool recording,
  bool web_rtc,
  std::string& error) 
{
  std::scoped_lock lock(m_mutex);

  if (camera_id.empty()) {
    error = "camera_id must not be empty";
    return false;
  }

  auto existing = m_cameras.find(camera_id);
  if (existing != m_cameras.end()) {
    existing->second->shutdown();
    m_cameras.erase(existing);
  }

  try {
    auto camera = std::make_shared<cam>(rtsp_url, camera_id, user, pass);
    if (recording) {
      camera->start_recording();
    }


    if (web_rtc) {
      if (!camera->start_web_rtc(error)) {
        return false;
      }
    }


    m_cameras.emplace(camera_id, std::move(camera));
    return true;
  } catch (const std::exception& ex) {
    error = ex.what();
    return false;
  }
}

bool camera_manager::start_recording(const std::string& camera_id,
                                     std::string& error) {
  std::scoped_lock lock(m_mutex);

  auto it = m_cameras.find(camera_id);
  if (it == m_cameras.end()) {
    error = "camera not found: " + camera_id;
    return false;
  }

  if (it->second->is_recording()) {
    error = "camera already recording: " + camera_id;
    return false;
  }

  it->second->start_recording();
  return true;
}

bool camera_manager::stop_recording(const std::string& camera_id,
                                    std::string& error) {
  std::scoped_lock lock(m_mutex);

  auto it = m_cameras.find(camera_id);
  if (it == m_cameras.end()) {
    error = "camera not found: " + camera_id;
    return false;
  }

  if (!it->second->is_recording()) {
    error = "camera is not recording: " + camera_id;
    return false;
  }

  it->second->stop_recording();

  return true;
}

std::optional<camera_manager::camera_info>  camera_manager::get_camera_state(
  const std::string& camera_id,
  std::string& error) 
{
  std::scoped_lock lock(m_mutex);

  auto it = m_cameras.find(camera_id);
  if (it == m_cameras.end()) 
  {
    error = "camera not found: " + camera_id;
    return std::nullopt;
  }

  camera_manager::camera_info info
  {
    .camera_id = camera_id,
    .rtsp_url = it->second->get_source(),
    .user = it->second->get_user(),
    .pass = it->second->get_pass(),
    .is_recording = it->second->is_recording(),
    .is_web_rtc_streaming = it->second->is_web_rtc_streaming(),
  };

  return info;
}

bool camera_manager::set_camera_state(
  const std::string& camera_id,
  const std::string& rtsp_url,
  const std::string& user,
  const std::string& pass,
  bool recording,
  bool web_rtc,
  std::string& error)
{
  bool recreate = false;
  do
  {
    std::scoped_lock lock(m_mutex);

    auto it = m_cameras.find(camera_id);
    if (it == m_cameras.end())
    {
      error = "camera not found: " + camera_id;
      return false;
    }

    if (it->second->get_source()!= rtsp_url || 
      it->second->get_user() != user ||
      it->second->get_pass() != pass)
    {
      recreate = true;
      break;
    }

    if (recording != it->second->is_recording())
    {
      if (recording)
      {
        it->second->start_recording();
      }
      else
      {
        it->second->stop_recording();
      }
    }


    if (web_rtc != it->second->is_web_rtc_streaming())
    {
      if (web_rtc)
      {
        if (!it->second->start_web_rtc(error))
        {
          return false;
        }
      }
      else
      {
        if (!it->second->stop_web_rtc(error))
        {
          return false;
        }
      }
    }
  } while (false);
 
  if (recreate)
  {
    return add_camera(camera_id, rtsp_url, user, pass, recording, web_rtc, error);
  }
  return true;
}


bool camera_manager::remove_camera(const std::string& camera_id,
                                 std::string& error) {
  std::scoped_lock lock(m_mutex);

  auto it = m_cameras.find(camera_id);
  if (it == m_cameras.end()) {
    error = "camera not found: " + camera_id;
    return false;
  }

  it->second->shutdown();
  m_cameras.erase(it);
  return true;
}

bool camera_manager::remove_arch_player(const std::string& client_id,
                                      std::string& error) {
  std::scoped_lock lock(m_mutex);

  if (client_id.empty()) {
    error = "client_id must not be empty";
    return false;
  }

  auto it = m_arch_players_by_client.find(client_id);
  if (it == m_arch_players_by_client.end()) {
    error = "archive player not found for client: " + client_id;
    return false;
  }

  it->second->shutdown();
  m_arch_players_by_client.erase(it);
  return true;
}

std::vector<camera_manager::camera_info> camera_manager::list_cameras() const 
{
  std::scoped_lock lock(m_mutex);

  std::vector<camera_info> cameras;

  for (const auto& [camera_id, entry] : m_cameras) 
  {
    cameras.push_back(
      camera_info{
        camera_id, 
        entry->get_source(),
        entry->get_user(),
        entry->get_pass(),
        entry->is_recording(),
        entry->is_web_rtc_streaming()
      });
  }

  return cameras;
}

std::vector<camera_manager::player_info> camera_manager::list_players() const
{
  std::scoped_lock lock(m_mutex);

  std::vector<player_info> cameras;

  for (const auto& [client_id, entry] : m_arch_players_by_client)
  {
    cameras.push_back(player_info{
        entry->camera_id(),
        client_id,
        entry->cur_time(),
        entry->file_path() ? std::make_optional(*entry->file_path()) : std::nullopt
      });
  }

  return cameras;
}
void camera_manager::stop_all() 
{
  std::scoped_lock lock(m_mutex);

  for (auto& [_, entry] : m_cameras) {
    entry->shutdown();
  }

  for (auto& [_, player] : m_arch_players_by_client) 
  {
    player->shutdown();
  }
}

bool camera_manager::add_arch_player(
  const std::string& client_id,
  const std::string& camera_id,
  std::string& error) 
{


  if (client_id.empty()) {
    error = "client_id must not be empty";
    return false;
  }


  try {
    std::scoped_lock lock(m_mutex);

    auto existing = m_arch_players_by_client.find(client_id);
    if (existing != m_arch_players_by_client.end()) {
      existing->second->shutdown();
      m_arch_players_by_client.erase(existing);
    }

    std::shared_ptr<arch_player> player = std::make_shared<arch_player>(
      client_id,
      camera_id
      );
    m_arch_players_by_client.emplace(client_id, std::move(player));

    return true;
  } catch (const std::exception& ex) {
    error = ex.what();
    return false;
  }
}

bool camera_manager::set_player_state(
  const std::string& client_id,
  const std::string& camera_id,
  const std::chrono::system_clock::time_point& start_time,
  std::optional<std::chrono::milliseconds> position,
  std::string& error) 
{
  std::scoped_lock lock(m_mutex);

  if (client_id.empty()) {
    error = "client_id must not be empty";
    return false;
  }

  auto it = m_arch_players_by_client.find(client_id);
  if (it == m_arch_players_by_client.end()) {
    error = "archive player not found for client: " + client_id;
    return false;
  }

  if (!camera_id.empty() && camera_id != it->second->camera_id()) {
    error = "camera_id does not match archive player camera";
    return false;
  }

  return it->second->set_state(start_time, camera_id, position);
}

bool camera_manager::set_player_command(const std::string& client_id,
  const player_command& cmd,
  std::string& error)
{
  std::scoped_lock lock(m_mutex);

  if (client_id.empty()) {
    error = "client_id must not be empty";
    return false;
  }
  auto it = m_arch_players_by_client.find(client_id);
  if (it == m_arch_players_by_client.end()) {
    error = "archive player not found for client: " + client_id;
    return false;
  }

  return it->second->set_player_command(cmd);
}

std::optional<camera_manager::player_info>  camera_manager::get_player_state(
  const std::string& client_id,
  std::string& error)
{
  std::scoped_lock lock(m_mutex);

  if (client_id.empty()) {
    error = "client_id must not be empty";
    return std::nullopt;
  }

  auto it = m_arch_players_by_client.find(client_id);
  if (it == m_arch_players_by_client.end()) {
    error = "archive player not found for client: " + client_id;
    return std::nullopt;
  }

  camera_manager::player_info pi
  {
    it->second->camera_id(),
    client_id,
    it->second->cur_time(),
    it->second->file_path() ? std::make_optional(*it->second->file_path()) : std::nullopt
  };
  pi.start_ts = it->second->get_start_time();
  pi.progress = it->second->get_progress();
  pi.mode = it->second->get_mode();
  return pi;
}
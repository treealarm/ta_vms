#include "arch_player.h"

#include <cstdlib>
#include <memory>
#include <stdexcept>

#include "reader/file_reader.h"
#include "interfaces/storage_utils.h"


arch_player::arch_player(const std::string& camera_id,
                         const std::string& start_time,
                         const std::string& storage_base_path,
                         std::string& error)
    : m_camera_id(camera_id),
      m_storage_base_path(storage_base_path) {
  if (!recreate(start_time, error)) {
    throw std::runtime_error(error);
  }
}

arch_player::~arch_player() {
  shutdown();
}

bool arch_player::recreate(const std::string& start_time,
                           std::string& error) {
  const auto playback_config = file_playback_config{start_time, m_storage_base_path};
  auto playback_reader = std::make_unique<file_reader>(
      m_camera_id,
      resolve_storage_base_path(playback_config.storage_base_path));

  if (!playback_reader->open(playback_config.start_time)) {
    error = "Failed to open media source";
    return false;
  }

  playback_reader->start();

  m_reader = std::move(playback_reader);
  m_start_time = start_time;

  return true;
}

bool arch_player::set_state(const std::string& start_time,
                            std::string& error) {
  const bool has_new_start_time = !start_time.empty() && start_time != m_start_time;
  if (has_new_start_time) {
    shutdown();
    return recreate(start_time, error);
  }

  if (!m_reader) {
    error = "player is not initialized";
    return false;
  }

  return true;
}

const std::string& arch_player::camera_id() const {
  return m_camera_id;
}

void arch_player::shutdown() {
  if (!m_reader) {
    return;
  }


  if (m_reader) {
    m_reader->stop();
  }

  m_reader.reset();
}


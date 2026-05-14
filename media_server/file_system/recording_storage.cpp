#include "file_system/recording_storage.h"

#include <cstdlib>
#include <stdexcept>

using namespace std::chrono;

std::filesystem::path recording_storage::make_segment_path(
  const std::string& camera_id,
  const std::string& stream_name,
  std::chrono::system_clock::time_point ts)
{
  auto ts_sec = floor<seconds>(ts);

  auto base = stream_name.empty()
    ? std::filesystem::path(camera_id)
    : std::filesystem::path(camera_id) / stream_name;

  return base
    / std::format("{:%Y/%m/%d/%H}", ts_sec)
    / std::format("{:%Y%m%dT%H%M%S}{}", ts_sec, file_ext().string());
}

std::filesystem::path recording_storage::resolve_storage_base_path()
{
  const char* value = std::getenv("VMS_STORAGE_BASE_PATH");
  if (!value || value[0] == '\0')
    throw std::runtime_error("Required environment variable 'VMS_STORAGE_BASE_PATH' is not set");
  return value;
}

std::filesystem::path recording_storage::file_ext()
{
  if (const char* value = std::getenv("VMS_FILE_EXT")) {
    if (value[0] != '\0') {
      return value;
    }
  }

  return k_ts_ext;
}


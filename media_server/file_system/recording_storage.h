#pragma once

#include <string>
#include <filesystem>
#include <chrono>

class recording_storage
{
public:
  static constexpr std::string_view k_ext = ".mp4";

  std::filesystem::path make_segment_path(
    const std::string& camera_id,
    const std::string& stream_name,
    std::chrono::system_clock::time_point ts);

  std::filesystem::path file_ext();

  // Returns the value of VMS_STORAGE_BASE_PATH. Terminates the process if the variable is not set.
  static std::filesystem::path resolve_storage_base_path();
};

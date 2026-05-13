#pragma once

#include <string>
#include <filesystem>
#include <chrono>

class recording_storage
{
public:
  static constexpr std::string_view k_ts_ext = ".ts";
  static constexpr std::string_view k_mkv_ext = ".mkv";

  std::filesystem::path make_segment_path(
    const std::string& camera_id,
    const std::string& stream_name,
    std::chrono::system_clock::time_point ts);

  std::filesystem::path file_ext();

  static std::filesystem::path resolve_storage_base_path();
};

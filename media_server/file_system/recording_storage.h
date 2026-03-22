#pragma once

#include <string>
#include <vector>
#include <optional>
#include <filesystem>
#include <chrono>

struct recording_info
{
  std::filesystem::path file_path;
  std::chrono::system_clock::time_point start_time;
  std::optional<std::chrono::system_clock::time_point> end_time;
};

class recording_storage
{
public:
  explicit recording_storage(std::filesystem::path base_path);

  std::vector<recording_info> get_recordings(
    const std::string& camera_id,
    std::chrono::system_clock::time_point ts,
    uint32_t limit);

  std::optional<recording_info> find_nearest(
    const std::string& camera_id,
    std::chrono::system_clock::time_point ts);
  std::filesystem::path make_segment_path(
    const std::string& camera_id,
    std::chrono::system_clock::time_point ts);
private:
  std::filesystem::path m_base_path;

  std::vector<recording_info> load_day(
    const std::filesystem::path& day_path);

  std::optional<std::chrono::system_clock::time_point> parse_time(
    const std::string& filename);

  std::filesystem::path make_day_path(
    const std::string& camera_id,
    std::chrono::system_clock::time_point ts);

  std::filesystem::path shift_day(
    const std::filesystem::path& current_day,
    int offset_days);
};
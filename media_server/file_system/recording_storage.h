#pragma once

#include <string>
#include <vector>
#include <optional>
#include <filesystem>
#include <chrono>
#include <cstdlib>
#include <string>

struct recording_info
{
  std::filesystem::path file_path;
  std::chrono::system_clock::time_point start_time;
};

class recording_storage
{
public:
  explicit recording_storage();

  std::vector<recording_info> get_recordings(
    const std::string& camera_id,
    std::chrono::system_clock::time_point ts,
    uint32_t limit,
    int32_t& nearestIndex);

  std::optional<recording_info> find_nearest(
    const std::string& camera_id,
    std::chrono::system_clock::time_point ts);

  std::optional<recording_info> find_offset(
    const std::string& camera_id,
    std::chrono::system_clock::time_point ts,
    int offset);

  std::filesystem::path make_segment_path(
    const std::string& camera_id,
    std::chrono::system_clock::time_point ts);

  std::filesystem::path get_absolute_path(const std::string& camera_id,
    std::chrono::system_clock::time_point ts);

  std::optional<std::chrono::system_clock::time_point> parse_time(
    const std::string& filename);
  static inline std::string timepoint_to_utc_string(std::chrono::system_clock::time_point tp)
  {
    using namespace std::chrono;

    // создаём zoned_time в UTC
    zoned_time utc{ "UTC", tp };

    // форматируем в строку
    return format("{:%Y-%m-%d %H:%M:%S}", utc);
  }
private:
  std::filesystem::path resolve_storage_base_path();
  std::filesystem::path resolve_file_ext();
  std::vector<recording_info> load_day(
    const std::filesystem::path& day_path);

  std::filesystem::path make_day_path(
    const std::string& camera_id,
    std::chrono::system_clock::time_point ts);

  std::filesystem::path shift_day(
    const std::filesystem::path& current_day,
    int offset_days);

  std::filesystem::path m_base_path;
};
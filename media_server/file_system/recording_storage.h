#pragma once

#include <string>
#include <vector>
#include <array>
#include <optional>
#include <filesystem>
#include <chrono>
#include <cstdlib>

struct recording_info
{
  std::filesystem::path file_path;
  std::chrono::system_clock::time_point start_time;
};

class recording_storage
{
public:
  explicit recording_storage();
  static constexpr std::string_view k_mkv_ext = ".mkv";
  static constexpr std::string_view k_ts_ext = ".ts";
  static constexpr std::array<std::string_view, 2> k_allowed_exts = {
    k_mkv_ext,
    k_ts_ext
  };
  static std::chrono::seconds get_segment_duration()
  {
    return std::chrono::seconds(60);
  };

  std::vector<recording_info> get_recordings(
    const std::string& camera_id,
    const std::string& stream_name,
    std::chrono::system_clock::time_point ts,
    uint32_t limit,
    int32_t& nearestIndex);

  std::optional<recording_info> find_nearest(
    const std::string& camera_id,
    const std::string& stream_name,
    std::chrono::system_clock::time_point ts);

  std::optional<recording_info> find_offset(
    const std::string& camera_id,
    const std::string& stream_name,
    std::chrono::system_clock::time_point ts,
    int offset);

  std::filesystem::path make_segment_path(
    const std::string& camera_id,
    const std::string& stream_name,
    std::chrono::system_clock::time_point ts);

  std::filesystem::path get_absolute_path(
    const std::string& camera_id,
    const std::string& stream_name,
    std::chrono::system_clock::time_point ts);

  std::filesystem::path file_ext();

  std::optional<std::chrono::system_clock::time_point> parse_time(
    const std::string& filename);
  static std::string timepoint_to_utc_string(std::chrono::system_clock::time_point tp);
  static std::filesystem::path resolve_storage_base_path();

private:
  std::vector<recording_info> load_day(
    const std::filesystem::path& day_path);

  std::filesystem::path make_day_path(
    const std::string& camera_id,
    const std::string& stream_name,
    std::chrono::system_clock::time_point ts);

  std::filesystem::path shift_day(
    const std::filesystem::path& current_day,
    int offset_days);

  std::filesystem::path m_base_path;
};

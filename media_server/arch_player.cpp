#include "arch_player.h"

#include <cstdlib>
#include <memory>
#include <stdexcept>
#include <filesystem>

#include "reader/file_reader.h"
#include "interfaces/storage_utils.h"
#include "interfaces/web_rtc_utils.h"

namespace
{
std::vector<std::string> split(const std::string& value, char delimiter)
{
  std::vector<std::string> parts;
  std::stringstream stream(value);
  std::string item;
  while (std::getline(stream, item, delimiter))
  {
    if (!item.empty())
    {
      parts.push_back(item);
    }
  }

  return parts;
}

std::optional<std::chrono::system_clock::time_point> parse_segment_start_time(
  const std::string& file_name)
{
  if (file_name.size() < 19)
  {
    return std::nullopt;
  }

  std::tm tm{};
  std::istringstream stream(file_name.substr(0, 19));
  stream >> std::get_time(&tm, "%Y-%m-%d_%H-%M-%S");
  if (stream.fail())
  {
    return std::nullopt;
  }

  const std::time_t value = std::mktime(&tm);
  if (value < 0)
  {
    return std::nullopt;
  }

  return std::chrono::system_clock::from_time_t(value);
}

std::optional<std::chrono::system_clock::time_point> parse_requested_time(
  const std::string& value)
{
  if (value.empty())
  {
    return std::nullopt;
  }

  std::tm tm{};
  std::istringstream stream(value);
  stream >> std::get_time(&tm, "%Y-%m-%dT%H:%M:%S");
  if (stream.fail())
  {
    return std::nullopt;
  }

  const std::time_t parsed_time = std::mktime(&tm);
  if (parsed_time < 0)
  {
    return std::nullopt;
  }

  return std::chrono::system_clock::from_time_t(parsed_time);
}
}

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
  const auto camera_dir = base_path / camera_id;

  if (!std::filesystem::exists(camera_dir))
    return std::nullopt;

  std::vector<std::pair<std::chrono::system_clock::time_point, std::string>> files;

  for (const auto& entry : std::filesystem::recursive_directory_iterator(camera_dir))
  {
    if (!entry.is_regular_file())
      continue;

    if (entry.path().extension() != ".mkv")
      continue;

    auto parsed = parse_segment_start_time(entry.path().filename().string());
    if (!parsed)
      continue;

    files.emplace_back(*parsed, entry.path().string());
  }

  if (files.empty())
    return std::nullopt;

  std::sort(files.begin(), files.end(),
    [](const auto& a, const auto& b)
    {
      return a.first < b.first;
    });

  // --- ключевая логика ---
  for (const auto& [ts, path] : files)
  {
    if (ts >= start_time)
      return path;
  }

  // fallback: если нет >=, берём последний
  return files.back().second;
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

    if (m_webrtc_output_sink)
    {
      error = "web rtc stream already running";
      return true;
    }

    m_webrtc_output_sink = 
      std::make_shared<webrtc_output_sink>(resolve_default_webrtc_rtsp_url(m_client_id));
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
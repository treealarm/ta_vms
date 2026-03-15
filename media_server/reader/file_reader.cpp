#include <algorithm>
#include <chrono>
#include <ctime>
#include <filesystem>
#include <iomanip>
#include <iostream>
#include <optional>
#include <sstream>
#include <utility>

#include "file_reader.h"
#include "media/av_deleters.h"

namespace {
std::vector<std::string> split(const std::string& value, char delimiter) {
  std::vector<std::string> parts;
  std::stringstream stream(value);
  std::string item;
  while (std::getline(stream, item, delimiter)) {
    if (!item.empty()) {
      parts.push_back(item);
    }
  }

  return parts;
}

std::optional<std::chrono::system_clock::time_point> parse_segment_start_time(
    const std::string& file_name) {
  if (file_name.size() < 19) {
    return std::nullopt;
  }

  std::tm tm{};
  std::istringstream stream(file_name.substr(0, 19));
  stream >> std::get_time(&tm, "%Y-%m-%d_%H-%M-%S");
  if (stream.fail()) {
    return std::nullopt;
  }

  const std::time_t value = std::mktime(&tm);
  if (value < 0) {
    return std::nullopt;
  }

  return std::chrono::system_clock::from_time_t(value);
}

std::optional<std::chrono::system_clock::time_point> parse_requested_time(
    const std::string& value) {
  if (value.empty()) {
    return std::nullopt;
  }

  std::tm tm{};
  std::istringstream stream(value);
  stream >> std::get_time(&tm, "%Y-%m-%dT%H:%M:%S");
  if (stream.fail()) {
    return std::nullopt;
  }

  const std::time_t parsed_time = std::mktime(&tm);
  if (parsed_time < 0) {
    return std::nullopt;
  }

  return std::chrono::system_clock::from_time_t(parsed_time);
}
}

file_reader::file_reader() = default;

file_reader::file_reader(std::string camera_id, std::string storage_base_path)
    : m_camera_id(std::move(camera_id)),
      m_storage_base_path(std::move(storage_base_path)) {}

file_reader::~file_reader() {
  stop();
  disconnect();
}

bool file_reader::open(const std::string& source) {
  m_file_paths.clear();
  m_current_file_index = 0;

  if (!m_camera_id.empty() && !m_storage_base_path.empty()) {
    if (!initialize_recording_file_sequence(source)) {
      return false;
    }
  } else if (source.rfind("playlist:", 0) == 0) {
    const auto list = source.substr(9);
    m_file_paths = split(list, '|');
  } else if (source.rfind("file:", 0) == 0) {
    m_file_paths.push_back(source.substr(5));
  } else {
    m_file_paths.push_back(source);
  }

  if (m_file_paths.empty()) {
    return false;
  }

  disconnect();
  return connect_current_file();
}

bool file_reader::initialize_recording_file_sequence(const std::string& start_time) {
  const auto requested_time = parse_requested_time(start_time);
  if (!requested_time) {
    std::cerr << "Invalid start time, expected YYYY-MM-DDTHH:MM:SS\n";
    return false;
  }

  const std::filesystem::path camera_directory = m_storage_base_path / m_camera_id;
  if (!std::filesystem::exists(camera_directory)) {
    std::cerr << "records not found for camera: " << m_camera_id << "\n";
    return false;
  }

  std::vector<std::filesystem::path> candidate_files;
  for (const auto& entry : std::filesystem::recursive_directory_iterator(camera_directory)) {
    if (!entry.is_regular_file()) {
      continue;
    }

    if (entry.path().extension() != ".mkv") {
      continue;
    }

    if (parse_segment_start_time(entry.path().filename().string())) {
      candidate_files.push_back(entry.path());
    }
  }

  if (candidate_files.empty()) {
    std::cerr << "no playable files found for camera: " << m_camera_id << "\n";
    return false;
  }

  std::sort(candidate_files.begin(), candidate_files.end());

  size_t best_index = 0;
  std::chrono::system_clock::duration best_difference =
      std::chrono::system_clock::duration::max();

  for (size_t i = 0; i < candidate_files.size(); ++i) {
    const auto parsed_time = parse_segment_start_time(candidate_files[i].filename().string());
    if (!parsed_time) {
      continue;
    }

    const auto difference = *parsed_time > *requested_time
        ? *parsed_time - *requested_time
        : *requested_time - *parsed_time;
    if (difference < best_difference) {
      best_difference = difference;
      best_index = i;
    }
  }

  m_file_paths.reserve(candidate_files.size() - best_index);
  for (size_t i = best_index; i < candidate_files.size(); ++i) {
    m_file_paths.push_back(candidate_files[i].string());
  }

  return !m_file_paths.empty();
}

bool file_reader::connect_current_file() {
  disconnect();

  if (m_current_file_index >= m_file_paths.size()) {
    return false;
  }

  const auto& file_path = m_file_paths[m_current_file_index];

  if (avformat_open_input(&m_ctx, file_path.c_str(), nullptr, nullptr) < 0) {
    std::cerr << "Failed to open file source: " << file_path << "\n";
    return false;
  }

  if (avformat_find_stream_info(m_ctx, nullptr) < 0) {
    std::cerr << "Failed to get stream info\n";
    disconnect();
    return false;
  }

  m_video_index = -1;
  m_forward_stream_indexes.clear();
  for (unsigned i = 0; i < m_ctx->nb_streams; ++i) {
    const AVMediaType media_type = m_ctx->streams[i]->codecpar->codec_type;
    if (media_type == AVMEDIA_TYPE_VIDEO && m_video_index < 0) {
      m_video_index = static_cast<int>(i);
    }

    if (media_type == AVMEDIA_TYPE_VIDEO || media_type == AVMEDIA_TYPE_AUDIO) {
      m_forward_stream_indexes.insert(static_cast<int>(i));
    }
  }

  if (m_video_index < 0) {
    std::cerr << "No video stream\n";
    disconnect();
    return false;
  }

  std::cerr << "File source opened: " << file_path << "\n";
  return true;
}

bool file_reader::advance_to_next_file() {
  if (m_current_file_index + 1 >= m_file_paths.size()) {
    return false;
  }

  ++m_current_file_index;
  return connect_current_file();
}

void file_reader::disconnect() {
  m_video_index = -1;
  m_forward_stream_indexes.clear();
  if (m_ctx) {
    avformat_close_input(&m_ctx);
  }
}

void file_reader::add_sink(media_sink* sink) {
  std::scoped_lock lock(m_sinks_mutex);
  m_sinks.push_back(sink);
}

void file_reader::remove_sink(media_sink* sink) {
  std::scoped_lock lock(m_sinks_mutex);
  m_sinks.erase(std::remove(m_sinks.begin(), m_sinks.end(), sink), m_sinks.end());
}

AVStream* file_reader::video_stream() const {
  if (m_ctx && m_video_index >= 0) {
    return m_ctx->streams[m_video_index];
  }

  return nullptr;
}

void file_reader::start() {
  if (m_running) {
    return;
  }

  m_running = true;
  m_thread = std::thread([this] { read_loop(); });
}

void file_reader::stop() {
  m_running = false;
  if (m_thread.joinable()) {
    m_thread.join();
  }
}

void file_reader::read_loop() {
  while (m_running) {
    if (!m_ctx || m_video_index < 0 || m_forward_stream_indexes.empty()) {
      if (!connect_current_file()) {
        break;
      }
    }

    avpacket_ptr pkt(av_packet_alloc(), avpacket_deleter{});
    const int read_result = av_read_frame(m_ctx, pkt.get());
    if (read_result < 0) {
      if (!advance_to_next_file()) {
        break;
      }

      continue;
    }

    if (!m_forward_stream_indexes.contains(pkt->stream_index)) {
      continue;
    }

    auto media = std::make_shared<media_packet>();
    media->packet = pkt;
    media->stream_index = pkt->stream_index;
    media->time_base = m_ctx->streams[pkt->stream_index]->time_base;
    media->media_type = m_ctx->streams[pkt->stream_index]->codecpar->codec_type;
    avcodec_parameters_ptr codec_copy(avcodec_parameters_alloc(),
      [](AVCodecParameters* params) { avcodec_parameters_free(&params); });
    if (!codec_copy) {
      continue;
    }

    if (avcodec_parameters_copy(codec_copy.get(), m_ctx->streams[pkt->stream_index]->codecpar) < 0) {
      continue;
    }

    media->codec_parameters = std::move(codec_copy);

    std::vector<media_sink*> sinks_copy;
    {
      std::scoped_lock lock(m_sinks_mutex);
      sinks_copy = m_sinks;
    }

    for (auto* sink : sinks_copy) {
      auto sink_media = clone_media_packet(media);
      if (!sink_media) {
        continue;
      }

      sink->on_packet(sink_media);
    }
  }

  disconnect();
}

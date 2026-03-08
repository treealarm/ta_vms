#include <algorithm>
#include <iostream>
#include <sstream>

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
}

file_reader::file_reader() = default;

file_reader::~file_reader() {
  stop();
  disconnect();
}

bool file_reader::open(const std::string& source) {
  m_file_paths.clear();
  m_current_file_index = 0;

  if (source.rfind("playlist:", 0) == 0) {
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
  for (unsigned i = 0; i < m_ctx->nb_streams; ++i) {
    if (m_ctx->streams[i]->codecpar->codec_type == AVMEDIA_TYPE_VIDEO) {
      m_video_index = static_cast<int>(i);
      break;
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
    if (!m_ctx || m_video_index < 0) {
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

    if (pkt->stream_index != m_video_index) {
      continue;
    }

    auto media = std::make_shared<media_packet>();
    media->packet = pkt;
    media->stream_index = m_video_index;
    media->time_base = m_ctx->streams[m_video_index]->time_base;

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

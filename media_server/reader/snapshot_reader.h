#pragma once

#include <chrono>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

#include "interfaces/media_packet.h"
#include "interfaces/media_sink.h"
#include "interfaces/reader.h"
#include "sink_container/sink_container_impl.h"

extern "C" {
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/opt.h>
}

class snapshot_reader
{
public:
  explicit snapshot_reader(
    std::chrono::milliseconds position);
  ~snapshot_reader();

  bool open(const std::string& source);

  AVStream* video_stream();
  std::vector<std::shared_ptr<media_packet>> snapshot_packets() const;

private:
  bool connect_current_file(const std::string& source);
  bool decode_to_snapshot_packets(std::chrono::milliseconds position);
  void build_placeholder_packets();
  void disconnect();
  bool init_encoder();
  std::shared_ptr<media_packet> make_video_snapshot_packet(AVPacket* pkt) const;
  std::shared_ptr<media_packet> make_placeholder_packet(int stream_index) const;

  avformat_context_ptr m_ctx = nullptr;
  int m_video_index = -1;
  std::vector<int> m_other_stream_indexes;
  std::chrono::milliseconds m_position;

  avframe_ptr m_snapshot_frame;
  std::vector<std::shared_ptr<media_packet>> m_snapshot_packets;

  AVCodecContext* m_encoder_ctx = nullptr;
  AVRational m_encoder_time_base{ 1, 25 };

  mutable std::mutex m_mutex;
};

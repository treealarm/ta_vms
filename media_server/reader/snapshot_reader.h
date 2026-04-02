#pragma once

#include <chrono>
#include <memory>
#include <mutex>
#include <string>
#include <vector>
#include <deque>

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

  void build_placeholder_packets();
  void disconnect();
  bool init_encoder();
  std::shared_ptr<media_packet> make_video_snapshot_packet(AVPacket* pkt, int64_t target_ts) const;
  std::shared_ptr<media_packet> make_placeholder_packet(int stream_index) const;

  bool init_decoder();
  bool decode_next_frame();
  bool fill_buffer_from_position(int64_t target_ts);
  size_t find_frame_ge(int64_t target_ts) const;
  bool encode_current_frame();
  bool forward();
  bool prev();

  avformat_context_ptr m_ctx = nullptr;
  int m_video_index = -1;
  std::vector<int> m_other_stream_indexes;
  std::chrono::milliseconds m_position;

  std::vector<std::shared_ptr<media_packet>> m_snapshot_packets;

  AVCodecContext* m_encoder_ctx = nullptr;

  mutable std::mutex m_mutex;

  struct buffered_frame
  {
    avframe_ptr frame;
    int64_t pts = AV_NOPTS_VALUE;
  };

  std::deque<buffered_frame> m_frame_buffer;
  size_t m_current_index = 0;
  size_t m_max_buffer = 100;
  AVCodecContext* m_decoder_ctx = nullptr;
};

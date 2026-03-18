#pragma once

#include <condition_variable>
#include <cstddef>
#include <cstdint>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

extern "C" {
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libswscale/swscale.h>
}

#include "interfaces/media_sink.h"

class jpeg_streamer : public media_sink {
public:
  jpeg_streamer();
  ~jpeg_streamer() override;

  bool init(AVStream* in_stream, std::string& error);
  void on_packet(const std::shared_ptr<media_packet>& media) override;
  void close();

  bool wait_next_frame(std::vector<uint8_t>& frame, uint64_t& frame_number);

private:
  bool init_decoder(AVStream* in_stream, std::string& error);
  bool init_encoder(std::string& error);

  void cleanup_codec();

private:
  AVCodecContext* m_decoder_ctx = nullptr;
  AVCodecContext* m_encoder_ctx = nullptr;

  AVFrame* m_decoded_frame = nullptr;
  AVFrame* m_scaled_frame = nullptr;
  SwsContext* m_sws_ctx = nullptr;

  std::mutex m_mutex;
  std::condition_variable m_frame_cv;
  std::vector<uint8_t> m_last_frame;
  uint64_t m_last_frame_number = 0;
  uint64_t m_consumed_frame_number = 0;
  bool m_closed = false;
};

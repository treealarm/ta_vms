#pragma once

#include <cstddef>
#include <cstdint>
#include <memory>
#include <mutex>
#include <string>

extern "C" {
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libswscale/swscale.h>
}

#include "media/media_sink.h"

class jpeg_streamer : public media_sink {
public:
  jpeg_streamer(const std::string& host, uint16_t port);
  ~jpeg_streamer() override;

  bool init(AVStream* in_stream, std::string& error);
  void on_packet(const std::shared_ptr<media_packet>& media) override;
  void close();

private:
  bool open_socket(std::string& error);
  bool init_decoder(AVStream* in_stream, std::string& error);
  bool init_encoder(std::string& error);

  bool send_frame(const uint8_t* data, size_t size);
  void cleanup_codec();
  void cleanup_socket();

private:
  std::string m_host;
  uint16_t m_port;

  int m_socket_fd = -1;

  AVCodecContext* m_decoder_ctx = nullptr;
  AVCodecContext* m_encoder_ctx = nullptr;

  AVFrame* m_decoded_frame = nullptr;
  AVFrame* m_scaled_frame = nullptr;
  SwsContext* m_sws_ctx = nullptr;

  std::mutex m_mutex;
};

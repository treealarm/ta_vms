#pragma once

#include <functional>
#include <memory>

extern "C" {
#include <libavcodec/avcodec.h>
#include <libswscale/swscale.h>
}

#include "media/media_sink.h"

class compat_sink : public media_sink {
public:
  using packet_callback = std::function<void(const std::shared_ptr<media_packet>&)>;

  explicit compat_sink(packet_callback on_packet_ready);
  ~compat_sink() override;

  bool init(const avcodec_parameters_ptr& codec_parameters, AVRational time_base);
  void on_packet(const std::shared_ptr<media_packet>& media) override;
  void close();

  const avcodec_parameters_ptr& output_codec_parameters() const;
  AVRational output_time_base() const;

private:
  enum class mode {
    unknown,
    copy_h264,
    encode_libx264,
    failed
  };

  bool init_copy_mode(const avcodec_parameters_ptr& codec_parameters, AVRational time_base);
  bool init_transcode_mode(const avcodec_parameters_ptr& codec_parameters, AVRational time_base);
  void transcode_packet(const std::shared_ptr<media_packet>& media);
  void encode_frame(AVFrame* frame);
  void drain_encoder_packets();
  void flush_decoder_encoder();
  void emit_packet(const std::shared_ptr<media_packet>& media);

private:
  packet_callback m_on_packet_ready;
  mode m_mode = mode::unknown;
  bool m_is_initialized = false;

  AVCodecContext* m_decoder_ctx = nullptr;
  AVCodecContext* m_encoder_ctx = nullptr;
  SwsContext* m_sws_ctx = nullptr;

  avcodec_parameters_ptr m_out_codec_parameters;
  AVRational m_out_time_base{};

  AVFrame* m_scaled_frame = nullptr;
};

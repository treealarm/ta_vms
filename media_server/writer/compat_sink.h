#pragma once

#include <memory>
#include <vector>
#include <mutex>

extern "C" {
#include <libavcodec/avcodec.h>
#include <libswresample/swresample.h>
#include <libswscale/swscale.h>
}

#include "media/sink_container.h"

class compat_sink : public media_sink, public sink_container {
public:
  compat_sink() = default;
  ~compat_sink() override;

  bool init(const avcodec_parameters_ptr& codec_parameters, AVRational time_base);
  void add_sink(media_sink* sink) override;
  void remove_sink(media_sink* sink) override;
  void on_packet(const std::shared_ptr<media_packet>& media) override;
  void close();

  const avcodec_parameters_ptr& output_codec_parameters() const;
  AVRational output_time_base() const;

private:
  enum class mode {
    unknown,
    copy_h264,
    copy_opus,
    encode_libx264,
    encode_opus,
    failed
  };

  bool init_copy_mode(const avcodec_parameters_ptr& codec_parameters, AVRational time_base);
  bool init_copy_audio_mode(const avcodec_parameters_ptr& codec_parameters, AVRational time_base);
  bool init_transcode_mode(const avcodec_parameters_ptr& codec_parameters, AVRational time_base);
  bool init_audio_transcode_mode(const avcodec_parameters_ptr& codec_parameters, AVRational time_base);
  void transcode_packet(const std::shared_ptr<media_packet>& media);
  void transcode_audio_packet(const std::shared_ptr<media_packet>& media);
  void encode_frame(AVFrame* frame);
  void encode_audio_frame(AVFrame* frame);
  void drain_encoder_packets();
  void flush_decoder_encoder();
  void emit_packet(const std::shared_ptr<media_packet>& media);

private:
  std::vector<media_sink*> m_sinks;
  mutable std::mutex m_sinks_mutex;
  mode m_mode = mode::unknown;
  bool m_is_initialized = false;

  AVCodecContext* m_decoder_ctx = nullptr;
  AVCodecContext* m_encoder_ctx = nullptr;
  SwsContext* m_sws_ctx = nullptr;
  SwrContext* m_swr_ctx = nullptr;

  avcodec_parameters_ptr m_out_codec_parameters;
  AVRational m_out_time_base{};

  AVFrame* m_scaled_frame = nullptr;
  AVFrame* m_audio_resampled_frame = nullptr;
};

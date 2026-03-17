#pragma once

#include <functional>
#include <memory>

extern "C" {
#include <libavcodec/avcodec.h>
#include <libswresample/swresample.h>
#include <libswscale/swscale.h>
#include <libavutil/audio_fifo.h>
}

class media_packet;

using avcodec_parameters_ptr = std::shared_ptr<AVCodecParameters>;

class stream_processor {
public:
  using emit_fn = std::function<void(const std::shared_ptr<media_packet>&)>;

  virtual ~stream_processor() = default;

  virtual bool init(const avcodec_parameters_ptr&, AVRational, emit_fn) = 0;
  virtual void process(const std::shared_ptr<media_packet>&) = 0;
  virtual void flush() = 0;

  virtual const avcodec_parameters_ptr& output_codec_parameters() const = 0;
  virtual AVRational output_time_base() const = 0;
};

class video_copy_processor : public stream_processor {
public:
  bool init(const avcodec_parameters_ptr&, AVRational, emit_fn) override;
  void process(const std::shared_ptr<media_packet>&) override;
  void flush() override;
  const avcodec_parameters_ptr& output_codec_parameters() const override;
  AVRational output_time_base() const override;

private:
  emit_fn m_emit;
  avcodec_parameters_ptr m_out_codec_parameters;
  AVRational m_out_time_base{};
};

class audio_copy_processor : public stream_processor {
public:
  bool init(const avcodec_parameters_ptr&, AVRational, emit_fn) override;
  void process(const std::shared_ptr<media_packet>&) override;
  void flush() override;
  const avcodec_parameters_ptr& output_codec_parameters() const override;
  AVRational output_time_base() const override;

private:
  emit_fn m_emit;
  avcodec_parameters_ptr m_out_codec_parameters;
  AVRational m_out_time_base{};
};

class video_transcode_processor : public stream_processor {
public:
  ~video_transcode_processor() override;
  bool init(const avcodec_parameters_ptr&, AVRational, emit_fn) override;
  void process(const std::shared_ptr<media_packet>&) override;
  void flush() override;
  const avcodec_parameters_ptr& output_codec_parameters() const override;
  AVRational output_time_base() const override;

private:
  void encode_frame(AVFrame* frame);
  void drain_encoder_packets();

private:
  emit_fn m_emit;
  AVCodecContext* m_decoder_ctx = nullptr;
  AVCodecContext* m_encoder_ctx = nullptr;
  SwsContext* m_sws_ctx = nullptr;
  AVFrame* m_scaled_frame = nullptr;
  avcodec_parameters_ptr m_out_codec_parameters;
  AVRational m_out_time_base{};
};

class audio_transcode_processor : public stream_processor {
public:
  ~audio_transcode_processor() override;
  bool init(const avcodec_parameters_ptr&, AVRational, emit_fn) override;
  void process(const std::shared_ptr<media_packet>&) override;
  void flush() override;
  const avcodec_parameters_ptr& output_codec_parameters() const override;
  AVRational output_time_base() const override;

private:
  void encode_audio_frame(AVFrame* frame);
  void drain_encoder_packets();

private:
  emit_fn m_emit;
  AVCodecContext* m_decoder_ctx = nullptr;
  AVCodecContext* m_encoder_ctx = nullptr;
  SwrContext* m_swr_ctx = nullptr;
  AVAudioFifo* m_audio_fifo = nullptr;
  avcodec_parameters_ptr m_out_codec_parameters;
  AVRational m_out_time_base{};
};

std::unique_ptr<stream_processor> create_processor(const avcodec_parameters_ptr& codec_parameters);


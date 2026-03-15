#include "writer/compat_sink.h"

#include <algorithm>
#include <iostream>

extern "C" {
#include <libavutil/opt.h>
}

compat_sink::~compat_sink()
{
  close();
}

bool compat_sink::init(const avcodec_parameters_ptr& codec_parameters, AVRational time_base)
{
  close();

  if (!codec_parameters) {
    m_mode = mode::failed;
    return false;
  }

  if (codec_parameters->codec_type == AVMEDIA_TYPE_VIDEO) {
    if (codec_parameters->codec_id == AV_CODEC_ID_H264) {
      return init_copy_mode(codec_parameters, time_base);
    }
    return init_transcode_mode(codec_parameters, time_base);
  }

  if (codec_parameters->codec_type == AVMEDIA_TYPE_AUDIO) {
    if (codec_parameters->codec_id == AV_CODEC_ID_OPUS) {
      return init_copy_audio_mode(codec_parameters, time_base);
    }
    return init_audio_transcode_mode(codec_parameters, time_base);
  }

  m_mode = mode::failed;
  return false;
}

bool compat_sink::init_copy_mode(const avcodec_parameters_ptr& codec_parameters, AVRational time_base)
{
  m_mode = mode::copy_h264;
  m_out_time_base = time_base;
  m_out_codec_parameters = codec_parameters;
  m_is_initialized = true;
  return true;
}

bool compat_sink::init_copy_audio_mode(const avcodec_parameters_ptr& codec_parameters, AVRational time_base)
{
  m_mode = mode::copy_opus;
  m_out_time_base = time_base;
  m_out_codec_parameters = codec_parameters;
  m_is_initialized = true;
  return true;
}

bool compat_sink::init_transcode_mode(const avcodec_parameters_ptr& codec_parameters, AVRational time_base)
{
  const AVCodec* decoder = avcodec_find_decoder(codec_parameters->codec_id);
  if (!decoder) {
    std::cerr << "failed to find decoder for input stream\n";
    m_mode = mode::failed;
    return false;
  }

  m_decoder_ctx = avcodec_alloc_context3(decoder);
  if (!m_decoder_ctx) {
    std::cerr << "failed to allocate decoder context\n";
    m_mode = mode::failed;
    return false;
  }

  if (avcodec_parameters_to_context(m_decoder_ctx, codec_parameters.get()) < 0) {
    std::cerr << "failed to copy decoder parameters\n";
    m_mode = mode::failed;
    return false;
  }

  if (avcodec_open2(m_decoder_ctx, decoder, nullptr) < 0) {
    std::cerr << "failed to open decoder\n";
    m_mode = mode::failed;
    return false;
  }

  const AVCodec* encoder = avcodec_find_encoder_by_name("libx264");
  if (!encoder) {
    encoder = avcodec_find_encoder(AV_CODEC_ID_H264);
  }
  if (!encoder) {
    std::cerr << "failed to find h264 encoder\n";
    m_mode = mode::failed;
    return false;
  }

  m_encoder_ctx = avcodec_alloc_context3(encoder);
  if (!m_encoder_ctx) {
    std::cerr << "failed to allocate encoder context\n";
    m_mode = mode::failed;
    return false;
  }

  m_encoder_ctx->width = m_decoder_ctx->width;
  m_encoder_ctx->height = m_decoder_ctx->height;
  m_encoder_ctx->sample_aspect_ratio = m_decoder_ctx->sample_aspect_ratio;
  m_encoder_ctx->pix_fmt = AV_PIX_FMT_YUV420P;
  m_encoder_ctx->time_base = time_base.num > 0 ? time_base : AVRational{ 1, 25 };
  m_encoder_ctx->framerate = av_inv_q(m_encoder_ctx->time_base);
  m_encoder_ctx->max_b_frames = 0;
  m_encoder_ctx->gop_size = 50;

  av_opt_set(m_encoder_ctx->priv_data, "preset", "veryfast", 0);
  av_opt_set(m_encoder_ctx->priv_data, "tune", "zerolatency", 0);

  if (avcodec_open2(m_encoder_ctx, encoder, nullptr) < 0) {
    std::cerr << "failed to open h264 encoder\n";
    m_mode = mode::failed;
    return false;
  }

  m_sws_ctx = sws_getContext(m_decoder_ctx->width,
    m_decoder_ctx->height,
    m_decoder_ctx->pix_fmt,
    m_encoder_ctx->width,
    m_encoder_ctx->height,
    m_encoder_ctx->pix_fmt,
    SWS_BILINEAR,
    nullptr,
    nullptr,
    nullptr);
  if (!m_sws_ctx) {
    std::cerr << "failed to create swscale context\n";
    m_mode = mode::failed;
    return false;
  }

  m_scaled_frame = av_frame_alloc();
  if (!m_scaled_frame) {
    std::cerr << "failed to allocate frame\n";
    m_mode = mode::failed;
    return false;
  }

  m_scaled_frame->format = m_encoder_ctx->pix_fmt;
  m_scaled_frame->width = m_encoder_ctx->width;
  m_scaled_frame->height = m_encoder_ctx->height;

  if (av_frame_get_buffer(m_scaled_frame, 32) < 0) {
    std::cerr << "failed to allocate frame buffer\n";
    m_mode = mode::failed;
    return false;
  }

  avcodec_parameters_ptr out_codec_params(avcodec_parameters_alloc(),
    [](AVCodecParameters* params) { avcodec_parameters_free(&params); });
  if (!out_codec_params) {
    m_mode = mode::failed;
    return false;
  }

  if (avcodec_parameters_from_context(out_codec_params.get(), m_encoder_ctx) < 0) {
    std::cerr << "failed to export encoder params\n";
    m_mode = mode::failed;
    return false;
  }

  m_out_codec_parameters = std::move(out_codec_params);
  m_out_time_base = m_encoder_ctx->time_base;
  m_mode = mode::encode_libx264;
  m_is_initialized = true;

  return true;
}



bool compat_sink::init_audio_transcode_mode(const avcodec_parameters_ptr& codec_parameters, AVRational time_base)
{
  const AVCodec* decoder = avcodec_find_decoder(codec_parameters->codec_id);
  if (!decoder) {
    std::cerr << "failed to find audio decoder for input stream\n";
    m_mode = mode::failed;
    return false;
  }

  m_decoder_ctx = avcodec_alloc_context3(decoder);
  if (!m_decoder_ctx) {
    m_mode = mode::failed;
    return false;
  }

  if (avcodec_parameters_to_context(m_decoder_ctx, codec_parameters.get()) < 0 ||
      avcodec_open2(m_decoder_ctx, decoder, nullptr) < 0) {
    m_mode = mode::failed;
    return false;
  }

  const AVCodec* encoder = avcodec_find_encoder_by_name("libopus");
  if (!encoder) {
    std::cerr << "failed to find libopus encoder (FFmpeg must be built with --enable-libopus)\n";
    m_mode = mode::failed;
    return false;
  }

  m_encoder_ctx = avcodec_alloc_context3(encoder);
  if (!m_encoder_ctx) {
    m_mode = mode::failed;
    return false;
  }

  m_encoder_ctx->sample_rate = 48000;
  av_channel_layout_default(&m_encoder_ctx->ch_layout, 2);
  m_encoder_ctx->sample_fmt = encoder->sample_fmts ? encoder->sample_fmts[0] : AV_SAMPLE_FMT_FLTP;
  m_encoder_ctx->time_base = AVRational{ 1, m_encoder_ctx->sample_rate };
  m_encoder_ctx->bit_rate = 128000;

  if (avcodec_open2(m_encoder_ctx, encoder, nullptr) < 0) {
    std::cerr << "failed to open opus encoder\n";
    m_mode = mode::failed;
    return false;
  }

  if (swr_alloc_set_opts2(&m_swr_ctx,
                          &m_encoder_ctx->ch_layout,
                          m_encoder_ctx->sample_fmt,
                          m_encoder_ctx->sample_rate,
                          &m_decoder_ctx->ch_layout,
                          m_decoder_ctx->sample_fmt,
                          m_decoder_ctx->sample_rate,
                          0,
                          nullptr) < 0 || !m_swr_ctx || swr_init(m_swr_ctx) < 0) {
    std::cerr << "failed to initialize swresample for opus\n";
    m_mode = mode::failed;
    return false;
  }

  m_audio_resampled_frame = av_frame_alloc();
  if (!m_audio_resampled_frame) {
    m_mode = mode::failed;
    return false;
  }

  m_audio_resampled_frame->format = m_encoder_ctx->sample_fmt;
  m_audio_resampled_frame->sample_rate = m_encoder_ctx->sample_rate;
  m_audio_resampled_frame->ch_layout = m_encoder_ctx->ch_layout;
  m_audio_resampled_frame->nb_samples = m_encoder_ctx->frame_size > 0 ? m_encoder_ctx->frame_size : 960;

  if (av_frame_get_buffer(m_audio_resampled_frame, 0) < 0) {
    m_mode = mode::failed;
    return false;
  }

  avcodec_parameters_ptr out_codec_params(avcodec_parameters_alloc(),
    [](AVCodecParameters* params) { avcodec_parameters_free(&params); });
  if (!out_codec_params || avcodec_parameters_from_context(out_codec_params.get(), m_encoder_ctx) < 0) {
    m_mode = mode::failed;
    return false;
  }

  m_out_codec_parameters = std::move(out_codec_params);
  m_out_time_base = m_encoder_ctx->time_base;
  m_mode = mode::encode_opus;
  m_is_initialized = true;
  return true;
}

void compat_sink::transcode_audio_packet(const std::shared_ptr<media_packet>& media)
{
  if (avcodec_send_packet(m_decoder_ctx, media->packet.get()) < 0) {
    return;
  }

  AVFrame* decoded = av_frame_alloc();
  if (!decoded) {
    return;
  }

  while (avcodec_receive_frame(m_decoder_ctx, decoded) == 0) {
    if (av_frame_make_writable(m_audio_resampled_frame) < 0) {
      break;
    }

    av_frame_unref(m_audio_resampled_frame);
    m_audio_resampled_frame->nb_samples = decoded->nb_samples;
    m_audio_resampled_frame->format = m_encoder_ctx->sample_fmt;
    m_audio_resampled_frame->sample_rate = m_encoder_ctx->sample_rate;
    m_audio_resampled_frame->ch_layout = m_encoder_ctx->ch_layout;
    if (av_frame_get_buffer(m_audio_resampled_frame, 0) < 0) {
      break;
    }

    if (swr_convert(m_swr_ctx,
                    m_audio_resampled_frame->data,
                    m_audio_resampled_frame->nb_samples,
                    const_cast<const uint8_t**>(decoded->data),
                    decoded->nb_samples) < 0) {
      break;
    }

    m_audio_resampled_frame->pts = av_rescale_q(decoded->pts, media->time_base, m_encoder_ctx->time_base);
    encode_audio_frame(m_audio_resampled_frame);
    av_frame_unref(decoded);
  }

  av_frame_free(&decoded);
}

void compat_sink::encode_audio_frame(AVFrame* frame)
{
  if (avcodec_send_frame(m_encoder_ctx, frame) < 0) {
    return;
  }

  drain_encoder_packets();
}
void compat_sink::add_sink(media_sink* sink)
{
  if (!sink) {
    return;
  }

  std::scoped_lock lock(m_sinks_mutex);
  m_sinks.push_back(sink);
}

void compat_sink::remove_sink(media_sink* sink)
{
  std::scoped_lock lock(m_sinks_mutex);
  m_sinks.erase(std::remove(m_sinks.begin(), m_sinks.end(), sink), m_sinks.end());
}

void compat_sink::on_packet(const std::shared_ptr<media_packet>& media)
{
  if (!media || !media->packet || !m_is_initialized) {
    return;
  }

  if (m_mode == mode::copy_h264 || m_mode == mode::copy_opus) {
    auto out_media = clone_media_packet(media);
    if (!out_media) {
      return;
    }

    out_media->codec_parameters = m_out_codec_parameters;
    out_media->time_base = m_out_time_base;
    emit_packet(out_media);
    return;
  }

  if (m_mode == mode::encode_libx264) {
    transcode_packet(media);
    return;
  }

  if (m_mode == mode::encode_opus) {
    transcode_audio_packet(media);
  }
}

void compat_sink::transcode_packet(const std::shared_ptr<media_packet>& media)
{
  if (avcodec_send_packet(m_decoder_ctx, media->packet.get()) < 0) {
    return;
  }

  AVFrame* decoded = av_frame_alloc();
  if (!decoded) {
    return;
  }

  while (avcodec_receive_frame(m_decoder_ctx, decoded) == 0) {
    if (av_frame_make_writable(m_scaled_frame) < 0) {
      break;
    }

    sws_scale(m_sws_ctx,
      decoded->data,
      decoded->linesize,
      0,
      m_decoder_ctx->height,
      m_scaled_frame->data,
      m_scaled_frame->linesize);

    m_scaled_frame->pts = av_rescale_q(decoded->pts, media->time_base, m_encoder_ctx->time_base);
    encode_frame(m_scaled_frame);
    av_frame_unref(decoded);
  }

  av_frame_free(&decoded);
}

void compat_sink::encode_frame(AVFrame* frame)
{
  if (avcodec_send_frame(m_encoder_ctx, frame) < 0) {
    return;
  }

  drain_encoder_packets();
}

void compat_sink::drain_encoder_packets()
{
  AVPacket* out_pkt = av_packet_alloc();
  if (!out_pkt) {
    return;
  }

  while (avcodec_receive_packet(m_encoder_ctx, out_pkt) == 0) {
    avpacket_ptr pkt(out_pkt, avpacket_deleter{});
    auto out_media = std::make_shared<media_packet>();
    out_media->packet = pkt;
    out_media->stream_index = 0;
    out_media->time_base = m_encoder_ctx->time_base;
    out_media->media_type = m_out_codec_parameters ? m_out_codec_parameters->codec_type : AVMEDIA_TYPE_UNKNOWN;
    out_media->codec_parameters = m_out_codec_parameters;
    emit_packet(out_media);

    out_pkt = av_packet_alloc();
    if (!out_pkt) {
      return;
    }
  }

  av_packet_free(&out_pkt);
}

void compat_sink::flush_decoder_encoder()
{
  if (m_mode != mode::encode_libx264 && m_mode != mode::encode_opus) {
    return;
  }

  avcodec_send_packet(m_decoder_ctx, nullptr);

  if (m_mode == mode::encode_opus) {
    avcodec_send_frame(m_encoder_ctx, nullptr);
    drain_encoder_packets();
    return;
  }

  AVFrame* decoded = av_frame_alloc();
  if (decoded) {
    while (avcodec_receive_frame(m_decoder_ctx, decoded) == 0) {
      if (av_frame_make_writable(m_scaled_frame) < 0) {
        break;
      }

      sws_scale(m_sws_ctx,
        decoded->data,
        decoded->linesize,
        0,
        m_decoder_ctx->height,
        m_scaled_frame->data,
        m_scaled_frame->linesize);

      m_scaled_frame->pts = av_rescale_q(decoded->pts, m_decoder_ctx->time_base, m_encoder_ctx->time_base);
      encode_frame(m_scaled_frame);
      av_frame_unref(decoded);
    }

    av_frame_free(&decoded);
  }

  avcodec_send_frame(m_encoder_ctx, nullptr);
  drain_encoder_packets();
}

void compat_sink::emit_packet(const std::shared_ptr<media_packet>& media)
{
  std::vector<media_sink*> sinks_copy;
  {
    std::scoped_lock lock(m_sinks_mutex);
    sinks_copy = m_sinks;
  }

  for (auto* sink : sinks_copy) {
    if (!sink) {
      continue;
    }

    auto sink_media = clone_media_packet(media);
    if (!sink_media) {
      continue;
    }

    sink->on_packet(sink_media);
  }
}

void compat_sink::close()
{
  flush_decoder_encoder();

  if (m_scaled_frame) {
    av_frame_free(&m_scaled_frame);
  }

  if (m_audio_resampled_frame) {
    av_frame_free(&m_audio_resampled_frame);
  }

  if (m_swr_ctx) {
    swr_free(&m_swr_ctx);
  }

  if (m_sws_ctx) {
    sws_freeContext(m_sws_ctx);
    m_sws_ctx = nullptr;
  }

  if (m_decoder_ctx) {
    avcodec_free_context(&m_decoder_ctx);
  }

  if (m_encoder_ctx) {
    avcodec_free_context(&m_encoder_ctx);
  }

  m_out_codec_parameters.reset();
  m_out_time_base = AVRational{};
  m_mode = mode::unknown;
  m_is_initialized = false;
}

const avcodec_parameters_ptr& compat_sink::output_codec_parameters() const
{
  return m_out_codec_parameters;
}

AVRational compat_sink::output_time_base() const
{
  return m_out_time_base;
}

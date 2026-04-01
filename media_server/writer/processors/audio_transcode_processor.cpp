#include "writer/processors/audio_transcode_processor.h"

#include <iostream>

extern "C" {
#include <libavutil/audio_fifo.h>
#include <libswresample/swresample.h>
}

#include "interfaces/media_packet.h"

namespace 
{
avcodec_parameters_ptr alloc_out_params(AVCodecContext* encoder_ctx)
{
  avcodec_parameters_ptr out_codec_params(avcodec_parameters_alloc(),
    [](AVCodecParameters* params) { avcodec_parameters_free(&params); });
  if (!out_codec_params)
  {
    return nullptr;
  }

  if (avcodec_parameters_from_context(out_codec_params.get(), encoder_ctx) < 0) 
  {
    return nullptr;
  }

  return out_codec_params;
}


AVSampleFormat choose_supported_sample_fmt(AVCodecContext* ctx, const AVCodec* codec, AVSampleFormat wanted)
{
  const void* configs = nullptr;
  int num_configs = 0;

  int ret = avcodec_get_supported_config(ctx, codec, AV_CODEC_CONFIG_SAMPLE_FORMAT, 0, &configs, &num_configs);
  if (ret < 0 || num_configs == 0)
    return wanted; // fallback

  auto arr = reinterpret_cast<const AVSampleFormat*>(configs);
  for (int i = 0; i < num_configs; i++) 
  {
    if (arr[i] == wanted)
      return wanted;
  return arr[0];

  return arr[0]; // åñëè íåò — ïåðâûé ïîääåðæèâàåìûé
}

}

audio_transcode_processor::~audio_transcode_processor()
{
  flush();

  if (m_swr_ctx) {
    swr_free(&m_swr_ctx);
  }

  if (m_audio_fifo) {
    av_audio_fifo_free(m_audio_fifo);
    m_audio_fifo = nullptr;
  }

  if (m_decoder_ctx) {
    avcodec_free_context(&m_decoder_ctx);
  }

  if (m_encoder_ctx) {
    avcodec_free_context(&m_encoder_ctx);
  }
}

bool audio_transcode_processor::init(const avcodec_parameters_ptr& codec_parameters, AVRational, emit_fn emit)
{
  if (!codec_parameters) {
    return false;
  }

  m_emit = std::move(emit);

  const AVCodec* decoder = avcodec_find_decoder(codec_parameters->codec_id);
  if (!decoder) {
    std::cerr << "failed to find audio decoder for input stream\n";
    return false;
  }

  m_decoder_ctx = avcodec_alloc_context3(decoder);
  if (!m_decoder_ctx) {
    return false;
  }

  if (avcodec_parameters_to_context(m_decoder_ctx, codec_parameters.get()) < 0 ||
      avcodec_open2(m_decoder_ctx, decoder, nullptr) < 0) {
    return false;
  }

  const AVCodec* encoder = avcodec_find_encoder(m_codec_id);

  if (!encoder) 
  {
    
    std::cerr << "failed to find audio encoder for codec id: " << static_cast<int>(m_codec_id) << std::endl;

    if (m_codec_id == AV_CODEC_ID_OPUS)
    {
      std::cerr << "(FFmpeg must be built with --enable - libopus)" << std::endl;
    }
    return false;
  }

  m_encoder_ctx = avcodec_alloc_context3(encoder);
  if (!m_encoder_ctx) {
    return false;
  }

  m_encoder_ctx->sample_rate = 48000;
  av_channel_layout_default(&m_encoder_ctx->ch_layout, 2);

  m_encoder_ctx->sample_fmt = choose_supported_sample_fmt(
    m_encoder_ctx,
    encoder,
    AV_SAMPLE_FMT_FLTP
  );

  m_encoder_ctx->time_base = AVRational{ 1, m_encoder_ctx->sample_rate };
  m_encoder_ctx->bit_rate = 128000;

  if (avcodec_open2(m_encoder_ctx, encoder, nullptr) < 0) {
    return false;
  }

  m_audio_fifo = av_audio_fifo_alloc(
    m_encoder_ctx->sample_fmt,
    m_encoder_ctx->ch_layout.nb_channels,
    1024);

  if (!m_audio_fifo) {
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
    return false;
  }

  m_out_codec_parameters = alloc_out_params(m_encoder_ctx);
  if (!m_out_codec_parameters) {
    return false;
  }

  m_out_time_base = m_encoder_ctx->time_base;
  return true;
}

void audio_transcode_processor::process(const std::shared_ptr<media_packet>& media)
{
  if (!media || !media->packet || !m_decoder_ctx || !m_encoder_ctx || !m_swr_ctx || !m_audio_fifo) {
    return;
  }

  if (avcodec_send_packet(m_decoder_ctx, media->packet.get()) < 0) {
    return;
  }

  AVFrame* decoded = av_frame_alloc();
  if (!decoded) {
    return;
  }

  while (avcodec_receive_frame(m_decoder_ctx, decoded) == 0) {
    int dst_nb_samples = av_rescale_rnd(
      swr_get_delay(m_swr_ctx, m_decoder_ctx->sample_rate) + decoded->nb_samples,
      m_encoder_ctx->sample_rate,
      m_decoder_ctx->sample_rate,
      AV_ROUND_UP);

    AVFrame* resampled = av_frame_alloc();
    if (!resampled) {
      break;
    }

    resampled->format = m_encoder_ctx->sample_fmt;
    resampled->sample_rate = m_encoder_ctx->sample_rate;
    resampled->ch_layout = m_encoder_ctx->ch_layout;
    resampled->nb_samples = dst_nb_samples;

    if (av_frame_get_buffer(resampled, 0) < 0) {
      av_frame_free(&resampled);
      break;
    }

    int samples = swr_convert(
      m_swr_ctx,
      resampled->data,
      dst_nb_samples,
      (const uint8_t**)decoded->data,
      decoded->nb_samples);

    if (samples < 0) {
      av_frame_free(&resampled);
      break;
    }

    resampled->nb_samples = samples;

    av_audio_fifo_write(
      m_audio_fifo,
      (void**)resampled->data,
      resampled->nb_samples);

    av_frame_free(&resampled);

    while (av_audio_fifo_size(m_audio_fifo) >= m_encoder_ctx->frame_size) {
      AVFrame* frame = av_frame_alloc();
      if (!frame) {
        break;
      }

      frame->nb_samples = m_encoder_ctx->frame_size;
      frame->format = m_encoder_ctx->sample_fmt;
      frame->sample_rate = m_encoder_ctx->sample_rate;
      frame->ch_layout = m_encoder_ctx->ch_layout;

      if (av_frame_get_buffer(frame, 0) < 0) {
        av_frame_free(&frame);
        break;
      }

      av_audio_fifo_read(
        m_audio_fifo,
        (void**)frame->data,
        m_encoder_ctx->frame_size);

      encode_audio_frame(frame);
      av_frame_free(&frame);
    }

    av_frame_unref(decoded);
  }

  av_frame_free(&decoded);
}

void audio_transcode_processor::flush()
{
  if (!m_decoder_ctx || !m_encoder_ctx) {
    return;
  }

  avcodec_send_packet(m_decoder_ctx, nullptr);
  avcodec_send_frame(m_encoder_ctx, nullptr);
  drain_encoder_packets();
}

void audio_transcode_processor::encode_audio_frame(AVFrame* frame)
{
  if (avcodec_send_frame(m_encoder_ctx, frame) < 0) {
    return;
  }

  drain_encoder_packets();
}

void audio_transcode_processor::drain_encoder_packets()
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

    if (m_emit) {
      m_emit(out_media);
    }

    out_pkt = av_packet_alloc();
    if (!out_pkt) {
      return;
    }
  }

  av_packet_free(&out_pkt);
}

const avcodec_parameters_ptr& audio_transcode_processor::output_codec_parameters() const
{
  return m_out_codec_parameters;
}

AVRational audio_transcode_processor::output_time_base() const
{
  return m_out_time_base;
}

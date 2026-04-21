#include "writer/processors/video_transcode_processor.h"

#include <format>

extern "C" {
#include <libavutil/opt.h>
#include <libswscale/swscale.h>
}

#include "interfaces/media_packet.h"
#include "logger/logger.h"

namespace {
avcodec_parameters_ptr alloc_out_params(AVCodecContext* encoder_ctx)
{
  avcodec_parameters_ptr out_codec_params(avcodec_parameters_alloc(),
    [](AVCodecParameters* params) { avcodec_parameters_free(&params); });
  if (!out_codec_params) {
    return nullptr;
  }

  if (avcodec_parameters_from_context(out_codec_params.get(), encoder_ctx) < 0) {
    return nullptr;
  }

  return out_codec_params;
}
}

video_transcode_processor::video_transcode_processor(AVCodecID codec_id)
  : m_codec_id(codec_id)
{
}

video_transcode_processor::~video_transcode_processor()
{
  flush();

  if (m_scaled_frame) {
    av_frame_free(&m_scaled_frame);
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
}

bool video_transcode_processor::init(const avcodec_parameters_ptr& codec_parameters, AVRational time_base, emit_fn emit)
{
  if (!codec_parameters) {
    logger::instance().warning("video_transcode_processor: empty codec parameters");
    return false;
  }

  m_emit = std::move(emit);

  const AVCodec* decoder = avcodec_find_decoder(codec_parameters->codec_id);
  if (!decoder) {
    logger::instance().error("failed to find decoder for input stream");
    return false;
  }

  m_decoder_ctx = avcodec_alloc_context3(decoder);
  if (!m_decoder_ctx) {
    logger::instance().error("video_transcode_processor: failed to allocate decoder context");
    return false;
  }

  if (avcodec_parameters_to_context(m_decoder_ctx, codec_parameters.get()) < 0) {
    logger::instance().error("video_transcode_processor: failed to initialize decoder context");
    return false;
  }

  if (avcodec_open2(m_decoder_ctx, decoder, nullptr) < 0) {
    logger::instance().error("video_transcode_processor: failed to open decoder");
    return false;
  }

  const AVCodec* encoder = avcodec_find_encoder(m_codec_id);

  if (!encoder) {
    logger::instance().error(std::format("failed to find video encoder for codec id: {}", static_cast<int>(m_codec_id)));
    return false;
  }

  m_encoder_ctx = avcodec_alloc_context3(encoder);
  if (!m_encoder_ctx) {
    logger::instance().error("video_transcode_processor: failed to allocate encoder context");
    return false;
  }

  m_encoder_ctx->width = m_decoder_ctx->width;
  m_encoder_ctx->height = m_decoder_ctx->height;
  m_encoder_ctx->sample_aspect_ratio = m_decoder_ctx->sample_aspect_ratio;
  m_encoder_ctx->pix_fmt = AV_PIX_FMT_YUV420P;
  m_encoder_ctx->time_base = time_base.num > 0 ? time_base : AVRational{ 1, 25 };
  m_encoder_ctx->framerate = av_inv_q(m_encoder_ctx->time_base);
  m_encoder_ctx->max_b_frames = 0;
  m_encoder_ctx->gop_size = m_decoder_ctx->gop_size >= 1 ? m_decoder_ctx->gop_size : 25;

  av_opt_set(m_encoder_ctx->priv_data, "preset", "veryfast", 0);
  av_opt_set(m_encoder_ctx->priv_data, "tune", "zerolatency", 0);

  if (avcodec_open2(m_encoder_ctx, encoder, nullptr) < 0) {
    logger::instance().error("video_transcode_processor: failed to open encoder");
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
    logger::instance().error("video_transcode_processor: failed to create scaler context");
    return false;
  }

  m_scaled_frame = av_frame_alloc();
  if (!m_scaled_frame) {
    logger::instance().error("video_transcode_processor: failed to allocate scaled frame");
    return false;
  }

  m_scaled_frame->format = m_encoder_ctx->pix_fmt;
  m_scaled_frame->width = m_encoder_ctx->width;
  m_scaled_frame->height = m_encoder_ctx->height;

  if (av_frame_get_buffer(m_scaled_frame, 32) < 0) {
    logger::instance().error("video_transcode_processor: failed to allocate scaled frame buffer");
    return false;
  }

  m_out_codec_parameters = alloc_out_params(m_encoder_ctx);
  if (!m_out_codec_parameters) {
    logger::instance().error("video_transcode_processor: failed to build output codec parameters");
    return false;
  }

  m_out_time_base = m_encoder_ctx->time_base;
  return true;
}

void video_transcode_processor::process(const std::shared_ptr<media_packet>& media)
{
  if (!media || !media->packet || !m_decoder_ctx || !m_encoder_ctx) {
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

void video_transcode_processor::flush()
{
  if (!m_decoder_ctx || !m_encoder_ctx) {
    return;
  }

  avcodec_send_packet(m_decoder_ctx, nullptr);

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

void video_transcode_processor::encode_frame(AVFrame* frame)
{
  if (avcodec_send_frame(m_encoder_ctx, frame) < 0) {
    return;
  }

  drain_encoder_packets();
}

void video_transcode_processor::drain_encoder_packets()
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

const avcodec_parameters_ptr& video_transcode_processor::output_codec_parameters() const
{
  return m_out_codec_parameters;
}

AVRational video_transcode_processor::output_time_base() const
{
  return m_out_time_base;
}

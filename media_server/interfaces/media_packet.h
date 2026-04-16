#pragma once
#include <memory>

extern "C" {
#include <libavcodec/codec_par.h>
#include <libavcodec/packet.h>
#include <libavutil/avutil.h>
#include <libavutil/rational.h>
}

#include "av_deleters.h"

using avpacket_ptr = std::shared_ptr<AVPacket>;
using avframe_ptr = std::shared_ptr<AVFrame>;
using avcodec_parameters_ptr = std::shared_ptr<AVCodecParameters>;
using avformat_context_ptr = std::unique_ptr<AVFormatContext, avformat_context_deleter>;

struct media_packet {
  avpacket_ptr packet;
  int stream_index;
  AVRational time_base;
  AVMediaType media_type = AVMEDIA_TYPE_UNKNOWN;
  AVRational frame_rate{ 0, 1 };
  int sample_rate = 0;
  int nb_samples = 0;
  avcodec_parameters_ptr codec_parameters;
};



inline std::shared_ptr<media_packet> clone_media_packet(const std::shared_ptr<media_packet>& src) {
  if (!src || !src->packet)
    return nullptr;

  avpacket_ptr pkt_copy(av_packet_alloc(), avpacket_deleter{});
  if (!pkt_copy)
    return nullptr;

  if (av_packet_ref(pkt_copy.get(), src->packet.get()) < 0)
    return nullptr;

  auto dst = std::make_shared<media_packet>();
  dst->packet = std::move(pkt_copy);
  dst->stream_index = src->stream_index;
  dst->time_base = src->time_base;
  dst->media_type = src->media_type;
  dst->frame_rate = src->frame_rate;
  dst->sample_rate = src->sample_rate;
  dst->nb_samples = src->nb_samples;

  dst->codec_parameters = src->codec_parameters;

  return dst;
}

struct media_frame {
  avframe_ptr frame;
  int stream_index;
  AVRational time_base;
  AVMediaType media_type = AVMEDIA_TYPE_UNKNOWN;
};

#pragma once
#include <chrono>
#include <memory>
#include <optional>

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
  // When the frame was captured, mapped onto this host's wall clock from the source's PTS pace.
  // Unset for sources that cannot say (file readers, imports). Arrival time is NOT a substitute: on
  // a degrading link packets queue up and turn up seconds after the moment they show, which would
  // then be written into the archive as the clip's boundaries.
  std::optional<std::chrono::system_clock::time_point> wall_ts;
  // Monotonic receive time in microseconds (av_gettime_relative), -1 = unknown.
  // Live re-timing uses it as the authoritative clock when the source stamps
  // packets with a broken pts pace (see video_transcode_processor).
  int64_t arrival_us = -1;
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
  dst->wall_ts = src->wall_ts;
  dst->time_base = src->time_base;
  dst->media_type = src->media_type;
  dst->frame_rate = src->frame_rate;
  dst->sample_rate = src->sample_rate;
  dst->nb_samples = src->nb_samples;

  dst->codec_parameters = src->codec_parameters;
  dst->arrival_us = src->arrival_us;

  return dst;
}

struct media_frame {
  avframe_ptr frame;
  int stream_index;
  AVRational time_base;
  AVMediaType media_type = AVMEDIA_TYPE_UNKNOWN;
};

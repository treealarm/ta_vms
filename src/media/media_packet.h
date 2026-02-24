#pragma once
#include <memory>

extern "C" {
#include <libavcodec/packet.h>
#include <libavutil/rational.h>
}

#include "av_deleters.h"

using avpacket_ptr = std::shared_ptr<AVPacket>;
using avframe_ptr = std::shared_ptr<AVFrame>;

struct media_packet {
  avpacket_ptr packet;
  int stream_index;
  AVRational time_base;
};

struct media_frame {
  avframe_ptr frame;
  int stream_index;
  AVRational time_base;
};
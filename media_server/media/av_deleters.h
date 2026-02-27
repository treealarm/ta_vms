#pragma once
extern "C" {
#include <libavcodec/avcodec.h>
#include <libavutil/frame.h>
}

struct avpacket_deleter {
  void operator()(AVPacket* pkt) const noexcept {
    if (pkt) {
      av_packet_free(&pkt);
    }
  }
};

using avframe_ptr = std::shared_ptr<AVFrame>;

struct avframe_deleter {
  void operator()(AVFrame* f) const noexcept {
    if (f) av_frame_free(&f);
  }
};
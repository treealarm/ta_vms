#pragma once

extern "C" {
#include <libavcodec/codec_par.h>
#include <libavcodec/packet.h>
#include <libavutil/avutil.h>
#include <libavutil/rational.h>
}

namespace av_utils
{
  bool need_reinit(const AVCodecParameters* old_cp,
    AVRational old_tb,
    const AVCodecParameters* new_cp,
    AVRational new_tb);
}

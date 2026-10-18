#include "need_reinit.h"

namespace av_utils
{
 bool need_reinit(const AVCodecParameters* old_cp,
    AVRational old_tb,
    const AVCodecParameters* new_cp,
    AVRational new_tb) 
  {
    if (!old_cp || !new_cp)
      return true;

    // 1. The basics
    if (old_cp->codec_type != new_cp->codec_type ||
      old_cp->codec_id != new_cp->codec_id)
      return true;

    // 2. time_base
    if (old_tb.num != new_tb.num || old_tb.den != new_tb.den)
      return true;

    // 3. VIDEO
    if (new_cp->codec_type == AVMEDIA_TYPE_VIDEO)
    {
      if (old_cp->width != new_cp->width ||
        old_cp->height != new_cp->height ||
        old_cp->format != new_cp->format)
        return true;

      // sometimes matters
      if (old_cp->sample_aspect_ratio.num != new_cp->sample_aspect_ratio.num ||
        old_cp->sample_aspect_ratio.den != new_cp->sample_aspect_ratio.den)
        return true;

      // colour/space (not always critical, but safe to take into account)
      if (old_cp->color_space != new_cp->color_space ||
        old_cp->color_range != new_cp->color_range)
        return true;
    }

    // 4. AUDIO
    if (new_cp->codec_type == AVMEDIA_TYPE_AUDIO)
    {
      if (old_cp->sample_rate != new_cp->sample_rate ||
        old_cp->ch_layout.nb_channels != new_cp->ch_layout.nb_channels ||
        old_cp->format != new_cp->format)
        return true;

      if (av_channel_layout_compare(&old_cp->ch_layout, &new_cp->ch_layout))
        return true;
    }

    // 5. extradata (CRITICAL for H264/H265 and others)
    if (old_cp->extradata_size != new_cp->extradata_size)
      return true;

    if (old_cp->extradata && new_cp->extradata)
    {
      if (memcmp(old_cp->extradata,
        new_cp->extradata,
        old_cp->extradata_size) != 0)
        return true;
    }
    else if (old_cp->extradata || new_cp->extradata)
    {
      // one has it, the other does not
      return true;
    }

    return false;
  }
};
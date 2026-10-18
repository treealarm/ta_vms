#pragma once

#include <functional>
#include <memory>
#include <string>

#include "interfaces/media_packet.h"

extern "C" {
#include <libavcodec/avcodec.h>
}


class stream_processor 
{
public:
  using emit_fn = std::function<void(const std::shared_ptr<media_packet>&)>;

  virtual ~stream_processor() = default;

  virtual bool init(const avcodec_parameters_ptr&, AVRational time_base, AVRational frame_rate, emit_fn) = 0;
  virtual void process(const std::shared_ptr<media_packet>&) = 0;
  virtual void flush() = 0;

  virtual const avcodec_parameters_ptr& output_codec_parameters() const = 0;
  virtual AVRational output_time_base() const = 0;

  /// Which encoder re-compressed this stream, in the vocabulary VMS_VIDEO_ENCODER accepts
  /// ("vaapi", "x264") so that what the monitor shows and what an operator would type to force it
  /// are the same word. Empty means the stream was passed through untouched, which is what the
  /// copy processors report and is worth showing as its own answer.
  virtual std::string encoder_name() const { return {}; }
};

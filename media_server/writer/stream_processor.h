#pragma once

#include <functional>
#include <memory>

#include "interfaces/media_packet.h"

extern "C" {
#include <libavcodec/avcodec.h>
}

using avcodec_parameters_ptr = std::shared_ptr<AVCodecParameters>;

class stream_processor {
public:
  using emit_fn = std::function<void(const std::shared_ptr<media_packet>&)>;

  virtual ~stream_processor() = default;

  virtual bool init(const avcodec_parameters_ptr&, AVRational, emit_fn) = 0;
  virtual void process(const std::shared_ptr<media_packet>&) = 0;
  virtual void flush() = 0;

  virtual const avcodec_parameters_ptr& output_codec_parameters() const = 0;
  virtual AVRational output_time_base() const = 0;
};

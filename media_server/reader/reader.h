#pragma once

#include <string>

#include "media/media_sink.h"

extern "C" {
#include <libavformat/avformat.h>
}

class reader {
public:
  virtual ~reader() = default;

  virtual bool open(const std::string& source) = 0;
  virtual void add_sink(media_sink* sink) = 0;
  virtual void remove_sink(media_sink* sink) = 0;
  virtual AVStream* video_stream() const = 0;
  virtual void start() = 0;
  virtual void stop() = 0;
};

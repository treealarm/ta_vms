#pragma once

#include <string>

#include "media/sink_container.h"

extern "C" {
#include <libavformat/avformat.h>
}

class reader : public sink_container {
public:
  virtual ~reader() = default;

  virtual bool open(const std::string& source) = 0;
  virtual AVStream* video_stream() const = 0;
  virtual void start() = 0;
  virtual void stop() = 0;
};

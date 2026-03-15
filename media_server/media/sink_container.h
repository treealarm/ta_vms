#pragma once

#include "media_sink.h"

class sink_container {
public:
  virtual ~sink_container() = default;

  virtual void add_sink(media_sink* sink) = 0;
  virtual void remove_sink(media_sink* sink) = 0;
};


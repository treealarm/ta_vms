#pragma once

#include <memory>

#include "writer/stream_processor.h"

std::unique_ptr<stream_processor> create_processor(const avcodec_parameters_ptr& codec_parameters);

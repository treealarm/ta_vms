#include "writer/stream_processor_factory.h"

#include "writer/audio_copy_processor.h"
#include "writer/audio_transcode_processor.h"
#include "writer/video_copy_processor.h"
#include "writer/video_transcode_processor.h"

std::unique_ptr<stream_processor> create_processor(const avcodec_parameters_ptr& codec_parameters)
{
  if (!codec_parameters) {
    return nullptr;
  }

  if (codec_parameters->codec_type == AVMEDIA_TYPE_VIDEO) {
    if (codec_parameters->codec_id == AV_CODEC_ID_H264) {
      return std::make_unique<video_copy_processor>();
    }

    return std::make_unique<video_transcode_processor>();
  }

  if (codec_parameters->codec_type == AVMEDIA_TYPE_AUDIO) {
    if (codec_parameters->codec_id == AV_CODEC_ID_OPUS) {
      return std::make_unique<audio_copy_processor>();
    }

    return std::make_unique<audio_transcode_processor>();
  }

  return nullptr;
}

#include "writer/webrtc_output_sink.h"

webrtc_output_sink::webrtc_output_sink(std::string rtsp_url)
  : m_rtsp_output_sink(std::move(rtsp_url))
{
  m_video_compat_sink.add_sink(&m_rtsp_output_sink);
  m_audio_compat_sink.add_sink(&m_rtsp_output_sink);
}

bool webrtc_output_sink::init(const avcodec_parameters_ptr& video_codec_parameters,
                              AVRational video_time_base,
                              const avcodec_parameters_ptr& audio_codec_parameters,
                              AVRational audio_time_base)
{
  close();

  if (!video_codec_parameters || !audio_codec_parameters) {
    return false;
  }

  if (!m_video_compat_sink.init(video_codec_parameters, video_time_base)) {
    return false;
  }

  if (!m_audio_compat_sink.init(audio_codec_parameters, audio_time_base)) {
    m_video_compat_sink.close();
    return false;
  }

  std::vector<rtsp_output_sink::stream_config> streams;
  streams.push_back(rtsp_output_sink::stream_config{
    .media_type = AVMEDIA_TYPE_VIDEO,
    .codec_parameters = m_video_compat_sink.output_codec_parameters(),
    .time_base = m_video_compat_sink.output_time_base()
  });
  streams.push_back(rtsp_output_sink::stream_config{
    .media_type = AVMEDIA_TYPE_AUDIO,
    .codec_parameters = m_audio_compat_sink.output_codec_parameters(),
    .time_base = m_audio_compat_sink.output_time_base()
  });

  if (!m_rtsp_output_sink.init(streams)) {
    m_video_compat_sink.close();
    m_audio_compat_sink.close();
    return false;
  }

  m_is_initialized = true;
  return true;
}

void webrtc_output_sink::on_packet(const std::shared_ptr<media_packet>& media)
{
  if (!m_is_initialized || !media) {
    return;
  }

  if (media->media_type == AVMEDIA_TYPE_VIDEO) {
    m_video_compat_sink.on_packet(media);
    return;
  }

  if (media->media_type == AVMEDIA_TYPE_AUDIO) {
    m_audio_compat_sink.on_packet(media);
  }
}

void webrtc_output_sink::close()
{
  m_video_compat_sink.close();
  m_audio_compat_sink.close();
  m_rtsp_output_sink.close();
  m_is_initialized = false;
}

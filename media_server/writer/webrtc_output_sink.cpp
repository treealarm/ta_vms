#include "writer/webrtc_output_sink.h"

webrtc_output_sink::webrtc_output_sink(std::string rtsp_url)
  : m_rtsp_output_sink(std::move(rtsp_url))
{
  m_video_compat_sink.add_sink(&m_rtsp_output_sink);
  m_audio_compat_sink.add_sink(&m_rtsp_output_sink);
}

webrtc_output_sink::~webrtc_output_sink()
{
  close();
}
void webrtc_output_sink::on_packet(const std::shared_ptr<media_packet>& media)
{
  if (!media) {
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
}

#include "writer/webrtc_output_sink.h"

webrtc_output_sink::webrtc_output_sink(std::string rtsp_url)
  : m_rtsp_output_sink(std::move(rtsp_url))
{
  m_compat_sink.add_sink(&m_rtsp_output_sink);
}

bool webrtc_output_sink::init(const avcodec_parameters_ptr& codec_parameters, AVRational time_base)
{
  close();

  if (!codec_parameters) {
    return false;
  }

  if (!m_compat_sink.init(codec_parameters, time_base)) {
    return false;
  }

  if (!m_rtsp_output_sink.init(m_compat_sink.output_codec_parameters(),
                               m_compat_sink.output_time_base())) {
    m_compat_sink.close();
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

  m_compat_sink.on_packet(media);
}

void webrtc_output_sink::close()
{
  m_compat_sink.close();
  m_rtsp_output_sink.close();
  m_is_initialized = false;
}

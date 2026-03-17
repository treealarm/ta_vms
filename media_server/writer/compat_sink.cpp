#include "writer/compat_sink.h"

#include <algorithm>

#include "media/media_packet.h"

compat_sink::~compat_sink()
{
  close();
}

bool compat_sink::init(const avcodec_parameters_ptr& codec_parameters, AVRational time_base)
{
  close();

  m_processor = create_processor(codec_parameters);
  if (!m_processor) {
    return false;
  }

  if (!m_processor->init(codec_parameters, time_base, [this](const std::shared_ptr<media_packet>& media) {
        emit_packet(media);
      })) {
    m_processor.reset();
    return false;
  }

  m_out_codec_parameters = m_processor->output_codec_parameters();
  m_out_time_base = m_processor->output_time_base();
  return true;
}

void compat_sink::add_sink(media_sink* sink)
{
  if (!sink) {
    return;
  }

  std::scoped_lock lock(m_sinks_mutex);
  m_sinks.push_back(sink);
}

void compat_sink::remove_sink(media_sink* sink)
{
  std::scoped_lock lock(m_sinks_mutex);
  m_sinks.erase(std::remove(m_sinks.begin(), m_sinks.end(), sink), m_sinks.end());
}

void compat_sink::on_packet(const std::shared_ptr<media_packet>& media)
{
  if (!media || !media->packet || !m_processor) {
    return;
  }

  m_processor->process(media);
}

void compat_sink::emit_packet(const std::shared_ptr<media_packet>& media)
{
  std::vector<media_sink*> sinks_copy;
  {
    std::scoped_lock lock(m_sinks_mutex);
    sinks_copy = m_sinks;
  }

  for (auto* sink : sinks_copy) {
    if (!sink) {
      continue;
    }

    auto sink_media = clone_media_packet(media);
    if (!sink_media) {
      continue;
    }

    sink->on_packet(sink_media);
  }
}

void compat_sink::close()
{
  if (m_processor) {
    m_processor->flush();
    m_processor.reset();
  }

  m_out_codec_parameters.reset();
  m_out_time_base = AVRational{};
}

const avcodec_parameters_ptr& compat_sink::output_codec_parameters() const
{
  return m_out_codec_parameters;
}

AVRational compat_sink::output_time_base() const
{
  return m_out_time_base;
}

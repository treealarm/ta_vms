#pragma once

#include <memory>
#include <mutex>
#include <vector>

#include "media/sink_container.h"
#include "writer/stream_processor.h"

class compat_sink : public media_sink, public sink_container {
public:
  compat_sink() = default;
  ~compat_sink() override;

  bool init(const avcodec_parameters_ptr& codec_parameters, AVRational time_base);
  void add_sink(media_sink* sink) override;
  void remove_sink(media_sink* sink) override;
  void on_packet(const std::shared_ptr<media_packet>& media) override;
  void close();

  const avcodec_parameters_ptr& output_codec_parameters() const;
  AVRational output_time_base() const;

private:
  std::unique_ptr<stream_processor> create_processor(const avcodec_parameters_ptr& codec_parameters) const;
  void emit_packet(const std::shared_ptr<media_packet>& media);

private:
  bool should_reinitialize(const std::shared_ptr<media_packet>& media) const;

private:
  std::vector<media_sink*> m_sinks;
  mutable std::mutex m_sinks_mutex;
  std::unique_ptr<stream_processor> m_processor;
  avcodec_parameters_ptr m_in_codec_parameters;
  AVRational m_in_time_base{};
  avcodec_parameters_ptr m_out_codec_parameters;
  AVRational m_out_time_base{};
};

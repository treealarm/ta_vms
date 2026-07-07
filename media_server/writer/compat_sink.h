#pragma once

#include <memory>
#include <mutex>
#include <vector>

#include "interfaces/sink_container.h"
#include "writer/processors/stream_processor.h"
#include "sink_container/sink_container_impl.h"

class compat_sink : public media_sink, public sink_container
{
public:
  compat_sink() = default;
  ~compat_sink() override;

  void add_sink(std::shared_ptr<media_sink> sink) override;
  void remove_sink(std::shared_ptr<media_sink> sink) override;
  void on_packet(const std::shared_ptr<media_packet>& media) override;
  void close();

  const avcodec_parameters_ptr& output_codec_parameters() const;
  AVRational output_time_base() const;

private:
  std::unique_ptr<stream_processor> create_processor(const avcodec_parameters_ptr& codec_parameters) const;
  void emit_packet(const std::shared_ptr<media_packet>& media);
  bool init(const avcodec_parameters_ptr& codec_parameters, AVRational time_base, AVRational frame_rate);
  bool should_reinitialize(const std::shared_ptr<media_packet>& media) const;

  mutable std::mutex m_mutex;
  std::unique_ptr<stream_processor> m_processor;
  avcodec_parameters_ptr m_in_codec_parameters;
  AVRational m_in_time_base{};
  avcodec_parameters_ptr m_out_codec_parameters;
  AVRational m_out_time_base{};

  sink_container_impl m_sinks_helper;
};

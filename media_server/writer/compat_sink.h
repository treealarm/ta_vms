#pragma once

#include <memory>
#include <string>
#include <mutex>
#include <vector>

#include "interfaces/sink_container.h"
#include "writer/processors/stream_processor.h"
#include "sink_container/sink_container_impl.h"

class compat_sink : public media_sink, public sink_container
{
public:
  compat_sink() = default;
  // `transcode_hevc` comes from the MediaMTX router this stream publishes to: false passes the
  // camera's H.265 through, which MediaMTX carries over WebRTC.
  explicit compat_sink(bool transcode_hevc) : m_transcode_hevc(transcode_hevc) {}
  ~compat_sink() override;

  void add_sink(std::shared_ptr<media_sink> sink) override;
  void remove_sink(std::shared_ptr<media_sink> sink) override;
  void on_packet(const std::shared_ptr<media_packet>& media) override;
  void close();

  const avcodec_parameters_ptr& output_codec_parameters() const;
  AVRational output_time_base() const;
  /// Which encoder the active processor is using; empty while nothing is initialized.
  std::string video_encoder_name() const;

private:
  // Processors to try, in preference order: the first whose init() succeeds is kept.
  std::vector<std::unique_ptr<stream_processor>> create_processors(const avcodec_parameters_ptr& codec_parameters) const;
  void emit_packet(const std::shared_ptr<media_packet>& media);
  bool init(const avcodec_parameters_ptr& codec_parameters, AVRational time_base, AVRational frame_rate);
  bool should_reinitialize(const std::shared_ptr<media_packet>& media) const;

  mutable std::mutex m_mutex;
  std::unique_ptr<stream_processor> m_processor;
  avcodec_parameters_ptr m_in_codec_parameters;
  AVRational m_in_time_base{};
  avcodec_parameters_ptr m_out_codec_parameters;
  AVRational m_out_time_base{};

  const bool m_transcode_hevc = false;

  sink_container_impl m_sinks_helper;
};

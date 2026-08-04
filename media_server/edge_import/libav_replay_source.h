#pragma once

#include <string>

#include "edge_import/replay_source.h"

/// ONVIF Replay over libavformat's RTSP demuxer.
///
/// The requested window is expressed as a seek offset from the start of the recording, because
/// libav cannot send the ONVIF `Range: clock=` header. Plays at the device's own pace — see
/// replay_source.h for why faster-than-realtime needs a different implementation.
class libav_replay_source : public replay_source
{
public:
  explicit libav_replay_source(replay_source_settings settings);

  [[nodiscard]] bool open(std::string& error) override;
  [[nodiscard]] bool last_failure_was_device_busy() const override { return m_device_busy; }
  [[nodiscard]] bool read(AVPacket* packet, std::string& error) override;
  [[nodiscard]] AVFormatContext* format_context() const override { return m_input.get(); }
  [[nodiscard]] std::chrono::milliseconds packet_offset(const AVPacket* packet) override;

private:
  [[nodiscard]] bool open_input(std::string& error);
  [[nodiscard]] bool seek_to_window(std::string& error);
  [[nodiscard]] std::string decorated_url() const;

  replay_source_settings m_settings;
  avformat_context_ptr m_input = nullptr;
  int64_t m_first_packet_offset_ms = -1;
  bool m_device_busy = false;
};

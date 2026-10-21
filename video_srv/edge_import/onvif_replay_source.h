#pragma once

#include <string>

#include "edge_import/onvif_replay_rtsp.h"
#include "edge_import/replay_source.h"

/// ONVIF Replay through our own RTSP client.
///
/// This is the only implementation that lands on the requested footage. libavformat can express the
/// position solely as `Range: npt=` — an offset from the start of the recording — and cameras answer
/// `200 OK` to it while streaming from the beginning of the card anyway, which no caller can detect:
/// the import then had the right duration and the wrong hour. The ONVIF way is `Range: clock=` with
/// absolute times, and libavformat has no code path that emits it.
class onvif_replay_source : public replay_source
{
public:
  explicit onvif_replay_source(replay_source_settings settings);

  [[nodiscard]] bool open(std::string& error) override;
  [[nodiscard]] bool last_failure_was_device_busy() const override { return m_device_busy; }
  [[nodiscard]] bool read(AVPacket* packet, std::string& error) override;
  [[nodiscard]] AVFormatContext* format_context() const override { return m_client.format_context(); }
  [[nodiscard]] std::chrono::milliseconds packet_offset(const AVPacket* packet) override;

private:
  replay_source_settings m_settings;
  onvif_replay_rtsp m_client;
  bool m_device_busy = false;
  /// The window's start in the camera's clock, which the replay header's times are in.
  std::chrono::system_clock::time_point m_seek_start{};
  int64_t m_first_packet_offset_ms = -1;
};

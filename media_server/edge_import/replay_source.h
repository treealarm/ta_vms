#pragma once

#include <chrono>
#include <memory>
#include <optional>
#include <string>

extern "C" {
#include <libavformat/avformat.h>
}

#include "interfaces/media_packet.h"

/// Where an edge import reads camera-side footage from.
///
/// The libav implementation plays ONVIF Replay at the device's own pace, because libavformat's RTSP
/// demuxer cannot send the `Rate-Control: no` header the ONVIF Replay spec defines for
/// faster-than-realtime pulls (its PLAY only ever carries `Range: npt=`). Measured against a
/// F-IC-2642C2MSZ4: realtime ~530 KiB/s, `Rate-Control: no` ~2150-2600 KiB/s — i.e. roughly 4x, and
/// that is the device's ceiling (`Scale: 8.0` reaches the same number). A hand-rolled RTSP client
/// that can send those headers is the only way to get it, so the read path is kept behind this
/// interface: swapping the implementation must not touch the worker, the remuxer or anything above.
class replay_source
{
public:
  virtual ~replay_source() = default;

  /// Opens the stream and positions it at the start of the requested window.
  /// Returns false with `error` set; the caller treats that as a hard failure rather than
  /// continuing from wherever the device happened to be, which would mis-stamp wall clock.
  [[nodiscard]] virtual bool open(std::string& error) = 0;

  /// Reads the next packet into `packet`. Returns false at end of stream or on error
  /// (`error` empty means a clean end).
  [[nodiscard]] virtual bool read(AVPacket* packet, std::string& error) = 0;

  /// Input streams, needed to build the output muxer. Valid only after a successful open().
  [[nodiscard]] virtual AVFormatContext* format_context() const = 0;

  /// Media offset of a packet relative to the first packet accepted from the device.
  [[nodiscard]] virtual std::chrono::milliseconds packet_offset(const AVPacket* packet) = 0;
};

struct replay_source_settings
{
  std::string replay_uri;
  std::string user;
  std::string password;
  std::chrono::system_clock::time_point start_ts{};
  /// Start of the recording the replay URI addresses; the seek offset is computed from it.
  std::chrono::system_clock::time_point recording_start_ts{};
};

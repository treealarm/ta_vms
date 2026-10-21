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
/// Implemented by onvif_replay_source, which speaks RTSP itself. libavformat cannot: its PLAY only
/// ever carries `Range: npt=` (an offset from the start of the recording, which devices accept and
/// then ignore, streaming from the beginning of the card) and it has no way to send
/// `Require: onvif-replay` or `Rate-Control: no`. The latter is also what makes a pull run faster
/// than realtime — measured against a F-IC-2642C2MSZ4: ~530 KiB/s at realtime against
/// ~2150-2600 KiB/s, i.e. roughly 4x, which is the device's ceiling (`Scale: 8.0` reaches the same
/// number). The read path stays behind this interface so a future implementation does not touch the
/// worker, the remuxer or anything above.
class replay_source
{
public:
  virtual ~replay_source() = default;

  /// Opens the stream and positions it at the start of the requested window.
  /// Returns false with `error` set; the caller treats that as a hard failure rather than
  /// continuing from wherever the device happened to be, which would mis-stamp wall clock.
  [[nodiscard]] virtual bool open(std::string& error) = 0;

  /// True when the last failed open() was the device refusing another RTSP session (it is out of
  /// them, typically because live streaming holds them) rather than a real error. Cameras have very
  /// few sessions to give, so this is worth retrying — see edge_import_manager.
  [[nodiscard]] virtual bool last_failure_was_device_busy() const = 0;

  /// Reads the next packet into `packet`. Returns false at end of stream or on error
  /// (`error` empty means a clean end).
  [[nodiscard]] virtual bool read(AVPacket* packet, std::string& error) = 0;

  /// Input streams, needed to build the output muxer. Valid only after a successful open().
  [[nodiscard]] virtual AVFormatContext* format_context() const = 0;

  /// Where a packet falls in the requested window, from its start. Negative for footage from before
  /// it: a device can only start from a key frame, and starts from the one before the window when the
  /// window's start falls between two.
  [[nodiscard]] virtual std::chrono::milliseconds packet_offset(const AVPacket* packet) = 0;
};

struct replay_source_settings
{
  std::string replay_uri;
  std::string user;
  std::string password;
  /// The window in OUR clock — what imported clips are stamped with.
  std::chrono::system_clock::time_point start_ts{};
  std::chrono::system_clock::time_point end_ts{};
  /// The same window in the CAMERA's clock — what the device is asked for. Unset (epoch) means the
  /// two clocks agree and start_ts/end_ts are used as they are.
  std::chrono::system_clock::time_point seek_start_ts{};
  std::chrono::system_clock::time_point seek_end_ts{};
  /// Start of the recording the replay URI addresses. Diagnostics only since the position is sent
  /// as an absolute clock range rather than as an offset from it.
  std::chrono::system_clock::time_point recording_start_ts{};
};

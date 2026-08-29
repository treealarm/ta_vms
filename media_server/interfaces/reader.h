#pragma once

#include <chrono>
#include <functional>
#include <optional>
#include <string>

#include "interfaces/sink_container.h"

extern "C" {
#include <libavformat/avformat.h>
}

/// Whether the reader is currently receiving media, and when it last did.
///
/// Wall clock, not steady: this leaves the process and becomes the boundary of a recorded outage,
/// so it has to mean an absolute moment. `last_frame_ts` is unset until the very first packet.
struct source_liveness
{
  bool alive = false;
  std::optional<std::chrono::system_clock::time_point> last_frame_ts;
  /// First packet received after the source came back — where the outage ended. Unset until a
  /// recovery has happened at least once.
  std::optional<std::chrono::system_clock::time_point> recovered_frame_ts;
};

/// What the source is actually sending, as opposed to what the configuration says it should.
///
/// The monitor picks a stream per tile from these: a sub-stream is the right thing to decode on a
/// small tile, and only the real frame size can say which stream is which — a camera's resolution
/// can change without anyone editing the configuration.
///
/// Zero width means nothing has been observed yet. `bitrate_bps` is measured over a sliding window
/// rather than taken from the container, which for RTSP usually declares nothing.
struct observed_video
{
  int width = 0;
  int height = 0;
  AVCodecID codec_id = AV_CODEC_ID_NONE;
  /// Nominal, from the container's avg_frame_rate — not a measured arrival rate.
  double fps = 0.0;
  int64_t bitrate_bps = 0;
  std::optional<std::chrono::system_clock::time_point> observed_at;
};

class reader : public sink_container {
public:
  virtual ~reader() = default;

  /// Thread-safe snapshot for the reporting timer, like liveness() below. Readers that never see a
  /// video track report nothing observed, which is what an empty struct means.
  virtual observed_video observed_video_params() const { return {}; }

  /// Thread-safe snapshot for the reporting timer. Readers that cannot lose a source (files) may
  /// report themselves permanently alive.
  /// Not brace-initialised: naming only `alive` leaves the two optionals to their default member
  /// initializers, which is exactly the intent, but gcc reports that as a missing initializer.
  virtual source_liveness liveness() const
  {
    source_liveness state;
    state.alive = true;
    return state;
  }

  /// Called once, from the reader thread, at the moment the source is declared lost — the
  /// alive→dead transition the reader detects in its own watchdog. Lets the owner react then and
  /// there instead of waiting for the next liveness poll, which is seconds away. Must be cheap and
  /// must not block the reader thread. Readers that cannot lose a source ignore it.
  using source_lost_handler = std::function<void()>;
  virtual void set_source_lost_handler(source_lost_handler /*handler*/) {}

  virtual bool open(const std::string& source) = 0;
  virtual AVStream* video_stream() const = 0;
  virtual void start() = 0;
  virtual void stop() = 0;
  virtual void pause() = 0;
  virtual void resume() = 0;
  virtual bool is_running() const = 0;
  virtual void step(int32_t steps) = 0;
  virtual void set_speed(double speed) = 0;
};

enum class player_mode {
  stopped,
  playing,
  paused,
  undefined
};

struct reader_progress
{
  std::chrono::milliseconds duration = std::chrono::milliseconds(0);
  std::chrono::milliseconds position = std::chrono::milliseconds(0);
};


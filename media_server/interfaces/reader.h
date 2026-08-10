#pragma once

#include <chrono>
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

class reader : public sink_container {
public:
  virtual ~reader() = default;

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


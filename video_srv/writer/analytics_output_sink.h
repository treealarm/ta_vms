#pragma once

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <deque>
#include <memory>
#include <mutex>
#include <string>
#include <thread>

#include "interfaces/media_sink.h"
#include "writer/rtsp_output_sink.h"

/// Publishes a camera's video to the detector that watches it, instead of letting the detector
/// read the stream back out of MediaMTX.
///
/// Two readers of one high-bitrate path is what makes the operator's live view stutter. MediaMTX
/// pushes the same keyframe burst — around 1.5 MB, a thousand RTP packets — to both readers in
/// turn, so the browser's share arrives spread over roughly twice as long, misses its 46 ms
/// frame-assembly window, and asks for a keyframe that a file source cannot produce on demand. The
/// picture then holds until the next natural one, seconds later. Measured: packetsLost 0,
/// framesDropped 0, and pliCount climbing all the same.
///
/// Deliberately thinner than webrtc_output_sink, which it is otherwise modelled on:
///
///  - **Video only.** A detector has no use for audio, and dropping it here also skips the
///    AAC-to-Opus transcode that publish does.
///  - **No compat layer.** The elementary stream goes out as it came in; video_a decodes H.264 and
///    HEVC natively, so re-encoding for a consumer that only wants pixels would be pure cost.
class analytics_output_sink : public media_sink {
public:
  /// `sample_fps` is how many frames a second the consumer will actually decode; 0 reads as 1.
  analytics_output_sink(std::string rtsp_url, uint32_t sample_fps);
  ~analytics_output_sink() override;

  void on_packet(const std::shared_ptr<media_packet>& media) override;

private:
  void worker_loop();
  /// Reader-thread only: times a keyframe and decides which mode the measurement calls for.
  void note_keyframe();
  void update_mode();

  std::shared_ptr<rtsp_output_sink> m_rtsp_output_sink;

  std::mutex m_queue_mutex;
  std::condition_variable m_cv;
  std::deque<std::shared_ptr<media_packet>> m_queue;
  std::atomic_bool m_running{ true };
  std::thread m_worker;

  /// Same shape as webrtc_output_sink's: bounded, and on overflow it stops enqueueing until the
  /// next keyframe, which is a clean point to resume from.
  size_t m_max_queue = 90;
  bool m_dropping_until_keyframe = false;  // guarded by m_queue_mutex

  /// What the consumer asked for, as a period.
  std::chrono::milliseconds m_emit_period{ 1000 };

  /// Send only keyframes while they arrive often enough to satisfy that period, everything when
  /// they do not.
  ///
  /// This is the same decision video_a's frame_sampler already makes from the same evidence — it
  /// decodes keyframes only until the stream's own keyframe spacing turns out to be longer than
  /// the sample period. Taken here it saves the traffic as well as the decoding: on a camera
  /// keying once a second at 30 fps that is one packet in thirty rather than thirty.
  ///
  /// Starts keyframe-only, because that is the cheap answer and the common one; a stream that
  /// keys too rarely reveals itself within two keyframes and switches. The median of the last few
  /// gaps decides, not the mean — one reconnect would otherwise strand a well-behaved stream on
  /// the expensive path — with a hysteresis band so a stream keying at exactly the period does not
  /// flip modes on jitter.
  static constexpr size_t k_interval_gaps = 5;
  std::deque<std::chrono::steady_clock::time_point> m_keyframe_times;  // reader thread only
  std::chrono::milliseconds m_keyframe_interval{ 0 };                  // 0 = not measured yet
  bool m_send_all = false;                                             // reader thread only
};

#pragma once

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <deque>
#include <memory>
#include <mutex>
#include <string>
#include <thread>

#include <grpcpp/grpcpp.h>

#include "onvif.grpc.pb.h"

/// Ships reassembled ONVIF metadata documents to this node's VmsOnvif, which parses them and
/// forwards normalized boxes to VmsAnalyticsSingleton for the live overlay.
///
/// VmsOnvif and not the analytics service on purpose: the ONVIF schema knowledge belongs where the
/// rest of it already is, VmsOnvif is node-local (so parsing spreads across nodes instead of
/// converging on a single instance), and media_server already talks to it — see onvif_client — so
/// this adds no new dependency to the C++ core, only another RPC on a channel it already opens.
///
/// Documents are queued and sent under a minimum interval between RPCs rather than a flush window
/// before each one. A document arriving on an idle sender goes out immediately; a busy node
/// coalesces, because whatever accumulates while the sender waits out the remainder of the interval
/// leaves in one call. That bounds the call rate without charging the latency to every document,
/// which the unconditional window that used to sit here did — visibly, on boxes drawn over live
/// video.
///
/// Best-effort by construction. The queue is bounded and drops its oldest entries when the link to
/// the sidecar stalls — the RTSP reader thread must never block on the overlay, and a metadata
/// document that arrives late is worthless anyway (the overlay ages boxes out in seconds).
///
/// Process-wide singleton: one queue and one sender thread serve every camera on this node.
class onvif_metadata_client {
public:
  static onvif_metadata_client& instance();

  /// Reads the required environment and starts the sender thread. Called once from main(); the
  /// environment it needs (DAPR_ONVIF_APP_ID, DAPR_GRPC_PORT) is the same onvif_client already
  /// requires, so this introduces no new startup prerequisite. Throws std::runtime_error when the
  /// environment is incomplete.
  void start();

  void stop();

  /// Queues one reassembled document. Safe from any thread; never blocks.
  ///
  /// `captured_at` is the wall clock of the first packet that carried the document, taken by the
  /// RTSP reader from the same stamping the video packets get. It is passed in rather than taken
  /// here because by this point the document has already been reassembled and is about to be
  /// batched — both of which happen after the moment the camera actually described the scene.
  void enqueue(
    const std::string& camera_id,
    const std::string& stream_name,
    std::string&& xml,
    int frame_width,
    int frame_height,
    std::chrono::system_clock::time_point captured_at);

private:
  onvif_metadata_client() = default;
  ~onvif_metadata_client();

  void send_loop();
  void send_batch(std::deque<vms::onvif::MetadataDocument>& batch);

  std::string m_app_id;
  std::shared_ptr<grpc::Channel> m_channel;
  std::unique_ptr<vms::onvif::OnvifService::Stub> m_stub;

  std::mutex m_mutex;
  std::condition_variable m_cv;
  std::deque<vms::onvif::MetadataDocument> m_queue;
  uint64_t m_dropped = 0;

  /// Sender thread only. Steady clock: this measures a gap, not a moment.
  std::chrono::steady_clock::time_point m_last_send{};

  std::thread m_thread;
  std::atomic_bool m_running{ false };
};

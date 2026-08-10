#pragma once

#include <atomic>
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
/// Documents are queued and flushed as a batch a few times a second rather than sent one per RPC:
/// a chatty camera emits several documents per second, and one unary call each would put a
/// per-frame round trip on a service that is already the fan-in point for every node.
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
  void enqueue(
    const std::string& camera_id,
    const std::string& stream_name,
    std::string&& xml,
    int frame_width,
    int frame_height);

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

  std::thread m_thread;
  std::atomic_bool m_running{ false };
};

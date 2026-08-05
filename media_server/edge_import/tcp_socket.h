#pragma once

#include <chrono>
#include <cstdint>
#include <string>

/// Blocking TCP client socket, POSIX and Winsock behind one interface.
///
/// The RTSP client below needs a raw byte stream, which libav does not offer as a public API: the
/// `tcp://` protocol is only reachable through AVIOContext, and an AVIOContext opened read-write
/// shares one buffer between both directions — mixing avio_read and avio_write on it is not a
/// documented contract. Every other platform dependency in media_server is already hidden behind
/// libav, and this is the one place that has to reach the network itself, so it is kept to this
/// file rather than sprinkled through the protocol code.
class tcp_socket
{
public:
  tcp_socket() = default;
  ~tcp_socket();

  tcp_socket(const tcp_socket&) = delete;
  tcp_socket& operator=(const tcp_socket&) = delete;
  tcp_socket(tcp_socket&& other) noexcept;
  tcp_socket& operator=(tcp_socket&& other) noexcept;

  /// Resolves and connects, trying every address the name yields (so IPv6-only hosts work).
  [[nodiscard]] bool connect(const std::string& host, int port, std::string& error);

  /// Sends the whole buffer. False on any error or a closed peer.
  [[nodiscard]] bool send_all(const void* data, size_t size);

  /// Waits up to `timeout` for readability. False on timeout or error — the caller decides which
  /// of the two matters, because a replay stream falling silent at the end of its range is normal.
  [[nodiscard]] bool wait_readable(std::chrono::milliseconds timeout);

  /// One recv. Returns bytes read, 0 at end of stream, -1 on error.
  [[nodiscard]] int recv_some(void* buffer, size_t size);

  [[nodiscard]] bool is_open() const;
  void close();

private:
#ifdef _WIN32
  // uintptr_t rather than SOCKET so <winsock2.h> stays out of this header — it drags in windows.h
  // and its macros collide with almost everything.
  using handle_type = std::uintptr_t;
#else
  using handle_type = int;
#endif

  static handle_type invalid_handle();

  handle_type m_fd = invalid_handle();
};

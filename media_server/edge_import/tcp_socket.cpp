#include "edge_import/tcp_socket.h"

#include <format>
#include <mutex>
#include <utility>

#ifdef _WIN32
#include <winsock2.h>
#include <ws2tcpip.h>
#else
#include <netdb.h>
#include <poll.h>
#include <sys/socket.h>
#include <sys/types.h>
#include <unistd.h>
#include <cerrno>
#include <cstring>
#endif

namespace
{

#ifdef _WIN32
using raw_socket = SOCKET;
constexpr raw_socket k_invalid = INVALID_SOCKET;

/// Winsock has to be initialised once per process before any socket call.
void ensure_winsock()
{
  static std::once_flag once;
  std::call_once(once, [] {
    WSADATA data{};
    WSAStartup(MAKEWORD(2, 2), &data);
    // Never torn down: the process is exiting anyway, and WSACleanup while another thread still
    // holds a socket is worse than leaking one refcount.
  });
}

std::string last_error_text()
{
  return std::to_string(WSAGetLastError());
}

int poll_one(raw_socket fd, int timeout_ms)
{
  WSAPOLLFD pfd{};
  pfd.fd = fd;
  pfd.events = POLLRDNORM;
  return WSAPoll(&pfd, 1, timeout_ms);
}

void close_socket(raw_socket fd)
{
  ::closesocket(fd);
}

// No SIGPIPE on Windows, so nothing to suppress.
constexpr int k_send_flags = 0;
#else
using raw_socket = int;
constexpr raw_socket k_invalid = -1;

void ensure_winsock() {}

std::string last_error_text()
{
  return std::strerror(errno);
}

int poll_one(raw_socket fd, int timeout_ms)
{
  pollfd pfd{ fd, POLLIN, 0 };
  return ::poll(&pfd, 1, timeout_ms);
}

void close_socket(raw_socket fd)
{
  ::close(fd);
}

// A camera that drops the connection mid-write would otherwise kill the whole process.
constexpr int k_send_flags = MSG_NOSIGNAL;
#endif

}  // namespace

tcp_socket::handle_type tcp_socket::invalid_handle()
{
  return static_cast<handle_type>(k_invalid);
}

tcp_socket::~tcp_socket()
{
  close();
}

tcp_socket::tcp_socket(tcp_socket&& other) noexcept
  : m_fd(std::exchange(other.m_fd, invalid_handle()))
{
}

tcp_socket& tcp_socket::operator=(tcp_socket&& other) noexcept
{
  if (this != &other)
  {
    close();
    m_fd = std::exchange(other.m_fd, invalid_handle());
  }
  return *this;
}

bool tcp_socket::is_open() const
{
  return m_fd != invalid_handle();
}

void tcp_socket::close()
{
  if (is_open())
  {
    close_socket(static_cast<raw_socket>(m_fd));
    m_fd = invalid_handle();
  }
}

bool tcp_socket::connect(const std::string& host, int port, std::string& error)
{
  ensure_winsock();
  close();

  addrinfo hints{};
  hints.ai_socktype = SOCK_STREAM;
  hints.ai_family = AF_UNSPEC;
  hints.ai_protocol = IPPROTO_TCP;

  addrinfo* resolved = nullptr;
  const std::string port_text = std::to_string(port);
  const int rc = ::getaddrinfo(host.c_str(), port_text.c_str(), &hints, &resolved);
  if (rc != 0 || resolved == nullptr)
  {
    error = std::format("resolve '{}': {}", host, ::gai_strerror(rc));
    return false;
  }

  raw_socket fd = k_invalid;
  for (const addrinfo* ai = resolved; ai != nullptr; ai = ai->ai_next)
  {
    fd = ::socket(ai->ai_family, ai->ai_socktype, ai->ai_protocol);
    if (fd == k_invalid)
    {
      continue;
    }
    if (::connect(fd, ai->ai_addr, static_cast<int>(ai->ai_addrlen)) == 0)
    {
      break;
    }
    close_socket(fd);
    fd = k_invalid;
  }
  ::freeaddrinfo(resolved);

  if (fd == k_invalid)
  {
    error = std::format("connect {}:{} failed: {}", host, port, last_error_text());
    return false;
  }

  m_fd = static_cast<handle_type>(fd);
  return true;
}

bool tcp_socket::send_all(const void* data, size_t size)
{
  if (!is_open())
  {
    return false;
  }

  const auto* bytes = static_cast<const char*>(data);
  size_t sent = 0;
  while (sent < size)
  {
#ifdef _WIN32
    const int n = ::send(static_cast<raw_socket>(m_fd), bytes + sent,
                         static_cast<int>(size - sent), k_send_flags);
#else
    const ssize_t n = ::send(static_cast<raw_socket>(m_fd), bytes + sent, size - sent, k_send_flags);
#endif
    if (n <= 0)
    {
      return false;
    }
    sent += static_cast<size_t>(n);
  }
  return true;
}

bool tcp_socket::wait_readable(std::chrono::milliseconds timeout)
{
  if (!is_open())
  {
    return false;
  }
  return poll_one(static_cast<raw_socket>(m_fd), static_cast<int>(timeout.count())) > 0;
}

int tcp_socket::recv_some(void* buffer, size_t size)
{
  if (!is_open())
  {
    return -1;
  }

#ifdef _WIN32
  const int n = ::recv(static_cast<raw_socket>(m_fd), static_cast<char*>(buffer),
                       static_cast<int>(size), 0);
  return n;
#else
  const ssize_t n = ::recv(static_cast<raw_socket>(m_fd), buffer, size, 0);
  return static_cast<int>(n);
#endif
}

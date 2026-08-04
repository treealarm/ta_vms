#include "edge_import/libav_replay_source.h"

#include <algorithm>
#include <format>
#include <string_view>
#include <utility>

extern "C" {
#include <libavutil/error.h>
#include <libavutil/mathematics.h>
}

namespace {

std::string av_error_text(int errnum)
{
  char buf[AV_ERROR_MAX_STRING_SIZE];
  av_strerror(errnum, buf, sizeof(buf));
  if (buf[0] == '\0')
  {
    return std::format("ffmpeg_error={}", errnum);
  }
  return buf;
}

/// RFC 3986 userinfo encoding so passwords with `@`, `:` or `/` do not break the URI.
std::string percent_encode_userinfo(std::string_view value)
{
  static constexpr char hex[] = "0123456789ABCDEF";
  std::string out;
  out.reserve(value.size());
  for (const unsigned char c : value)
  {
    const bool unreserved =
      (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ||
      c == '-' || c == '.' || c == '_' || c == '~';
    if (unreserved)
    {
      out.push_back(static_cast<char>(c));
      continue;
    }
    out.push_back('%');
    out.push_back(hex[c >> 4]);
    out.push_back(hex[c & 0x0F]);
  }
  return out;
}

}  // namespace

libav_replay_source::libav_replay_source(replay_source_settings settings)
  :
  m_settings(std::move(settings))
{
}

bool libav_replay_source::open(std::string& error)
{
  return open_input(error) && seek_to_window(error);
}

std::string libav_replay_source::decorated_url() const
{
  // Credentials belong in the URL because libavformat exposes no RTSP auth options; the
  // device-provided replay URI may already carry them.
  if (m_settings.user.empty() || m_settings.replay_uri.find('@') != std::string::npos)
  {
    return m_settings.replay_uri;
  }
  const auto scheme_end = m_settings.replay_uri.find("://");
  if (scheme_end == std::string::npos)
  {
    return m_settings.replay_uri;
  }
  const auto host_start = scheme_end + 3;
  return m_settings.replay_uri.substr(0, host_start) +
         percent_encode_userinfo(m_settings.user) + ":" +
         percent_encode_userinfo(m_settings.password) + "@" +
         m_settings.replay_uri.substr(host_start);
}

bool libav_replay_source::open_input(std::string& error)
{
  // Do not set initial_pause: with it, find_stream_info often leaves video width/height at 0
  // (SDP-only probe) and avformat_write_header then fails with EINVAL. The seek still repositions
  // into the requested window after the short probe.
  AVDictionary* opts = nullptr;
  av_dict_set(&opts, "rtsp_transport", "tcp", 0);
  av_dict_set(&opts, "timeout", "10000000", 0);

  AVFormatContext* ctx = nullptr;
  const int open_rc = avformat_open_input(&ctx, decorated_url().c_str(), nullptr, &opts);
  av_dict_free(&opts);
  if (open_rc < 0)
  {
    const auto text = av_error_text(open_rc);
    // libavformat collapses every RTSP 4xx into one generic "4XX Client Error" AVERROR, so the
    // status code itself is unavailable here. In practice the only 4xx a well-formed replay request
    // draws is 453 Not Enough Bandwidth — the camera is out of RTSP sessions because live streaming
    // holds them — which is worth retrying rather than failing outright.
    m_device_busy = text.find("4XX Client Error") != std::string::npos;
    error = std::format("open replay uri: {}", text);
    return false;
  }
  m_device_busy = false;
  m_input.reset(ctx);

  const int info_rc = avformat_find_stream_info(m_input.get(), nullptr);
  if (info_rc < 0)
  {
    error = std::format("probe replay stream: {}", av_error_text(info_rc));
    return false;
  }
  return true;
}

bool libav_replay_source::seek_to_window(std::string& error)
{
  const auto offset = m_settings.start_ts - m_settings.recording_start_ts;
  const auto offset_ms = std::chrono::duration_cast<std::chrono::milliseconds>(offset);
  if (offset_ms > std::chrono::milliseconds::zero())
  {
    const int64_t seek_us = offset_ms.count() * 1000;
    const int seek_rc = avformat_seek_file(m_input.get(), -1, INT64_MIN, seek_us, INT64_MAX, 0);
    if (seek_rc < 0)
    {
      // Continuing from the recording start would stamp the wrong wall clock onto the requested
      // window — fail loudly instead of silently mis-labelling footage.
      error = std::format("seek to +{}ms rejected by device: {}",
                          offset_ms.count(), av_error_text(seek_rc));
      return false;
    }
  }

  const int play_rc = av_read_play(m_input.get());
  if (play_rc < 0 && play_rc != AVERROR(ENOSYS))
  {
    error = std::format("start replay: {}", av_error_text(play_rc));
    return false;
  }
  return true;
}

bool libav_replay_source::read(AVPacket* packet, std::string& error)
{
  const int rc = av_read_frame(m_input.get(), packet);
  if (rc == AVERROR_EOF)
  {
    error.clear();
    return false;
  }
  if (rc < 0)
  {
    error = std::format("read replay stream: {}", av_error_text(rc));
    return false;
  }
  return true;
}

std::chrono::milliseconds libav_replay_source::packet_offset(const AVPacket* packet)
{
  if (!packet || !m_input || packet->stream_index < 0 ||
      packet->stream_index >= static_cast<int>(m_input->nb_streams))
  {
    return std::chrono::milliseconds::zero();
  }
  const int64_t ts = packet->pts != AV_NOPTS_VALUE ? packet->pts : packet->dts;
  if (ts == AV_NOPTS_VALUE)
  {
    return std::chrono::milliseconds::zero();
  }
  const AVRational time_base = m_input->streams[packet->stream_index]->time_base;
  const int64_t raw_ms = av_rescale_q(ts, time_base, AVRational{ 1, 1000 });

  // The device restarts presentation timestamps at the seek point, but not always at zero —
  // anchor on the first packet so offsets stay relative to the requested window start.
  if (m_first_packet_offset_ms < 0)
  {
    m_first_packet_offset_ms = raw_ms;
  }
  return std::chrono::milliseconds(std::max<int64_t>(0, raw_ms - m_first_packet_offset_ms));
}

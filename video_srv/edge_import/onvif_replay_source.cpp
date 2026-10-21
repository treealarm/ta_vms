#include "edge_import/onvif_replay_source.h"

#include <algorithm>
#include <utility>

extern "C" {
#include <libavutil/error.h>
#include <libavutil/mathematics.h>
}

onvif_replay_source::onvif_replay_source(replay_source_settings settings)
  : m_settings(std::move(settings))
{
}

bool onvif_replay_source::open(std::string& error)
{
  m_device_busy = false;

  // The window is given to the device in ITS clock, which is why the settings carry both: the
  // archive is stamped from start_ts/end_ts in ours, and a camera whose clock is hours off would
  // otherwise be asked for footage it never recorded. VmsOnvif measures the difference.
  m_seek_start = m_settings.seek_start_ts.time_since_epoch().count() != 0
    ? m_settings.seek_start_ts
    : m_settings.start_ts;
  const auto seek_end = m_settings.seek_end_ts.time_since_epoch().count() != 0
    ? m_settings.seek_end_ts
    : m_settings.end_ts;

  if (!m_client.open(m_settings.replay_uri, m_settings.user, m_settings.password,
                     m_seek_start, seek_end, error))
  {
    // Cameras have very few RTSP sessions; live viewing routinely holds them all, and that is worth
    // retrying rather than failing the job (see edge_import_manager).
    m_device_busy = error.find("453") != std::string::npos;
    return false;
  }

  const AVFormatContext* ctx = m_client.format_context();
  if (!ctx || ctx->nb_streams == 0)
  {
    error = "replay stream carries no tracks";
    m_client.close();
    return false;
  }
  return true;
}

bool onvif_replay_source::read(AVPacket* packet, std::string& error)
{
  const int rc = m_client.read_frame(packet);
  if (rc == AVERROR_EOF)
  {
    error.clear();
    return false;
  }
  if (rc < 0)
  {
    char text[AV_ERROR_MAX_STRING_SIZE]{};
    av_strerror(rc, text, sizeof(text));
    error = text;
    return false;
  }
  return true;
}

std::chrono::milliseconds onvif_replay_source::packet_offset(const AVPacket* packet)
{
  // The camera's own word on when it recorded the packet, where it gives it. Counting from the first
  // packet instead puts that packet at the window's start, which it is not when the camera starts
  // from the key frame before it: that stamped the whole import late by up to a GOP.
  if (packet)
  {
    if (const auto recorded_at = onvif_replay_rtsp::recorded_at(packet))
    {
      return std::chrono::duration_cast<std::chrono::milliseconds>(*recorded_at - m_seek_start);
    }
  }

  // A camera that sends no replay header: since the first packet, which is taken to be the window's
  // start.
  const AVFormatContext* ctx = m_client.format_context();
  if (!packet || !ctx || packet->stream_index < 0 ||
      packet->stream_index >= static_cast<int>(ctx->nb_streams))
  {
    return std::chrono::milliseconds::zero();
  }
  const int64_t ts = packet->pts != AV_NOPTS_VALUE ? packet->pts : packet->dts;
  if (ts == AV_NOPTS_VALUE)
  {
    return std::chrono::milliseconds::zero();
  }

  const AVRational time_base = ctx->streams[packet->stream_index]->time_base;
  const int64_t raw_ms = av_rescale_q(ts, time_base, AVRational{ 1, 1000 });

  // Anchored on the first packet of the session, whichever track it belongs to: both tracks are
  // rebased to their own RTP origin, so this makes the window offset mean "since playback began".
  if (m_first_packet_offset_ms < 0)
  {
    m_first_packet_offset_ms = raw_ms;
  }
  return std::chrono::milliseconds(std::max<int64_t>(0, raw_ms - m_first_packet_offset_ms));
}

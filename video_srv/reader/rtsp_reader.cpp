#include <chrono>
#include <format>
#include <string_view>
#include <thread>
#include "interfaces/av_deleters.h"
#include "rtsp_reader.h"
#include "grpc_layer/onvif_client.h"
#include "logger/logger.h"

namespace {
constexpr auto kWatchdogInterval = std::chrono::seconds(5);
// Between failed reads while the watchdog runs: a failing read can return at once, and FFmpeg logs
// every one, so without a pause the loop spins and fills the log at hundreds of thousands of lines
// a second.
constexpr auto kReadRetryDelay = std::chrono::milliseconds(100);

constexpr std::string_view kRtspPrefix = "rtsp://";

// True if the URL already carries userinfo (user[:pass]@) in its authority,
// e.g. rtsp://user:pass@host/path. In that case the stream provides its own
// credentials and we must not override them with the camera's.
bool rtsp_url_has_credentials(const std::string& url)
{
  if (url.rfind(kRtspPrefix, 0) != 0) return false;

  const std::string_view rest(url.data() + kRtspPrefix.size(),
    url.size() - kRtspPrefix.size());
  const auto at = rest.find('@');
  if (at == std::string_view::npos) return false;

  // userinfo must appear in the authority, before the path/query starts.
  const auto authority_end = rest.find_first_of("/?");
  return authority_end == std::string_view::npos || at < authority_end;
}

std::string build_rtsp_url_with_auth(const std::string& url,
  const std::string& user,
  const std::string& pass)
{
  if (user.empty()) return url;

  if (url.rfind(kRtspPrefix, 0) != 0) return url;

  return std::string(kRtspPrefix) + user + ":" + pass + "@"
    + url.substr(kRtspPrefix.size());
}
}

rtsp_reader::rtsp_reader(
  const std::string& cam_id, const std::string& profile_token,
  const std::string& user, const std::string& pass)
  : m_cam_id(cam_id)
  , m_profile_token(profile_token)
  , m_user(user)
  , m_password(pass)
{

}
rtsp_reader::~rtsp_reader() 
{
  stop();
  disconnect();
}

bool rtsp_reader::open(const std::string& url) 
{
  m_rtsp_url = url;
  
  return true;
}


namespace {
/// How long the bitrate is averaged over. Short enough to follow a real change in the stream,
/// long enough that one keyframe burst does not dominate the answer.
constexpr int64_t k_bitrate_window_us = 5'000'000;
}

observed_video rtsp_reader::observed_video_params() const
{
  observed_video out;
  out.width = m_observed_width.load();
  out.height = m_observed_height.load();
  out.codec_id = static_cast<AVCodecID>(m_observed_codec_id.load());
  out.fps = m_observed_fps.load();
  out.bitrate_bps = m_observed_bitrate_bps.load();
  const auto us = m_observed_at_us.load();
  if (us != 0)
    out.observed_at = std::chrono::system_clock::time_point(std::chrono::microseconds(us));
  return out;
}

void rtsp_reader::note_observed_video(const AVPacket* pkt)
{
  const AVStream* video = m_ctx->streams[m_video_index];
  m_observed_width.store(video->codecpar->width);
  m_observed_height.store(video->codecpar->height);
  m_observed_codec_id.store(video->codecpar->codec_id);
  m_observed_fps.store(video->avg_frame_rate.den > 0
    ? av_q2d(video->avg_frame_rate)
    : 0.0);
  m_observed_at_us.store(std::chrono::duration_cast<std::chrono::microseconds>(
    std::chrono::system_clock::now().time_since_epoch()).count());

  const int64_t now_us = av_gettime_relative();
  if (m_bitrate_window_start_us == 0)
  {
    m_bitrate_window_start_us = now_us;
    m_bitrate_window_bytes = 0;
  }
  m_bitrate_window_bytes += pkt->size;

  const int64_t elapsed_us = now_us - m_bitrate_window_start_us;
  if (elapsed_us >= k_bitrate_window_us)
  {
    m_observed_bitrate_bps.store(m_bitrate_window_bytes * 8 * 1'000'000 / elapsed_us);
    m_bitrate_window_start_us = now_us;
    m_bitrate_window_bytes = 0;
  }
}

source_liveness rtsp_reader::liveness() const
{
  source_liveness out;
  out.alive = m_source_alive.load();
  const auto us = m_last_frame_us.load();
  if (us != 0)
  {
    out.last_frame_ts = std::chrono::system_clock::time_point(std::chrono::microseconds(us));
  }
  const auto recovered_us = m_recovered_frame_us.load();
  if (recovered_us != 0)
  {
    out.recovered_frame_ts =
      std::chrono::system_clock::time_point(std::chrono::microseconds(recovered_us));
  }
  return out;
}

bool rtsp_reader::connect() 
{
  disconnect();
  m_stamped_since_connect = false;

  if (m_rtsp_url.empty())
  {
    onvif_client c_client;

    c_client.get_uri(m_cam_id, m_profile_token, m_rtsp_url);
  }

  AVDictionary* opts = nullptr;
  av_dict_set(&opts, "rtsp_transport", "tcp", 0);
  av_dict_set(&opts, "rtsp_flags", "prefer_tcp", 0);
  av_dict_set(&opts, "timeout", "5000000", 0);
  av_dict_set(&opts, "rw_timeout", "5000000", 0);

  // If the stream URL already embeds credentials, honour them and skip the
  // camera-wide ones; otherwise fall back to the camera's user/password.
  const bool url_has_creds = rtsp_url_has_credentials(m_rtsp_url);

  if (!url_has_creds && !m_user.empty())
  {
    av_dict_set(&opts, "user", m_user.c_str(), 0);
    av_dict_set(&opts, "password", m_password.c_str(), 0);
  }
  AVFormatContext* ctx = avformat_alloc_context();
  if (!ctx)
  {
    av_dict_free(&opts);
    return false;
  }
  // Without it stop() waits out the 5 s socket timeout of whatever FFmpeg is blocked in.
  ctx->interrupt_callback = { &rtsp_reader::interrupted, this };

  std::string final_url = url_has_creds
    ? m_rtsp_url
    : build_rtsp_url_with_auth(m_rtsp_url, m_user, m_password);

  if (avformat_open_input(&ctx, final_url.c_str(), nullptr, &opts) < 0)
  {
    if (m_running)
      logger::instance().error(std::format("Failed to open RTSP [cam={}]: {}", m_cam_id, m_rtsp_url));
    av_dict_free(&opts);
    avformat_close_input(&ctx);
    return false;
  }

  m_ctx.reset(ctx);

  av_dict_free(&opts);

  if (avformat_find_stream_info(m_ctx.get(), nullptr) < 0)
  {
    logger::instance().error(std::format("Failed to get stream info [cam={}]: {}", m_cam_id, m_rtsp_url));
    disconnect();
    return false;
  }

  m_video_index = -1;
  m_metadata_index = -1;
  m_forward_stream_indexes.clear();
  for (unsigned i = 0; i < m_ctx->nb_streams; ++i)
  {
    const AVMediaType media_type = m_ctx->streams[i]->codecpar->codec_type;
    if (media_type == AVMEDIA_TYPE_VIDEO && m_video_index < 0)
    {
      m_video_index = static_cast<int>(i);
    }

    // An "m=application" line in the SDP lands here as AVMEDIA_TYPE_DATA. The only application
    // track ONVIF cameras publish is the metadata one, and the assembler validates the document
    // root anyway, so no attempt is made to match the rtpmap encoding name — libavformat does not
    // surface it for payloads it has no depacketizer for.
    if (media_type == AVMEDIA_TYPE_DATA && m_metadata_index < 0)
    {
      m_metadata_index = static_cast<int>(i);
    }

    if (media_type == AVMEDIA_TYPE_VIDEO || media_type == AVMEDIA_TYPE_AUDIO)
    {
      m_forward_stream_indexes.insert(static_cast<int>(i));
    }
  }

  if (m_video_index < 0)
  {
    logger::instance().error(std::format("No video stream [cam={}]: {}", m_cam_id, m_rtsp_url));
    disconnect();
    return false;
  }

  if (m_metadata_index >= 0)
  {
    logger::instance().info(std::format(
      "ONVIF metadata track found [cam={}] stream_index={}", m_cam_id, m_metadata_index));
  }

  logger::instance().info(std::format("RTSP connected [cam={}]: {}", m_cam_id, m_rtsp_url));
  return true;
}

void rtsp_reader::disconnect() {
  m_video_index = -1;
  m_metadata_index = -1;
  // A half-assembled document from the old session would corrupt the first document of the new
  // one, so the assembler is dropped rather than carried across the reconnect.
  m_metadata_assembler.reset();
  m_forward_stream_indexes.clear();
  m_ctx.reset();
}

void rtsp_reader::add_sink(std::shared_ptr<media_sink> sink) 
{
  m_sinks_helper.add_sink(sink);
}

void rtsp_reader::remove_sink(std::shared_ptr<media_sink> sink) 
{
  m_sinks_helper.remove_sink(sink);
}

AVStream* rtsp_reader::video_stream() const 
{
  if (m_ctx && m_video_index >= 0)
    return m_ctx->streams[m_video_index];
  return nullptr;
}

void rtsp_reader::start() 
{
  if (m_running) 
    return;
  m_running = true;
  m_thread = std::thread([this] { read_loop(); });
}

void rtsp_reader::stop()
{
  {
    // Under the mutex, so a pause_until that has just checked m_running cannot miss the wake.
    std::scoped_lock lock(m_wake_mutex);
    m_running = false;
  }
  m_wake.notify_all();
  if (m_thread.joinable())
    m_thread.join();
}

void rtsp_reader::pause_until(std::chrono::steady_clock::time_point until)
{
  std::unique_lock lock(m_wake_mutex);
  m_wake.wait_until(lock, until, [this] { return !m_running; });
}

int rtsp_reader::interrupted(void* self)
{
  return static_cast<const rtsp_reader*>(self)->m_running ? 0 : 1;
}

void rtsp_reader::read_loop()
{
  auto last_frame_at = std::chrono::steady_clock::now();
  // DIAG: wall-clock gaps between video packets from the source. A gap here
  // means the stall is upstream of every sink (camera, network, av_read_frame).
  std::chrono::steady_clock::time_point last_video_at{};

  while (m_running) {
    if (!m_ctx || m_video_index < 0 || m_forward_stream_indexes.empty())
    {
      if (!connect())
      {
        pause_until(std::chrono::steady_clock::now() + kWatchdogInterval);
        continue;
      }
      last_frame_at = std::chrono::steady_clock::now();
      last_video_at = {};
    }

    avpacket_ptr pkt(av_packet_alloc(), avpacket_deleter{});
    // Taken BEFORE the read, because av_read_frame blocks: a source that dies mid-stream can still
    // deliver one straggler seconds later (measured: 5.1 s of silence, then a packet carrying a
    // frame from before the silence). Stamping that packet with its arrival moves the start of the
    // outage past footage the archive never got. The instant we began waiting is the last moment
    // the source was demonstrably alive, so that is what the frame is stamped with.
    const auto wait_started = std::chrono::system_clock::now();
    const int read_result = av_read_frame(m_ctx.get(), pkt.get());

    if (read_result < 0) 
    {
      // End of file: the server closed the session, nothing more will arrive on it, and every
      // further read fails at once. So rather than reading, wait out the rest of the interval —
      // a closed session and a silent one then reconnect on the same timeline, and a server that
      // accepts a session only to drop it is not reconnected to in a loop either.
      if (read_result == AVERROR_EOF)
        pause_until(last_frame_at + kWatchdogInterval);
      const auto now = std::chrono::steady_clock::now();
      if (now - last_frame_at >= kWatchdogInterval)
      {
        // Dead from here, not from the first failed read: a single error is normal, five seconds
        // of them is a lost source. This only decides *that* it happened — *when* comes from
        // m_last_frame_us, which still holds the last real packet.
        const bool was_alive = m_source_alive.exchange(false);
        logger::instance().warning(std::format("RTSP watchdog: reconnecting [cam={}]: {}", m_cam_id, m_rtsp_url));
        disconnect();
        // Announced at the transition rather than left to the liveness poll: the open clip has to
        // stop here, and until it does, packets from after the reconnect are written into a segment
        // opened for the old session — or dropped outright.
        if (was_alive && m_on_source_lost)
          m_on_source_lost();
      }
      else
      {
        pause_until(std::chrono::steady_clock::now() + kReadRetryDelay);
      }
      continue;
    }

    // Handled before the forward whitelist and before the liveness bookkeeping below, deliberately
    // on both counts: no sink is prepared for a data packet, and metadata must not keep the
    // watchdog happy — a camera that keeps narrating the scene after its video died is still a dead
    // source.
    //
    // It IS stamped, though, and from the same `wait_started` the video packets below use: these
    // boxes are drawn over archived video, and a box is only ever on its object if its timestamp
    // and the frame's come off the same clock. What it deliberately does not do is touch
    // m_stamped_since_connect — the "first packet of a session gets its own arrival" rule belongs
    // to the video timeline, and letting a metadata packet consume it would mis-stamp the frame
    // that actually opens the segment.
    if (m_on_metadata && pkt->stream_index == m_metadata_index)
    {
      const auto meta_wall = m_stamped_since_connect
        ? wait_started
        : std::chrono::system_clock::now();
      if (!m_metadata_assembler)
      {
        m_metadata_assembler = std::make_unique<onvif_metadata_assembler>(
          [this](std::string&& document, std::chrono::system_clock::time_point stamp)
          { m_on_metadata(std::move(document), stamp); });
      }
      m_metadata_assembler->push(pkt->data, static_cast<size_t>(pkt->size), meta_wall);
      continue;
    }

    if (!m_forward_stream_indexes.contains(pkt->stream_index))
      continue;

    last_frame_at = std::chrono::steady_clock::now();
    // Liveness is declared here, not in connect(): that returns true before a single packet has
    // arrived, so "connected but silent" would otherwise read as healthy.
    // Wall clock of this host, deliberately: the camera's own clock is frequently hours off, and
    // these timestamps become archive boundaries. The first packet of a session is the exception:
    // waiting for it started at connect, well before the stream actually began, so it is stamped
    // with its own arrival instead.
    const auto frame_wall = m_stamped_since_connect
      ? wait_started
      : std::chrono::system_clock::now();
    m_stamped_since_connect = true;
    const auto frame_us = std::chrono::duration_cast<std::chrono::microseconds>(
      frame_wall.time_since_epoch()).count();
    m_last_frame_us.store(frame_us);
    if (!m_source_alive.exchange(true))
    {
      // The transition, so this is the first frame after the silence — the instant the gap closed.
      m_recovered_frame_us.store(frame_us);
    }

    if (pkt->stream_index == m_video_index)
    {
      const auto now = std::chrono::steady_clock::now();
      if (last_video_at != std::chrono::steady_clock::time_point{})
      {
        const auto gap_ms = std::chrono::duration_cast<std::chrono::milliseconds>(
          now - last_video_at).count();
        // Cameras with an overloaded encoder legitimately pace frames unevenly
        // (~75-300ms observed); only a second-long hole is a real delivery stall.
        if (gap_ms > 1000)
          logger::instance().warning(std::format(
            "DIAG rtsp_reader: video source gap={}ms key={} [cam={}]",
            gap_ms, (int)((pkt->flags & AV_PKT_FLAG_KEY) != 0), m_cam_id));
      }
      last_video_at = now;
      note_observed_video(pkt.get());
    }

    auto media = std::make_shared<media_packet>();
    media->packet = pkt;
    media->wall_ts = frame_wall;
    media->arrival_us = av_gettime_relative();
    media->stream_index = pkt->stream_index;
    media->time_base = m_ctx->streams[pkt->stream_index]->time_base;
    media->media_type = m_ctx->streams[pkt->stream_index]->codecpar->codec_type;
    media->frame_rate = m_ctx->streams[pkt->stream_index]->avg_frame_rate;
    media->sample_rate = m_ctx->streams[pkt->stream_index]->codecpar->sample_rate;
    media->nb_samples = 0;
    avcodec_parameters_ptr codec_copy(avcodec_parameters_alloc(),
      [](AVCodecParameters* params) { avcodec_parameters_free(&params); });
    if (!codec_copy) {
      continue;
    }

    if (avcodec_parameters_copy(codec_copy.get(), m_ctx->streams[pkt->stream_index]->codecpar) < 0)
    {
      continue;
    }

    media->codec_parameters = std::move(codec_copy);

    for (auto& s : m_sinks_helper.snapshot_sinks()) 
    {
      s->on_packet(media);
    }
  }

  disconnect();
}

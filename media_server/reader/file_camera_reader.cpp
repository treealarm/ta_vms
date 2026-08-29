#include "file_camera_reader.h"

#include <algorithm>
#include <cctype>
#include <chrono>
#include <format>
#include <thread>

#include "interfaces/av_deleters.h"
#include "logger/logger.h"

extern "C" {
#include <libavutil/avutil.h>
#include <libavutil/time.h>
}

namespace {
constexpr auto kReadErrorBackoff = std::chrono::milliseconds(100);
constexpr auto kUnavailableLogInterval = std::chrono::seconds(10);
constexpr auto kConnectBackoffMin = std::chrono::milliseconds(1000);
constexpr auto kConnectBackoffMax = std::chrono::seconds(30);
constexpr int kKeyframeProbeLimit = 512;

avcodec_parameters_ptr copy_codec_parameters(const AVCodecParameters* source)
{
  avcodec_parameters_ptr params(avcodec_parameters_alloc(),
    [](AVCodecParameters* p) { avcodec_parameters_free(&p); });
  if (!params)
    return nullptr;

  if (avcodec_parameters_copy(params.get(), source) < 0)
    return nullptr;

  return params;
}

std::string ffmpeg_error_to_string(int error_code)
{
  char buf[AV_ERROR_MAX_STRING_SIZE] = { 0 };
  av_strerror(error_code, buf, sizeof(buf));
  return buf;
}

std::string trim_copy(const std::string& value)
{
  const auto start = value.find_first_not_of(" \t\r\n");
  if (start == std::string::npos)
    return {};
  const auto end = value.find_last_not_of(" \t\r\n");
  return value.substr(start, end - start + 1);
}

bool starts_with_ci(const std::string& value, const std::string& prefix)
{
  if (value.size() < prefix.size())
    return false;

  for (size_t i = 0; i < prefix.size(); ++i)
  {
    if (std::tolower(static_cast<unsigned char>(value[i])) !=
        std::tolower(static_cast<unsigned char>(prefix[i])))
      return false;
  }
  return true;
}

bool is_rtsp_source(const std::string& source)
{
  return starts_with_ci(source, "rtsp://") || starts_with_ci(source, "rtsps://");
}

std::string normalize_local_path_str(const std::string& source)
{
  const std::string file_scheme = "file://";
  if (starts_with_ci(source, file_scheme))
    return source.substr(file_scheme.size());
  return source;
}
} // namespace

file_camera_reader::file_camera_reader(const std::string& cam_id, const std::string& stream_name)
  : m_cam_id(cam_id), m_stream_name(stream_name)
{
}

file_camera_reader::~file_camera_reader()
{
  stop();
  disconnect();
}

bool file_camera_reader::is_file_source(const std::string& source)
{
  const std::string trimmed = trim_copy(source);
  if (trimmed.empty() || is_rtsp_source(trimmed))
    return false;

  const bool has_scheme = trimmed.find("://") != std::string::npos;
  if (has_scheme && !starts_with_ci(trimmed, "file://"))
    return false;

  const std::string path = normalize_local_path_str(trimmed);
  if (path.empty() || path.find("://") != std::string::npos)
    return false;

  std::error_code fs_error;
  if (!std::filesystem::exists(path, fs_error))
    return false;

  return std::filesystem::is_regular_file(path, fs_error)
    || std::filesystem::is_directory(path, fs_error);
}

bool file_camera_reader::open(const std::string& source)
{
  m_source = source;
  return !m_source.empty();
}

void file_camera_reader::add_sink(std::shared_ptr<media_sink> sink)
{
  m_sinks_helper.add_sink(sink);
}

void file_camera_reader::remove_sink(std::shared_ptr<media_sink> sink)
{
  m_sinks_helper.remove_sink(sink);
}

AVStream* file_camera_reader::video_stream() const
{
  if (m_ctx && m_video_index >= 0)
    return m_ctx->streams[m_video_index];
  return nullptr;
}

void file_camera_reader::start()
{
  if (m_running)
    return;
  m_running = true;
  m_thread = std::thread([this] { read_loop(); });
}

void file_camera_reader::stop()
{
  m_running = false;
  if (m_thread.joinable())
    m_thread.join();
  reset_source_gate();
  reset_continuity();
}

void file_camera_reader::read_loop()
{
  bool playback_clock_initialized = false;
  int64_t playback_base_ts_us = 0;
  std::chrono::steady_clock::time_point playback_base_wall{};

  while (m_running)
  {
    if (!m_ctx || m_video_index < 0 || m_forward_stream_indexes.empty())
    {
      if (!connect())
      {
        std::this_thread::sleep_for(m_connect_backoff);
        continue;
      }
      playback_clock_initialized = false;
    }

    avpacket_ptr pkt(av_packet_alloc(), avpacket_deleter{});
    const int read_result = av_read_frame(m_ctx.get(), pkt.get());

    if (read_result == AVERROR_EOF)
    {
      if (m_playlist.size() > 1)
      {
        advance_playlist_index();
        disconnect();
        playback_clock_initialized = false;
        continue;
      }

      if (restart_current_file())
      {
        playback_clock_initialized = false;
        continue;
      }

      disconnect();
      playback_clock_initialized = false;
      continue;
    }

    if (read_result < 0)
    {
      const std::string source_path = current_source_display();
      const std::string error_text = ffmpeg_error_to_string(read_result);
      if (m_playlist.size() > 1)
      {
        logger::instance().warning(std::format(
          "File source read failed, skipping file [cam={}] stream={} source={} error={}",
          m_cam_id, m_stream_name, source_path, error_text));
        advance_playlist_index();
      }
      else
      {
        note_unavailable("read.error",
          std::format("Failed reading file packet: source={} error={}", source_path, error_text));
      }
      disconnect();
      playback_clock_initialized = false;
      if (m_playlist.size() > 1)
        continue;
      std::this_thread::sleep_for(kReadErrorBackoff);
      continue;
    }

    if (!m_forward_stream_indexes.contains(pkt->stream_index))
      continue;

    if (!pass_source_keyframe_gate(pkt.get(), pkt->stream_index))
      continue;

    remux_continuous_timestamps(pkt.get(), pkt->stream_index);

    note_recovered();

    auto media = build_media_packet(std::move(pkt));
    if (!media)
      continue;

    apply_playback_pacing(media, playback_clock_initialized, playback_base_ts_us, playback_base_wall);

    for (auto& sink : m_sinks_helper.snapshot_sinks())
    {
      sink->on_packet(media);
    }
  }

  disconnect();
}

std::shared_ptr<media_packet> file_camera_reader::build_media_packet(avpacket_ptr pkt)
{
  if (!m_ctx || !pkt)
    return nullptr;

  const int stream_index = pkt->stream_index;
  note_observed_video(stream_index, pkt->size);
  auto media = std::make_shared<media_packet>();
  media->packet = std::move(pkt);
  media->stream_index = stream_index;
  media->time_base = m_ctx->streams[stream_index]->time_base;
  media->media_type = m_ctx->streams[stream_index]->codecpar->codec_type;
  media->frame_rate = m_ctx->streams[stream_index]->avg_frame_rate;
  media->sample_rate = m_ctx->streams[stream_index]->codecpar->sample_rate;
  media->nb_samples = 0;

  auto cached_codec = m_stream_codec_parameters.find(stream_index);
  if (cached_codec != m_stream_codec_parameters.end() && cached_codec->second)
  {
    media->codec_parameters = cached_codec->second;
    return media;
  }

  auto codec_copy = copy_codec_parameters(m_ctx->streams[stream_index]->codecpar);
  if (!codec_copy)
    return nullptr;
  media->codec_parameters = std::move(codec_copy);
  return media;
}

namespace {
/// Same window as rtsp_reader uses: long enough that a keyframe burst does not dominate.
constexpr int64_t k_bitrate_window_us = 5'000'000;
}

observed_video file_camera_reader::observed_video_params() const
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

void file_camera_reader::note_observed_video(int stream_index, int packet_size)
{
  if (!m_ctx || stream_index != m_video_index)
    return;

  const AVStream* video = m_ctx->streams[stream_index];
  m_observed_width.store(video->codecpar->width);
  m_observed_height.store(video->codecpar->height);
  m_observed_codec_id.store(video->codecpar->codec_id);
  m_observed_fps.store(video->avg_frame_rate.den > 0 ? av_q2d(video->avg_frame_rate) : 0.0);
  m_observed_at_us.store(std::chrono::duration_cast<std::chrono::microseconds>(
    std::chrono::system_clock::now().time_since_epoch()).count());

  const int64_t now_us = av_gettime_relative();
  if (m_bitrate_window_start_us == 0)
  {
    m_bitrate_window_start_us = now_us;
    m_bitrate_window_bytes = 0;
  }
  m_bitrate_window_bytes += packet_size;

  const int64_t elapsed_us = now_us - m_bitrate_window_start_us;
  if (elapsed_us >= k_bitrate_window_us)
  {
    m_observed_bitrate_bps.store(m_bitrate_window_bytes * 8 * 1'000'000 / elapsed_us);
    m_bitrate_window_start_us = now_us;
    m_bitrate_window_bytes = 0;
  }
}

void file_camera_reader::apply_playback_pacing(
  const std::shared_ptr<media_packet>& media,
  bool& playback_clock_initialized,
  int64_t& playback_base_ts_us,
  std::chrono::steady_clock::time_point& playback_base_wall) const
{
  if (!media || !media->packet || !m_ctx)
    return;

  AVPacket* const pkt = media->packet.get();
  AVStream* const in_stream = m_ctx->streams[media->stream_index];
  const int64_t packet_ts = pkt->dts != AV_NOPTS_VALUE ? pkt->dts : pkt->pts;
  if (packet_ts == AV_NOPTS_VALUE)
    return;

  const int64_t packet_ts_us =
    av_rescale_q(packet_ts, in_stream->time_base, AVRational{ 1, AV_TIME_BASE });
  if (!playback_clock_initialized)
  {
    playback_clock_initialized = true;
    playback_base_ts_us = packet_ts_us;
    playback_base_wall = std::chrono::steady_clock::now();
    return;
  }

  if (packet_ts_us < playback_base_ts_us)
    return;

  const auto elapsed_us = std::chrono::microseconds(packet_ts_us - playback_base_ts_us);
  const auto target_time = playback_base_wall + elapsed_us;
  const auto now = std::chrono::steady_clock::now();
  if (target_time > now)
    std::this_thread::sleep_for(target_time - now);
}

bool file_camera_reader::connect()
{
  disconnect();

  std::string refresh_error;
  if (!refresh_playlist(refresh_error))
  {
    increase_connect_backoff("source");
    note_unavailable("connect.source", refresh_error);
    return false;
  }

  if (m_playlist_index >= m_playlist.size())
    m_playlist_index = 0;

  const size_t attempts = m_playlist.size();
  for (size_t attempt = 0; attempt < attempts; ++attempt)
  {
    const std::string source_path = m_playlist[m_playlist_index].string();

    AVFormatContext* ctx = nullptr;
    const int open_result = avformat_open_input(&ctx, source_path.c_str(), nullptr, nullptr);
    if (open_result < 0)
    {
      logger::instance().warning(std::format(
        "File source open failed, skipping file [cam={}] stream={} source={} error={}",
        m_cam_id, m_stream_name, source_path, ffmpeg_error_to_string(open_result)));
      advance_playlist_index();
      avformat_close_input(&ctx);
      continue;
    }

    m_ctx.reset(ctx);

    const int stream_info_result = avformat_find_stream_info(m_ctx.get(), nullptr);
    if (stream_info_result < 0)
    {
      logger::instance().warning(std::format(
        "File source stream info failed, skipping file [cam={}] stream={} source={} error={}",
        m_cam_id, m_stream_name, source_path, ffmpeg_error_to_string(stream_info_result)));
      disconnect();
      advance_playlist_index();
      continue;
    }

    m_video_index = -1;
    m_forward_stream_indexes.clear();
    m_stream_codec_parameters.clear();

    for (unsigned i = 0; i < m_ctx->nb_streams; ++i)
    {
      const AVMediaType media_type = m_ctx->streams[i]->codecpar->codec_type;
      if (media_type == AVMEDIA_TYPE_VIDEO && m_video_index < 0)
        m_video_index = static_cast<int>(i);

      if (media_type == AVMEDIA_TYPE_VIDEO || media_type == AVMEDIA_TYPE_AUDIO)
      {
        const int stream_index = static_cast<int>(i);
        m_forward_stream_indexes.insert(stream_index);

        auto codec_parameters = copy_codec_parameters(m_ctx->streams[i]->codecpar);
        if (!codec_parameters)
        {
          logger::instance().warning(
            "Failed to cache codec parameters for file source packet path, will copy lazily");
          continue;
        }
        m_stream_codec_parameters.emplace(stream_index, std::move(codec_parameters));
      }
    }

    if (m_video_index < 0)
    {
      logger::instance().warning(std::format(
        "File source has no video stream, skipping file [cam={}] stream={} source={}",
        m_cam_id, m_stream_name, source_path));
      disconnect();
      advance_playlist_index();
      continue;
    }

    if (!validate_source_has_keyframe())
    {
      logger::instance().warning(std::format(
        "File source has no decodable keyframe, skipping file [cam={}] stream={} source={}",
        m_cam_id, m_stream_name, source_path));
      disconnect();
      advance_playlist_index();
      continue;
    }

    reset_connect_backoff();
    begin_new_source_file();
    note_recovered();
    logger::instance().info(std::format(
      "File source connected [cam={}] stream={} source={}", m_cam_id, m_stream_name, source_path));
    return true;
  }

  increase_connect_backoff("connect.exhausted");
  note_unavailable("connect.exhausted", "All files in playlist failed to open");
  return false;
}

void file_camera_reader::disconnect()
{
  m_video_index = -1;
  m_forward_stream_indexes.clear();
  m_stream_codec_parameters.clear();
  m_ctx.reset();
}

std::filesystem::path file_camera_reader::normalize_source_path(const std::string& source)
{
  const std::string file_scheme = "file://";
  if (source.rfind(file_scheme, 0) == 0)
    return std::filesystem::path(source.substr(file_scheme.size()));
  return std::filesystem::path(source);
}

bool file_camera_reader::refresh_playlist(std::string& error_detail)
{
  const std::filesystem::path path = normalize_source_path(m_source);
  if (path.empty())
  {
    error_detail = "Source path is empty";
    return false;
  }

  std::error_code fs_error;
  std::vector<std::filesystem::path> resolved;
  if (!std::filesystem::exists(path, fs_error))
  {
    error_detail = std::format("Source path does not exist: {}", path.string());
    return false;
  }

  if (std::filesystem::is_regular_file(path, fs_error))
  {
    resolved.push_back(path);
  }
  else if (std::filesystem::is_directory(path, fs_error))
  {
    std::error_code iter_error;
    std::filesystem::directory_iterator iter(
      path, std::filesystem::directory_options::skip_permission_denied, iter_error);
    std::filesystem::directory_iterator end;
    for (; !iter_error && iter != end; iter.increment(iter_error))
    {
      std::error_code entry_error;
      if (iter->is_regular_file(entry_error))
        resolved.push_back(iter->path());
    }
    if (iter_error)
    {
      error_detail = std::format("Failed reading source directory: {}", path.string());
      return false;
    }
    std::sort(resolved.begin(), resolved.end(), [](const auto& left, const auto& right) {
      return left.filename().string() < right.filename().string();
    });
  }
  else
  {
    error_detail = std::format("Source path must be a file or directory: {}", path.string());
    return false;
  }

  if (resolved.empty())
  {
    error_detail = std::format("No playable files in source path: {}", path.string());
    return false;
  }

  std::filesystem::path current;
  if (!m_playlist.empty() && m_playlist_index < m_playlist.size())
    current = m_playlist[m_playlist_index];

  m_playlist = std::move(resolved);
  if (!current.empty())
  {
    const auto it = std::find(m_playlist.begin(), m_playlist.end(), current);
    if (it != m_playlist.end())
    {
      m_playlist_index = static_cast<size_t>(std::distance(m_playlist.begin(), it));
      return true;
    }
  }
  if (m_playlist_index >= m_playlist.size())
    m_playlist_index = 0;
  return true;
}

void file_camera_reader::advance_playlist_index()
{
  if (m_playlist.empty())
    return;
  m_playlist_index = (m_playlist_index + 1) % m_playlist.size();
}

std::string file_camera_reader::current_source_display() const
{
  if (m_playlist.empty() || m_playlist_index >= m_playlist.size())
    return {};
  return m_playlist[m_playlist_index].string();
}

void file_camera_reader::note_unavailable(const std::string& error_class, const std::string& detail)
{
  const auto now = std::chrono::steady_clock::now();
  if (!m_unavailable)
  {
    m_unavailable = true;
    m_unavailable_since = now;
    m_next_unavailable_log = now + kUnavailableLogInterval;
    m_unavailable_error_class = error_class;
    m_unavailable_suppressed_events = 0;

    logger::instance().warning(std::format(
      "File stream unavailable [cam={}] stream={} class={} detail={}",
      m_cam_id, m_stream_name, error_class, detail));
    return;
  }

  if (m_unavailable_error_class != error_class)
    m_unavailable_error_class = error_class;

  ++m_unavailable_suppressed_events;
  if (now < m_next_unavailable_log)
    return;

  const auto unavailable_for_sec =
    std::chrono::duration_cast<std::chrono::seconds>(now - m_unavailable_since).count();
  logger::instance().warning(std::format(
    "File stream still unavailable [cam={}] stream={} class={} unavailable_sec={} suppressed_events={} source={}",
    m_cam_id, m_stream_name, m_unavailable_error_class, unavailable_for_sec,
    m_unavailable_suppressed_events, current_source_display()));

  m_unavailable_suppressed_events = 0;
  m_next_unavailable_log = now + kUnavailableLogInterval;
}

void file_camera_reader::note_recovered()
{
  if (!m_unavailable)
    return;

  const auto now = std::chrono::steady_clock::now();
  const auto unavailable_for_sec =
    std::chrono::duration_cast<std::chrono::seconds>(now - m_unavailable_since).count();
  logger::instance().info(std::format(
    "File stream recovered [cam={}] stream={} unavailable_sec={} suppressed_events={} source={}",
    m_cam_id, m_stream_name, unavailable_for_sec, m_unavailable_suppressed_events,
    current_source_display()));

  m_unavailable = false;
  m_unavailable_error_class.clear();
  m_unavailable_suppressed_events = 0;
}

void file_camera_reader::reset_connect_backoff()
{
  m_connect_backoff = kConnectBackoffMin;
  m_last_connect_error_class.clear();
}

void file_camera_reader::increase_connect_backoff(const std::string& error_class)
{
  if (m_last_connect_error_class == error_class)
  {
    auto next = m_connect_backoff * 2;
    if (next > kConnectBackoffMax)
      next = kConnectBackoffMax;
    m_connect_backoff = next;
  }
  else
  {
    m_connect_backoff = kConnectBackoffMin;
  }
  m_last_connect_error_class = error_class;
}

void file_camera_reader::begin_new_source_file()
{
  m_wait_source_keyframe = true;
  ++m_file_generation;
}

bool file_camera_reader::restart_current_file()
{
  if (!m_ctx || m_video_index < 0)
    return false;

  if (avformat_seek_file(m_ctx.get(), -1, 0, 0, 0, AVSEEK_FLAG_BACKWARD) < 0)
  {
    logger::instance().warning(std::format(
      "File source loop seek failed [cam={}] stream={} source={}",
      m_cam_id, m_stream_name, current_source_display()));
    return false;
  }

  avformat_flush(m_ctx.get());
  begin_new_source_file();
  // Deliberately NOT restarting the output timeline at zero. A loop is invisible to everything
  // downstream only if time keeps going forward: begin_new_source_file() above bumps the
  // generation, and remux_continuous_timestamps then anchors the new pass one step after the last
  // packet of the previous one.
  //
  // Resetting instead is what this used to do, and it destroyed every archive segment that
  // spanned a loop. segment_writer rebases each packet on the segment's own first timestamp, so a
  // packet from the new pass came out negative, tripped the monotonicity guard, and was rewritten
  // as "previous + 1 tick" -- along with every packet after it, because none of them ever climbed
  // back above the guard. Measured on a 62 s clip: 1861 frames, all present, packed into 1.12
  // seconds of timeline. The live view showed the same thing as a freeze followed by a jump,
  // because time had stopped advancing there too.
  return true;
}

void file_camera_reader::reset_source_gate()
{
  m_wait_source_keyframe = true;
}

void file_camera_reader::reset_continuity()
{
  m_continuity.clear();
  m_file_generation = 0;
}

void file_camera_reader::remux_continuous_timestamps(AVPacket* pkt, int stream_index)
{
  if (!pkt)
    return;

  auto& st = m_continuity[stream_index];
  const int64_t raw_pts = pkt->pts;
  const int64_t raw_dts = pkt->dts != AV_NOPTS_VALUE ? pkt->dts : pkt->pts;
  const int64_t step = pkt->duration > 0 ? pkt->duration : 1;

  // Recompute the additive offset once per source file so the output timeline
  // continues right after the previous file instead of jumping back to ~0.
  if (st.generation != m_file_generation)
  {
    if (st.last_out_dts == AV_NOPTS_VALUE)
      st.offset = raw_dts != AV_NOPTS_VALUE ? -raw_dts : 0;
    else if (raw_dts != AV_NOPTS_VALUE)
      st.offset = (st.last_out_dts + step) - raw_dts;
    st.generation = m_file_generation;
  }

  if (raw_pts != AV_NOPTS_VALUE)
  {
    pkt->pts = raw_pts + st.offset;
    st.last_out_pts = pkt->pts;
  }
  if (pkt->dts != AV_NOPTS_VALUE)
  {
    pkt->dts = pkt->dts + st.offset;
    st.last_out_dts = pkt->dts;
  }
}

bool file_camera_reader::validate_source_has_keyframe()
{
  if (!m_ctx || m_video_index < 0)
    return false;

  if (av_seek_frame(m_ctx.get(), -1, 0, AVSEEK_FLAG_BACKWARD) < 0)
    return false;

  for (int attempt = 0; attempt < kKeyframeProbeLimit; ++attempt)
  {
    avpacket_ptr pkt(av_packet_alloc(), avpacket_deleter{});
    const int read_result = av_read_frame(m_ctx.get(), pkt.get());
    if (read_result < 0)
      return false;
    if (pkt->stream_index == m_video_index && (pkt->flags & AV_PKT_FLAG_KEY))
    {
      av_seek_frame(m_ctx.get(), -1, 0, AVSEEK_FLAG_BACKWARD);
      return true;
    }
  }

  return false;
}

bool file_camera_reader::pass_source_keyframe_gate(AVPacket* pkt, int stream_index)
{
  if (!pkt || !m_wait_source_keyframe)
    return true;
  if (stream_index != m_video_index)
    return false;
  if (!(pkt->flags & AV_PKT_FLAG_KEY))
    return false;
  m_wait_source_keyframe = false;
  return true;
}

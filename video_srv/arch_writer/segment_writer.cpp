#include "segment_writer.h"

#include <cstring>
#include <format>
#include "file_system/recording_storage.h"
#include <optional>
#include <system_error>

#include "grpc_layer/fs_client.h"
#include "logger/logger.h"

namespace {

/// Whether a stream's encoding has changed in a way the open container cannot absorb. Its output
/// streams were created from the parameters seen when the segment was opened, so anything that
/// alters the decoder configuration — a different codec, resolution, or a new SPS/PPS after the
/// camera restarted its encoder — has to go into a new file.
bool codec_parameters_differ(const AVCodecParameters& a, const AVCodecParameters& b)
{
  if (a.codec_id != b.codec_id || a.codec_type != b.codec_type
    || a.width != b.width || a.height != b.height
    || a.format != b.format || a.sample_rate != b.sample_rate
    || a.extradata_size != b.extradata_size)
    return true;

  if (a.extradata_size <= 0)
    return false;

  if (!a.extradata || !b.extradata)
    return true;

  return std::memcmp(a.extradata, b.extradata, static_cast<std::size_t>(a.extradata_size)) != 0;
}

}  // namespace

segment_writer::segment_writer(
  const std::string& camera_id,
  const std::string& stream_name)
  :
  m_camera_id(camera_id),
  m_stream_name(stream_name),
  m_segment_duration(std::chrono::seconds(60)),
  m_packet_transcoder([]
    {
      sync_packet_transcoder::codec_config config;
      config.video_codec = AV_CODEC_ID_H264;
      config.allow_hevc_video_passthrough = true;
      config.audio_codec = AV_CODEC_ID_AAC;
      config.allow_all_audio_passthrough = false;
      return config;
    }())
{
  // Lifetime of *this is owned by cam_stream (one segment_writer per stream, for its
  // whole lifetime), so capturing a raw `this` is safe: the destructor joins the
  // thread before the object is destroyed.
  m_writer_thread = std::thread([this] { process_queue(); });
}

void segment_writer::set_segment_duration(std::chrono::seconds new_segment_duration)
{
  if (new_segment_duration.count() > 0)
    m_segment_duration.store(new_segment_duration);
}

void segment_writer::request_close_segment(std::chrono::seconds new_segment_duration)
{
  set_segment_duration(new_segment_duration);

  {
    std::lock_guard lock(m_queue_mutex);
    m_queue.push(nullptr);
  }
  m_queue_cv.notify_one();
}

void segment_writer::stop_async()
{
  std::lock_guard lock(m_queue_mutex);
  m_stopping = true;
  m_queue_cv.notify_one();
}

segment_writer::~segment_writer()
{
  stop_async();
  m_writer_thread.join();
}

bool segment_writer::ensure_input_stream(const std::shared_ptr<media_packet>& media)
{
  if (!media || !media->codec_parameters || media->stream_index < 0)
  {
    return false;
  }

  if (auto it = m_input_streams.find(media->stream_index); it != m_input_streams.end())
  {
    const bool same = it->second.codec_parameters
      && !codec_parameters_differ(*it->second.codec_parameters, *media->codec_parameters)
      && it->second.time_base.num == media->time_base.num
      && it->second.time_base.den == media->time_base.den;
    if (same)
      return true;

    // Re-registered rather than refused. The packets arriving after a camera reconnects can carry a
    // fresh encoder configuration, and the old behaviour kept the stale one — every one of them was
    // written into a container built for the previous session or rejected outright, which is
    // archive silently lost for as long as the segment stayed open.
    //
    // Every stream is forgotten, not just this one: a reconnect usually re-announces video and
    // audio together, and dropping them one at a time closes the segment once per stream. The
    // second close lands on a file the first packet has only just opened, leaving a sub-second clip
    // and an ingest row for it. One change means one new session.
    logger::instance().info(std::format(
      "segment_writer: stream {} changed its encoding — starting a new segment [cam={}/{}]",
      media->stream_index, m_camera_id, m_stream_name));
    close_segment();
    m_input_streams.clear();
  }

  avcodec_parameters_ptr codec_copy(avcodec_parameters_alloc(),
    [](AVCodecParameters* params) { avcodec_parameters_free(&params); });
  if (!codec_copy)
  {
    return false;
  }

  if (avcodec_parameters_copy(codec_copy.get(), media->codec_parameters.get()) < 0) {
    return false;
  }

  m_input_streams.emplace(media->stream_index,
                          input_stream_info{ std::move(codec_copy), media->time_base, media->media_type });
  return true;
}

bool segment_writer::may_attempt_segment()
{
  if (!m_segment_failing)
    return true;
  return std::chrono::steady_clock::now() - m_last_failed_attempt >= k_failed_retry_interval;
}

void segment_writer::note_segment_failure(const std::string& reason)
{
  m_last_failed_attempt = std::chrono::steady_clock::now();
  if (m_segment_failing)
    return;  // Already said so; saying it again every attempt is what buried the log.
  m_segment_failing = true;
  logger::instance().error(std::format(
    "camera '{}' stream '{}': recording stopped — {}. Retrying every {}s.",
    m_camera_id, m_stream_name, reason, k_failed_retry_interval.count()));
}

void segment_writer::note_segment_recovered()
{
  if (!m_segment_failing)
    return;
  m_segment_failing = false;
  logger::instance().info(std::format(
    "camera '{}' stream '{}': recording resumed", m_camera_id, m_stream_name));
}

void segment_writer::open_new_segment(std::chrono::system_clock::time_point first_frame_wall)
{
  // Called on every keyframe while no segment is open, so this is the throttle: without it a
  // storage that is down is probed at the video's frame rate.
  if (!may_attempt_segment())
    return;

  m_timing.clear();
  close_segment();

  if (m_input_streams.empty())
  {
    logger::instance().warning("segment writer has no registered streams");
    return;
  }

  // The keyframe's own capture time when it has one; only a packet that cannot say when it was
  // captured falls back to this thread's clock.
  const auto now = std::chrono::floor<std::chrono::milliseconds>(
    first_frame_wall.time_since_epoch().count() != 0 ? first_frame_wall
                                                     : std::chrono::system_clock::now());

  recording_storage storage;

  const auto cam_storages = fs_client::instance().get_camera_storages(m_camera_id);
  if (cam_storages.empty())
  {
    // Either nothing is assigned, or everything assigned is disabled for writing — VmsFs filters
    // those out before answering. Both mean the same thing here: there is nowhere to put a segment.
    note_segment_failure("no storage available to write to");
    return;
  }

  m_segment_rel_path = storage.make_segment_path(m_camera_id, m_stream_name, now);

  fs_client::storage_info cam_storage;
  std::filesystem::path path;
  bool dir_ready = false;
  for (const auto& s : cam_storages)
  {
    std::filesystem::path candidate = std::filesystem::path(s.root_path) / m_segment_rel_path;
    std::error_code ec;
    std::filesystem::create_directories(candidate.parent_path(), ec);
    if (ec)
    {
      logger::instance().warning(std::format(
        "storage '{}' (root '{}') not accessible: {} — trying next",
        s.storage_id, s.root_path, ec.message()));
      continue;
    }
    cam_storage = s;
    path = candidate;
    dir_ready = true;
    break;
  }

  if (!dir_ready)
  {
    note_segment_failure(std::format(
      "none of the {} assigned storage(s) accepted a directory", cam_storages.size()));
    return;
  }

  m_segment_abs_path = path;
  {
    std::lock_guard path_lock(m_segment_path_mutex);
    m_current_segment_path = path;
  }

  AVFormatContext* ctx = nullptr;

  if (avformat_alloc_output_context2(
    &ctx, nullptr, nullptr, path.string().c_str()) < 0)
  {
    logger::instance().error("avformat_alloc_output_context2 failed");
    return;
  }

  m_ctx.reset(ctx);

  m_output_streams.clear();

  for (const auto& [input_index, stream_info] : m_input_streams)
  {
    AVStream* out_stream = avformat_new_stream(m_ctx.get(), nullptr);
    if (!out_stream)
    {
      logger::instance().error("avformat_new_stream failed");
      m_ctx.reset();
      m_output_streams.clear();
      return;
    }

    if (avcodec_parameters_copy(out_stream->codecpar, stream_info.codec_parameters.get()) < 0)
    {
      logger::instance().error("avcodec_parameters_copy failed");
      m_ctx.reset();
      m_output_streams.clear();
      return;
    }

    out_stream->time_base = stream_info.time_base;
    m_output_streams.emplace(input_index, out_stream);
  }

  if (!(m_ctx->oformat->flags & AVFMT_NOFILE))
  {
    if (avio_open(&m_ctx->pb, path.string().c_str(), AVIO_FLAG_WRITE) < 0)
    {
      logger::instance().error("avio_open failed");
      m_ctx.reset();
      m_output_streams.clear();
      return;
    }
  }


  AVDictionary* mux_opts = nullptr;
  av_dict_set(&mux_opts, "movflags",
    "frag_keyframe+empty_moov+default_base_moof", 0);

  const int write_header_ret = avformat_write_header(m_ctx.get(), &mux_opts);
  av_dict_free(&mux_opts);
  if (write_header_ret < 0)
  {
    char av_errbuf[AV_ERROR_MAX_STRING_SIZE]{};
    av_strerror(write_header_ret, av_errbuf, sizeof(av_errbuf));
    logger::instance().error(std::format("avformat_write_header failed: {}", av_errbuf));
    avio_closep(&m_ctx->pb);
    m_ctx.reset();
    m_output_streams.clear();
    return;
  }

  m_segment_start_time = std::chrono::steady_clock::now();
  m_segment_start_ts = now;
  m_last_write_ts = {};
  m_segment_storage_id = cam_storage.storage_id;
  // A segment exists again. Says so once, so the log shows the outage as a pair of lines with a
  // duration between them rather than as a wall of identical errors that simply stops.
  note_segment_recovered();

  logger::instance().debug(std::format(
    "Segment opened: camera='{}' stream='{}' file='{}'",
    m_camera_id, m_stream_name, path.string()));

  fs_client::instance().enqueue_ingest_clip(
    m_camera_id,
    m_stream_name,
    m_segment_storage_id,
    m_segment_rel_path.string(),
    m_segment_start_ts,
    std::nullopt,
    0);
}

void segment_writer::close_segment()
{
  if (!m_ctx)
    return;

  av_write_trailer(m_ctx.get());

  if (!(m_ctx->oformat->flags & AVFMT_NOFILE))
    avio_closep(&m_ctx->pb);

  // Last written packet, not "now": see m_last_write_ts. Falls back to now only for a segment that
  // was opened and closed without a single packet getting through.
  const auto segment_end_ts = m_last_write_ts.time_since_epoch().count() != 0
    ? m_last_write_ts
    : std::chrono::system_clock::now();

  m_ctx.reset();
  m_output_streams.clear();

  if (!m_segment_abs_path.empty() && !m_segment_rel_path.empty())
  {
    std::error_code ec;
    const auto size = std::filesystem::file_size(m_segment_abs_path, ec);
    if (ec)
    {
      logger::instance().warning(std::format(
        "failed to read segment size for '{}': {}",
        m_segment_abs_path.string(),
        ec.message()));

      logger::instance().debug(std::format(
        "Segment closed: camera='{}' stream='{}' file='{}'",
        m_camera_id, m_stream_name, m_segment_abs_path.string()));

      fs_client::instance().enqueue_ingest_clip(
        m_camera_id,
        m_stream_name,
        m_segment_storage_id,
        m_segment_rel_path.string(),
        m_segment_start_ts,
        segment_end_ts,
        0);
    }
    else
    {
      logger::instance().debug(std::format(
        "Segment closed: camera='{}' stream='{}' file='{}' size={} bytes",
        m_camera_id, m_stream_name, m_segment_abs_path.string(), size));

      fs_client::instance().enqueue_ingest_clip(
        m_camera_id,
        m_stream_name,
        m_segment_storage_id,
        m_segment_rel_path.string(),
        m_segment_start_ts,
        segment_end_ts,
        size);
    }
  }

  m_segment_abs_path.clear();
  m_segment_rel_path.clear();
  m_segment_storage_id.clear();
  m_segment_start_ts = {};
  {
    std::lock_guard path_lock(m_segment_path_mutex);
    m_current_segment_path.clear();
  }
}

std::filesystem::path segment_writer::current_segment_path() const
{
  std::lock_guard path_lock(m_segment_path_mutex);
  return m_current_segment_path;
}

void segment_writer::on_packet(const std::shared_ptr<media_packet>& in_media)
{
  if (!in_media || !in_media->packet)
    return;
  std::lock_guard lock(m_queue_mutex);
  if (m_queue.size() >= k_max_queue_depth)
    return; // drop when writer thread falls behind
  m_queue.push(in_media);
  m_queue_cv.notify_one();
}

void segment_writer::process_queue()
{
  while (true)
  {
    std::queue<std::shared_ptr<media_packet>> batch;
    {
      std::unique_lock lock(m_queue_mutex);
      m_queue_cv.wait(lock, [this]{ return !m_queue.empty() || m_stopping.load(); });
      std::swap(batch, m_queue);
    }

    process_batch(batch);

    if (m_stopping.load())
      break;
  }

  // Final drain: a packet may have been pushed between the stop signal and
  // remove_sink() taking effect. Flush it before closing the segment.
  std::queue<std::shared_ptr<media_packet>> remaining;
  {
    std::lock_guard lock(m_queue_mutex);
    std::swap(remaining, m_queue);
  }
  process_batch(remaining);

  close();
}

void segment_writer::process_batch(std::queue<std::shared_ptr<media_packet>>& batch)
{
  while (!batch.empty())
  {
    auto pkt = std::move(batch.front());
    batch.pop();
    if (pkt)
      process_packet(pkt);
    else
      close_segment(); // control marker from request_close_segment()
  }
}

void segment_writer::process_packet(const std::shared_ptr<media_packet>& in_media)
{
  auto media = m_packet_transcoder.transcode_packet(in_media);
  while (media)
  {
    write_packet(media);
    media = m_packet_transcoder.transcode_packet(nullptr);
  }
}

void segment_writer::write_packet(const std::shared_ptr<media_packet>& in_media)
{
  if (!in_media || !in_media->packet || !ensure_input_stream(in_media))
    return;

  auto media = clone_media_packet(in_media);
  if (!media)
    return;

  AVPacket* pkt = media->packet.get();

  if (!m_ctx)
  {
    if (media->media_type != AVMEDIA_TYPE_VIDEO || !(pkt->flags & AV_PKT_FLAG_KEY))
      return;
    open_new_segment(media->wall_ts.value_or(std::chrono::system_clock::time_point{}));
    if (!m_ctx) return;
  }

  auto elapsed = std::chrono::steady_clock::now() - m_segment_start_time;
  if (elapsed >= m_segment_duration.load() &&
    media->media_type == AVMEDIA_TYPE_VIDEO &&
    (pkt->flags & AV_PKT_FLAG_KEY))
  {
    open_new_segment(media->wall_ts.value_or(std::chrono::system_clock::time_point{}));
    if (!m_ctx) return;
  }

  auto out_it = m_output_streams.find(media->stream_index);
  if (out_it == m_output_streams.end())
    return;

  AVStream* out_stream = out_it->second;
  auto& timing = m_timing[media->stream_index];

  av_packet_rescale_ts(pkt, media->time_base, out_stream->time_base);

  bool invalid = pkt->pts == AV_NOPTS_VALUE || pkt->dts == AV_NOPTS_VALUE || pkt->pts < 0 || pkt->dts < 0;

  if (!invalid)
  {
    if (timing.first_pts == AV_NOPTS_VALUE) timing.first_pts = pkt->pts;
    if (timing.first_dts == AV_NOPTS_VALUE) timing.first_dts = pkt->dts;

    pkt->pts -= timing.first_pts;
    pkt->dts -= timing.first_dts;

    if (timing.last_pts != AV_NOPTS_VALUE && pkt->pts <= timing.last_pts) pkt->pts = timing.last_pts + 1;
    if (timing.last_dts != AV_NOPTS_VALUE && pkt->dts <= timing.last_dts) pkt->dts = timing.last_dts + 1;
  }
  else
  {
    if (pkt->duration <= 0) pkt->duration = 1;
    int64_t next = (timing.last_pts != AV_NOPTS_VALUE) ? timing.last_pts + pkt->duration : 0;
    pkt->pts = pkt->dts = next;
  }

  timing.last_pts = pkt->pts;
  timing.last_dts = pkt->dts;

  if (pkt->dts > pkt->pts) pkt->dts = pkt->pts;

  pkt->stream_index = out_stream->index;

  if (av_interleaved_write_frame(m_ctx.get(), pkt) < 0)
  {
    logger::instance().error("write_frame failed");
    return;
  }

  // Capture time, not the moment this thread got round to writing: the queue in front of this
  // writer and the network in front of the reader both add delay, and a clip whose stamps disagree
  // with the footage inside it makes the player drift and the gap detector miss the hole.
  // A packet that cannot say when it was captured leaves the boundary where it was: falling back
  // to "now" would hand the clip the time spent queued in front of this writer.
  if (media->wall_ts.has_value())
  {
    const auto write_ts = *media->wall_ts;
    if (m_last_write_ts.time_since_epoch().count() == 0)
    {
      // First packet of the segment. Normally a no-op now that open_new_segment() is handed this
      // very packet's capture time: it still matters for a first packet that arrived without a
      // wall_ts, where the segment was opened on this thread's clock instead.
      m_segment_start_ts = write_ts;
    }
    if (write_ts > m_last_write_ts)
    {
      m_last_write_ts = write_ts;
    }
  }
}

void segment_writer::close()
{
  m_packet_transcoder.close();
  close_segment();
}

#include "writer/segment_writer.h"

#include <chrono>
#include <ctime>
#include <format>
#include <iomanip>
#include <iostream>
#include <sstream>

namespace {
  std::string to_iso_utc(const std::chrono::system_clock::time_point& tp)
  {
    using namespace std::chrono;

    auto utc_time = zoned_time{ "UTC", tp };
    auto sys_time = utc_time.get_sys_time();
    std::time_t t = system_clock::to_time_t(sys_time);

    // форматируем через ctime или put_time
    std::tm tm = *std::gmtime(&t);  // note: std::gmtime не потокобезопасна, но здесь одноразово
    char buf[32];
    std::strftime(buf, sizeof(buf), "%Y-%m-%dT%H:%M:%SZ", &tm);
    return std::string(buf);
  }

std::string json_escape(const std::string& value)
{
  std::string escaped;
  escaped.reserve(value.size());

  for (char c : value) {
    switch (c) {
      case '"':
        escaped += "\\\"";
        break;
      case '\\':
        escaped += "\\\\";
        break;
      case '\n':
        escaped += "\\n";
        break;
      case '\r':
        escaped += "\\r";
        break;
      case '\t':
        escaped += "\\t";
        break;
      default:
        escaped += c;
        break;
    }
  }

  return escaped;
}

std::string make_segment_started_payload(const std::string& camera_id,
  const std::string& relative_path,
  const std::chrono::system_clock::time_point& started_at)
{
  return std::format(
    "{{\"event\":\"recording_segment_started\","
    "\"camera_id\":\"{}\","
    "\"relative_path\":\"{}\","
    "\"started_at\":\"{}\"}}",
    json_escape(camera_id),
    json_escape(relative_path),
    to_iso_utc(started_at));
}

std::string make_segment_finished_payload(const std::string& camera_id,
  const std::string& relative_path,
  const std::chrono::system_clock::time_point& started_at,
  const std::chrono::system_clock::time_point& finished_at,
  double duration_seconds)
{
  return std::format(
    "{{\"event\":\"recording_segment_finished\","
    "\"camera_id\":\"{}\","
    "\"relative_path\":\"{}\","
    "\"started_at\":\"{}\","
    "\"finished_at\":\"{}\","
    "\"duration_seconds\":{:.3f}}}",
    json_escape(camera_id),
    json_escape(relative_path),
    to_iso_utc(started_at),
    to_iso_utc(finished_at),
    duration_seconds);
}
}

segment_writer::segment_writer(const std::string& base_dir,
  const std::string& camera_id,
  int segment_seconds)
  : m_base_dir(base_dir),
  m_camera_id(camera_id),
  m_segment_seconds(segment_seconds)
{
}

bool segment_writer::init(const avcodec_parameters_ptr& codec_parameters, AVRational time_base)
{
  if (!codec_parameters) {
    return false;
  }

  avcodec_parameters_ptr codec_copy(avcodec_parameters_alloc(),
    [](AVCodecParameters* params) { avcodec_parameters_free(&params); });
  if (!codec_copy) {
    return false;
  }

  if (avcodec_parameters_copy(codec_copy.get(), codec_parameters.get()) < 0) {
    return false;
  }

  m_in_codec_parameters = std::move(codec_copy);
  m_in_time_base = time_base;
  std::filesystem::create_directories(m_base_dir);
  return true;
}

void segment_writer::open_new_segment()
{
  close_segment();

  if (!m_in_codec_parameters) {
    std::cerr << "segment writer is not initialized with input codec parameters\n";
    return;
  }

  const auto now = std::chrono::floor<std::chrono::seconds>(std::chrono::system_clock::now());
  const std::string year = std::format("{:%Y}", now);
  const std::string month_day = std::format("{:%m-%d}", now);

  m_current_segment_relative_path =
    std::filesystem::path(m_camera_id) / year / month_day / make_filename();
  auto path = m_base_dir / m_current_segment_relative_path;
  std::filesystem::create_directories(path.parent_path());

  if (avformat_alloc_output_context2(
    &m_ctx, nullptr, nullptr, path.string().c_str()) < 0)
  {
    std::cerr << "avformat_alloc_output_context2 failed\n";
    m_ctx = nullptr;
    return;
  }

  m_out_stream = avformat_new_stream(m_ctx, nullptr);
  if (!m_out_stream) {
    std::cerr << "avformat_new_stream failed\n";
    avformat_free_context(m_ctx);
    m_ctx = nullptr;
    return;
  }

  if (avcodec_parameters_copy(
    m_out_stream->codecpar, m_in_codec_parameters.get()) < 0) {
    std::cerr << "avcodec_parameters_copy failed\n";
    avformat_free_context(m_ctx);
    m_ctx = nullptr;
    m_out_stream = nullptr;
    return;
  }

  m_out_stream->time_base = m_in_time_base;

  if (!(m_ctx->oformat->flags & AVFMT_NOFILE)) {
    if (avio_open(&m_ctx->pb,
      path.string().c_str(),
      AVIO_FLAG_WRITE) < 0)
    {
      std::cerr << "avio_open failed\n";
      avformat_free_context(m_ctx);
      m_ctx = nullptr;
      return;
    }
  }

  if (avformat_write_header(m_ctx, nullptr) < 0) {
    std::cerr << "avformat_write_header failed\n";
    avio_closep(&m_ctx->pb);
    avformat_free_context(m_ctx);
    m_ctx = nullptr;
    return;
  }

  m_segment_start_time = std::chrono::steady_clock::now();
  m_segment_start_wall_time = std::chrono::system_clock::now();
  m_pts = 0;

  const std::string payload = make_segment_started_payload(
    m_camera_id,
    m_current_segment_relative_path.string(),
    m_segment_start_wall_time);
  m_dapr_publisher.publish_json("recording.events", payload);
}

void segment_writer::close_segment()
{
  if (!m_ctx)
    return;

  const auto finished_at = std::chrono::system_clock::now();
  const auto duration = std::chrono::duration_cast<std::chrono::milliseconds>(
    std::chrono::steady_clock::now() - m_segment_start_time);

  const std::string payload = make_segment_finished_payload(
    m_camera_id,
    m_current_segment_relative_path.string(),
    m_segment_start_wall_time,
    finished_at,
    duration.count() / 1000.0);
  m_dapr_publisher.publish_json("recording.events", payload);

  av_write_trailer(m_ctx);

  if (!(m_ctx->oformat->flags & AVFMT_NOFILE))
    avio_closep(&m_ctx->pb);

  avformat_free_context(m_ctx);
  m_ctx = nullptr;
  m_out_stream = nullptr;
  m_current_segment_relative_path.clear();
}

void segment_writer::on_packet(const std::shared_ptr<media_packet>& media)
{
  if (!media || !media->packet)
    return;

  AVPacket* pkt = media->packet.get();

  // стартуем ТОЛЬКО с keyframe
  if (!m_ctx) {
    if (!(pkt->flags & AV_PKT_FLAG_KEY))
      return;

    open_new_segment();
    if (!m_ctx)
      return;
  }

  auto elapsed = std::chrono::steady_clock::now() - m_segment_start_time;

  // 🔪 режем ТОЛЬКО по keyframe
  if (elapsed >= m_segment_seconds &&
    (pkt->flags & AV_PKT_FLAG_KEY))
  {
    open_new_segment();
    if (!m_ctx)
      return;
  }

  AVRational tb = m_out_stream->time_base;

  int64_t frame_duration = av_rescale_q(1, AVRational{ 1,25 }, tb); ;

  av_packet_rescale_ts(pkt, media->time_base, m_out_stream->time_base);

  if (pkt->duration <= 0)
  {
    pkt->duration = frame_duration;
  }
  else
  {
    frame_duration = pkt->duration;
  }

  pkt->dts = m_pts;
  pkt->pts = m_pts;

  m_pts += frame_duration;


  pkt->stream_index = m_out_stream->index;

  if (av_interleaved_write_frame(m_ctx, pkt) < 0) {
    std::cerr << "write_frame failed\n";
  }
}

std::string segment_writer::make_filename() const
{
  using namespace std::chrono;
  auto now = floor<seconds>(system_clock::now());
  return std::format("{:%Y-%m-%d_%H-%M-%S}.mkv", now);
}

void segment_writer::close()
{
  close_segment();
}

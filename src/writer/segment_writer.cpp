#include "writer/segment_writer.h"
#include <format>
#include <iomanip>
#include <iostream>
#include <filesystem>
#include <chrono>

segment_writer::segment_writer(const std::string& base_dir,
  const std::string& camera_id,
  int segment_seconds)
  : m_base_dir(base_dir),
  m_camera_id(camera_id),
  m_segment_seconds(segment_seconds),
  m_next_dts(0)
{}

bool segment_writer::init(AVStream* in_stream) {
  m_in_stream = in_stream;
  std::filesystem::create_directories(m_base_dir / m_camera_id);
  return true;
}

void segment_writer::open_new_segment() {
  close_segment();
  m_next_dts = 0;
  m_segment_start_time = std::chrono::steady_clock::now();

  auto path = m_base_dir / m_camera_id / make_filename();

  if (avformat_alloc_output_context2(&m_ctx, nullptr, "matroska", path.string().c_str()) < 0) {
    std::cerr << "Failed to allocate output context\n";
    m_ctx = nullptr;
    return;
  }

  m_out_stream = avformat_new_stream(m_ctx, nullptr);
  avcodec_parameters_copy(m_out_stream->codecpar, m_in_stream->codecpar);
  m_out_stream->time_base = m_in_stream->time_base;

  if (!(m_ctx->oformat->flags & AVFMT_NOFILE)) {
    if (avio_open(&m_ctx->pb, path.string().c_str(), AVIO_FLAG_WRITE) < 0) {
      std::cerr << "Failed to open file: " << path << "\n";
      avformat_free_context(m_ctx);
      m_ctx = nullptr;
      return;
    }
  }

  if (avformat_write_header(m_ctx, nullptr) < 0) {
    std::cerr << "Failed to write header\n";
    avio_closep(&m_ctx->pb);
    avformat_free_context(m_ctx);
    m_ctx = nullptr;
    return;
  }

  // Вставляем последний ключевой кадр первым, если есть
// вставляем последний ключевой кадр первым
  if (m_last_keyframe) {
    AVPacket kf;
    av_init_packet(&kf);
    av_packet_ref(&kf, m_last_keyframe.get()); // копируем пакет
    kf.stream_index = m_out_stream->index;
    kf.pts = 0;
    kf.dts = 0;
    kf.duration = av_rescale_q(1, av_inv_q(m_in_stream->r_frame_rate), m_in_stream->time_base);

    m_next_dts = kf.duration;
    av_interleaved_write_frame(m_ctx, &kf);
    av_packet_unref(&kf); // освобождаем копию
  }
}

void segment_writer::close_segment() {
  if (!m_ctx) return;
  av_write_trailer(m_ctx);
  if (!(m_ctx->oformat->flags & AVFMT_NOFILE))
    avio_closep(&m_ctx->pb);
  avformat_free_context(m_ctx);
  m_ctx = nullptr;
  m_out_stream = nullptr;
}

void segment_writer::on_packet(const std::shared_ptr<media_packet>& media) {
  if (!media || !media->packet) return;
  AVPacket* pkt = media->packet.get();

  // Сохраняем последний ключевой кадр
  if (pkt->flags & AV_PKT_FLAG_KEY) {
    m_last_keyframe = media->packet;
  }

  if (!m_ctx) open_new_segment();
  if (!m_ctx) return;

  // Выставляем свои DTS/PTS
  pkt->stream_index = m_out_stream->index;
  pkt->pts = m_next_dts;
  pkt->dts = m_next_dts;

  // duration = 1 кадр
  pkt->duration = av_rescale_q(1, av_inv_q(m_in_stream->r_frame_rate), m_in_stream->time_base);

  m_next_dts += pkt->duration;

  av_interleaved_write_frame(m_ctx, pkt);

  // проверка времени сегмента
  auto now = std::chrono::steady_clock::now();
  auto elapsed_sec = std::chrono::duration_cast<std::chrono::seconds>(now - m_segment_start_time).count();
  if (elapsed_sec >= m_segment_seconds) {
    open_new_segment();
  }
}

std::string segment_writer::make_filename() const {
  using namespace std::chrono;
  auto now = floor<seconds>(system_clock::now());
  return std::format("{:%Y-%m-%d_%H-%M-%S}.mkv", now);
}

void segment_writer::close() {
  close_segment();
}
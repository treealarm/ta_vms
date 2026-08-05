#include "edge_import/edge_import_worker.h"

#include <algorithm>
#include <cstdlib>
#include <format>
#include <system_error>
#include <utility>

extern "C" {
#include <libavformat/avio.h>
#include <libavutil/error.h>
}

#include "edge_import/onvif_replay_source.h"
#include "file_system/recording_storage.h"
#include "grpc_layer/fs_client.h"
#include "logger/logger.h"

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

/// Inserts an `_edge` marker before the extension so an imported clip can never overwrite a live
/// segment written for the same second — clips.file_path is unique in the database, the file on
/// disk is not.
/// Marks the file as recovered footage and makes it unique to this job.
///
/// The marker alone is not enough. Segment paths are derived from the clip's start second, so two
/// pulls of the same stretch produce the same name, and the second truncates a file the first one's
/// clip row — and its cached fragment index — still describe. The archive then serves byte ranges
/// past the end of the file and playback fails outright.
std::filesystem::path with_edge_marker(const std::filesystem::path& path, const std::string& job_id)
{
  auto marked = path;
  const auto suffix = job_id.size() > 8 ? job_id.substr(0, 8) : job_id;
  marked.replace_filename(
    path.stem().string() + "_edge" + (suffix.empty() ? "" : "_" + suffix) + path.extension().string());
  return marked;
}

std::string require_env(const char* key)
{
  const char* value = std::getenv(key);
  if (!value || *value == '\0')
  {
    throw std::runtime_error(std::format("Required environment variable '{}' is not set", key));
  }
  return value;
}

}  // namespace

edge_import_worker::edge_import_worker(
  edge_import_settings settings,
  const std::atomic_bool& cancel_flag,
  std::function<void(const edge_import_progress&)> on_progress)
  :
  m_settings(std::move(settings)),
  m_cancel(cancel_flag),
  m_on_progress(std::move(on_progress))
{
  m_window = std::chrono::duration_cast<std::chrono::milliseconds>(
    m_settings.end_ts - m_settings.start_ts);
  m_progress.position = m_settings.start_ts;
}

edge_import_worker::~edge_import_worker()
{
  close_segment();
}

edge_import_outcome edge_import_worker::run()
{
  edge_import_outcome outcome;
  std::string error;

  if (m_window <= std::chrono::milliseconds::zero())
  {
    outcome.message = "requested window is empty";
    outcome.progress = m_progress;
    return outcome;
  }

  if (!resolve_target(error))
  {
    outcome.message = error;
    outcome.progress = m_progress;
    return outcome;
  }

  m_source = std::make_unique<onvif_replay_source>(replay_source_settings{
    .replay_uri = m_settings.replay_uri,
    .user = m_settings.user,
    .password = m_settings.password,
    .start_ts = m_settings.start_ts,
    .end_ts = m_settings.end_ts,
    .seek_start_ts = m_settings.seek_start_ts,
    .seek_end_ts = m_settings.seek_end_ts,
    .recording_start_ts = m_settings.recording_start_ts,
  });

  if (!m_source->open(error))
  {
    close_segment();
    outcome.message = error;
    outcome.device_busy = m_source->last_failure_was_device_busy();
    outcome.progress = m_progress;
    return outcome;
  }

  const bool read_ok = read_loop(error);
  close_segment();

  outcome.progress = m_progress;
  outcome.cancelled = m_cancel.load();
  if (outcome.cancelled)
  {
    outcome.message = "cancelled";
    return outcome;
  }
  if (!read_ok)
  {
    outcome.message = error;
    return outcome;
  }
  if (m_progress.clips_written == 0)
  {
    // Nothing was produced at all: no video keyframe in the window, an empty stream, or a seek that
    // landed past the available data.
    outcome.message = "replay produced no output (no keyframe in window or empty stream)";
    return outcome;
  }

  const auto covered_ms = std::chrono::duration_cast<std::chrono::milliseconds>(
    m_progress.position - m_settings.start_ts);

  if (!m_window_covered)
  {
    if (!m_reached_eof)
    {
      // The stream stopped mid-window without ending cleanly — the device dropped it or the replay
      // URI expired. What was written stays, but the job is a failure.
      outcome.message = std::format(
        "replay stopped before the requested window was covered (reached {}ms of {}ms)",
        covered_ms.count(), m_window.count());
      return outcome;
    }
    // Clean EOF short of the window: the camera simply has no more footage in that range. The
    // common case when the request runs to "now" or past the start of the card's history.
    outcome.success = true;
    outcome.message = std::format(
      "imported {} clip(s); device had {}ms of the {}ms requested",
      m_progress.clips_written, covered_ms.count(), m_window.count());
    return outcome;
  }
  outcome.success = true;
  outcome.message = m_settings.export_to_file
    ? std::format("exported {} bytes", m_progress.bytes_written)
    : std::format("imported {} clip(s)", m_progress.clips_written);
  return outcome;
}

bool edge_import_worker::resolve_target(std::string& error)
{
  if (m_settings.export_to_file)
  {
    // Export files are not archive clips: they never reach the clips table, so they must live
    // outside the storage roots or they would double the camera's timeline coverage.
    // Caught here rather than propagated: run() executes on a detached job thread, where an escaping
    // exception would std::terminate the whole media server over one misconfigured job.
    try
    {
      m_target_root = std::filesystem::path(require_env("VMS_EXPORT_BASE_PATH"));
    }
    catch (const std::exception& ex)
    {
      error = ex.what();
      return false;
    }
    return true;
  }

  const auto storages = fs_client::instance().get_camera_storages(m_settings.camera_id);
  const auto writable = std::find_if(storages.begin(), storages.end(),
                                     [](const fs_client::storage_info& s) { return !s.empty(); });
  if (writable == storages.end())
  {
    error = std::format("no writable storage for camera='{}' stream='{}'",
                        m_settings.camera_id, m_settings.stream_name);
    return false;
  }
  m_target_root = writable->root_path;
  m_storage_id = writable->storage_id;
  return true;
}

bool edge_import_worker::read_loop(std::string& error)
{
  avpacket_ptr packet(av_packet_alloc(), avpacket_deleter{});
  if (!packet)
  {
    error = "allocate packet";
    return false;
  }

  while (!m_cancel.load())
  {
    if (!m_source->read(packet.get(), error))
    {
      // Empty error means a clean end of stream: the device has nothing left in the range. That is
      // the normal ending when the requested window runs past what the card holds, so it is not a
      // failure — run() reports how much was actually covered.
      m_reached_eof = error.empty();
      return error.empty();
    }

    const auto offset = m_source->packet_offset(packet.get());
    if (offset >= m_window)
    {
      m_window_covered = true;
      av_packet_unref(packet.get());
      return true;
    }

    const auto wall_ts = m_settings.start_ts + offset;
    // Only a video keyframe can start a decodable clip. Audio packets carry AV_PKT_FLAG_KEY too,
    // so gating on the flag alone would open a segment on audio and leave the video mid-GOP —
    // same rule as the live path in segment_writer::write_packet.
    const bool is_video_key = is_video_stream(packet->stream_index)
      && (packet->flags & AV_PKT_FLAG_KEY) != 0;
    // Both paths rotate; only the length differs. A single multi-gigabyte export is awkward to
    // download and cannot be previewed without pulling all of it.
    const auto rotate_after = m_settings.export_to_file
      ? m_settings.export_chunk_duration
      : m_settings.segment_duration;
    const bool rotate_due = rotate_after > std::chrono::seconds::zero()
      && offset - m_segment_start_offset >= rotate_after;

    if (!m_output || (is_video_key && rotate_due))
    {
      if (!m_output && !is_video_key)
      {
        av_packet_unref(packet.get());
        continue;
      }
      close_segment();
      m_segment_start_offset = offset;
      if (!open_segment(wall_ts, error))
      {
        av_packet_unref(packet.get());
        return false;
      }
    }

    m_segment_last_ts = wall_ts;
    m_progress.position = wall_ts;
    const bool written = write_packet(packet.get());
    av_packet_unref(packet.get());
    if (!written)
    {
      error = "write imported packet";
      return false;
    }
    if (m_on_progress)
    {
      m_on_progress(m_progress);
    }
  }
  return true;
}

bool edge_import_worker::open_segment(
  std::chrono::system_clock::time_point ts,
  std::string& error)
{
  if (m_settings.export_to_file)
  {
    // Numbered parts under the job id: they list and sort in playback order, and the directory walk
    // in VmsFs picks them up without the job having to enumerate them.
    m_export_part++;
    m_segment_rel_path = std::filesystem::path(m_settings.camera_id) /
                         std::format("{}_{:03}{}", m_settings.job_id, m_export_part,
                                     std::string(recording_storage::k_ext));
  }
  else
  {
    recording_storage storage;
    m_segment_rel_path = with_edge_marker(
      storage.make_segment_path(m_settings.camera_id, m_settings.stream_name, ts),
      m_settings.job_id);
  }
  m_segment_abs_path = m_target_root / m_segment_rel_path;

  std::error_code mkdir_ec;
  std::filesystem::create_directories(m_segment_abs_path.parent_path(), mkdir_ec);
  if (mkdir_ec)
  {
    error = std::format("create directory '{}': {}",
                        m_segment_abs_path.parent_path().string(), mkdir_ec.message());
    return false;
  }

  AVFormatContext* ctx = nullptr;
  const int alloc_rc =
    avformat_alloc_output_context2(&ctx, nullptr, nullptr, m_segment_abs_path.string().c_str());
  if (alloc_rc < 0)
  {
    error = std::format("allocate muxer: {}", av_error_text(alloc_rc));
    return false;
  }
  m_output.reset(ctx);
  m_streams.clear();

  const AVFormatContext* input = m_source->format_context();
  for (unsigned i = 0; i < input->nb_streams; ++i)
  {
    const AVStream* in_stream = input->streams[i];
    const AVMediaType type = in_stream->codecpar->codec_type;
    if (type != AVMEDIA_TYPE_VIDEO && type != AVMEDIA_TYPE_AUDIO)
    {
      continue;
    }
    // Imports are a pure remux, while live recording transcodes audio to AAC (see segment_writer's
    // codec_config). Copying G.711 or G.726 into an archive clip would therefore produce a track no
    // browser can decode and break playback of the whole clip, so such audio is dropped until the
    // import path can transcode it too.
    if (type == AVMEDIA_TYPE_AUDIO && in_stream->codecpar->codec_id != AV_CODEC_ID_AAC)
    {
      logger::instance().warning(std::format(
        "edge import: dropping audio track of camera '{}' — {} cannot be remuxed into the archive",
        m_settings.camera_id,
        avcodec_get_name(in_stream->codecpar->codec_id)));
      continue;
    }
    AVStream* out_stream = avformat_new_stream(m_output.get(), nullptr);
    if (!out_stream)
    {
      error = "allocate output stream";
      m_output.reset();
      return false;
    }
    if (avcodec_parameters_copy(out_stream->codecpar, in_stream->codecpar) < 0)
    {
      error = "copy codec parameters";
      m_output.reset();
      return false;
    }
    out_stream->codecpar->codec_tag = 0;
    out_stream->time_base = in_stream->time_base;
    m_streams.emplace(static_cast<int>(i), output_stream_state{ out_stream });
  }
  if (m_streams.empty())
  {
    error = "replay stream carries no audio or video";
    m_output.reset();
    return false;
  }
  for (const auto& [_, state] : m_streams)
  {
    const AVCodecParameters* par = state.stream->codecpar;
    if (par->codec_type == AVMEDIA_TYPE_VIDEO && (par->width <= 0 || par->height <= 0))
    {
      error = "video dimensions unknown after probe — cannot write header";
      m_output.reset();
      return false;
    }
  }

  if (!(m_output->oformat->flags & AVFMT_NOFILE))
  {
    const int io_rc =
      avio_open(&m_output->pb, m_segment_abs_path.string().c_str(), AVIO_FLAG_WRITE);
    if (io_rc < 0)
    {
      error = std::format("open output file '{}': {}",
                          m_segment_abs_path.string(), av_error_text(io_rc));
      m_output.reset();
      return false;
    }
  }

  AVDictionary* mux_opts = nullptr;
  // Archive clips are fragmented mp4 like every live segment, so the existing HLS byte-range
  // player handles them unchanged. Exports are plain mp4 with a leading moov instead, so any
  // browser or player opens the downloaded file.
  av_dict_set(&mux_opts, "movflags",
              m_settings.export_to_file ? "faststart"
                                        : "frag_keyframe+empty_moov+default_base_moof", 0);
  const int header_rc = avformat_write_header(m_output.get(), &mux_opts);
  av_dict_free(&mux_opts);
  if (header_rc < 0)
  {
    error = std::format("write header: {}", av_error_text(header_rc));
    avio_closep(&m_output->pb);
    m_output.reset();
    return false;
  }

  m_segment_start_ts = ts;
  m_segment_last_ts = ts;
  return true;
}

bool edge_import_worker::is_video_stream(int stream_index) const
{
  const AVFormatContext* input = m_source ? m_source->format_context() : nullptr;
  if (!input || stream_index < 0 || stream_index >= static_cast<int>(input->nb_streams))
  {
    return false;
  }
  return input->streams[stream_index]->codecpar->codec_type == AVMEDIA_TYPE_VIDEO;
}

bool edge_import_worker::write_packet(AVPacket* packet)
{
  if (!m_output || !packet)
  {
    return false;
  }
  auto it = m_streams.find(packet->stream_index);
  if (it == m_streams.end())
  {
    // Metadata tracks and anything else we did not map are dropped silently.
    return true;
  }
  output_stream_state& state = it->second;

  const AVRational in_tb = m_source->format_context()->streams[packet->stream_index]->time_base;

  if (packet->pts == AV_NOPTS_VALUE || packet->dts == AV_NOPTS_VALUE)
  {
    av_packet_rescale_ts(packet, in_tb, state.stream->time_base);
    const int64_t next = state.last_pts != AV_NOPTS_VALUE ? state.last_pts + 1 : 0;
    packet->pts = next;
    packet->dts = next;
  }
  else
  {
    // Rebase every stream against ONE origin, expressed in wall time rather than in either
    // stream's ticks. Zeroing each stream on its own first packet would erase the offset between
    // audio and video and desync them — and doing it per segment would re-desync at every rotation.
    const auto pts_us = std::chrono::microseconds(
      av_rescale_q(packet->pts, in_tb, AVRational{ 1, 1000000 }));
    const auto dts_us = std::chrono::microseconds(
      av_rescale_q(packet->dts, in_tb, AVRational{ 1, 1000000 }));
    if (m_pts_origin.count() < 0)
    {
      m_pts_origin = std::min(pts_us, dts_us);
    }

    const AVRational us_tb{ 1, 1000000 };
    packet->pts = av_rescale_q((pts_us - m_pts_origin).count(), us_tb, state.stream->time_base);
    packet->dts = av_rescale_q((dts_us - m_pts_origin).count(), us_tb, state.stream->time_base);
    packet->duration = av_rescale_q(packet->duration, in_tb, state.stream->time_base);

    if (state.last_pts != AV_NOPTS_VALUE && packet->pts <= state.last_pts)
    {
      packet->pts = state.last_pts + 1;
    }
    if (state.last_dts != AV_NOPTS_VALUE && packet->dts <= state.last_dts)
    {
      packet->dts = state.last_dts + 1;
    }
  }

  // Order matters here. Clamping dts to pts can undo the monotonicity bump above and re-emit the
  // previous dts, which ffmpeg only warns about but browsers reject outright — the resulting mp4
  // simply refuses to play. Re-check after the clamp, and push pts along if it would fall behind.
  if (packet->dts > packet->pts)
  {
    packet->dts = packet->pts;
  }
  if (state.last_dts != AV_NOPTS_VALUE && packet->dts <= state.last_dts)
  {
    packet->dts = state.last_dts + 1;
    if (packet->pts < packet->dts)
    {
      packet->pts = packet->dts;
    }
  }
  state.last_pts = packet->pts;
  state.last_dts = packet->dts;
  packet->stream_index = state.stream->index;

  const int write_rc = av_interleaved_write_frame(m_output.get(), packet);
  if (write_rc < 0)
  {
    logger::instance().error(std::format(
      "edge import: write failed camera='{}' path='{}': {}",
      m_settings.camera_id, m_segment_abs_path.string(), av_error_text(write_rc)));
    return false;
  }
  return true;
}

void edge_import_worker::close_segment()
{
  if (!m_output)
  {
    return;
  }
  av_write_trailer(m_output.get());
  if (!(m_output->oformat->flags & AVFMT_NOFILE))
  {
    avio_closep(&m_output->pb);
  }
  m_output.reset();
  m_streams.clear();

  if (m_segment_rel_path.empty())
  {
    return;
  }

  std::error_code size_ec;
  const auto size = std::filesystem::file_size(m_segment_abs_path, size_ec);
  const uint64_t bytes = size_ec ? 0 : size;

  if (m_settings.export_to_file)
  {
    // The first part is what the UI plays; the rest are found by listing the export directory.
    if (m_progress.export_path.empty())
    {
      m_progress.export_path = m_segment_rel_path.string();
    }
    m_progress.bytes_written += bytes;
    m_progress.clips_written += 1;
  }
  else
  {
    // Imported clips are force-retained: the operator asked for this range explicitly, so cleanup
    // must not treat it as unclaimed continuous footage.
    fs_client::instance().enqueue_ingest_clip(
      m_settings.camera_id,
      m_settings.stream_name,
      m_storage_id,
      m_segment_rel_path.string(),
      m_segment_start_ts,
      m_segment_last_ts,
      bytes,
      true,
      "edge");

    m_progress.clips_written += 1;
    m_progress.bytes_written += bytes;
  }

  if (m_on_progress)
  {
    m_on_progress(m_progress);
  }

  m_segment_abs_path.clear();
  m_segment_rel_path.clear();
}

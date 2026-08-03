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

#include "edge_import/libav_replay_source.h"
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
std::filesystem::path with_edge_marker(const std::filesystem::path& path)
{
  auto marked = path;
  marked.replace_filename(path.stem().string() + "_edge" + path.extension().string());
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

  m_source = std::make_unique<libav_replay_source>(replay_source_settings{
    .replay_uri = m_settings.replay_uri,
    .user = m_settings.user,
    .password = m_settings.password,
    .start_ts = m_settings.start_ts,
    .recording_start_ts = m_settings.recording_start_ts,
  });

  if (!m_source->open(error))
  {
    close_segment();
    outcome.message = error;
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
    outcome.message = "replay produced no output (no keyframe in window or empty stream)";
    return outcome;
  }
  if (!m_window_covered)
  {
    // EOF before covering the window is not success: the device dropped the stream, the replay URI
    // expired, or the seek landed past the available data.
    outcome.message = std::format(
      "replay ended before the requested window was covered (reached {}ms of {}ms)",
      std::chrono::duration_cast<std::chrono::milliseconds>(
        m_progress.position - m_settings.start_ts).count(),
      m_window.count());
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
    m_target_root = std::filesystem::path(require_env("VMS_EXPORT_BASE_PATH"));
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
      // Empty error means a clean end of stream.
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
    const bool is_key = (packet->flags & AV_PKT_FLAG_KEY) != 0;
    // Export mode writes one continuous file; only the archive path rotates.
    const bool rotate_due = !m_settings.export_to_file &&
      offset - m_segment_start_offset >= m_settings.segment_duration;

    if (!m_output || (is_key && rotate_due))
    {
      if (!m_output && !is_key)
      {
        // Every output has to start on a keyframe or playback cannot decode it.
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
    m_segment_rel_path = std::filesystem::path(m_settings.camera_id) /
                         (m_settings.job_id + std::string(recording_storage::k_ext));
  }
  else
  {
    recording_storage storage;
    m_segment_rel_path = with_edge_marker(
      storage.make_segment_path(m_settings.camera_id, m_settings.stream_name, ts));
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
  av_packet_rescale_ts(packet, in_tb, state.stream->time_base);

  if (packet->pts == AV_NOPTS_VALUE || packet->dts == AV_NOPTS_VALUE)
  {
    const int64_t next = state.last_pts != AV_NOPTS_VALUE ? state.last_pts + 1 : 0;
    packet->pts = next;
    packet->dts = next;
  }
  else
  {
    if (state.first_pts == AV_NOPTS_VALUE)
    {
      state.first_pts = packet->pts;
      state.first_dts = packet->dts;
    }
    packet->pts -= state.first_pts;
    packet->dts -= state.first_dts;
    if (state.last_pts != AV_NOPTS_VALUE && packet->pts <= state.last_pts)
    {
      packet->pts = state.last_pts + 1;
    }
    if (state.last_dts != AV_NOPTS_VALUE && packet->dts <= state.last_dts)
    {
      packet->dts = state.last_dts + 1;
    }
  }
  if (packet->dts > packet->pts)
  {
    packet->dts = packet->pts;
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
    m_progress.export_path = m_segment_rel_path.string();
    m_progress.bytes_written = bytes;
    // clips_written doubles as "produced something"; an export is one artefact.
    m_progress.clips_written = 1;
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
      true);

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

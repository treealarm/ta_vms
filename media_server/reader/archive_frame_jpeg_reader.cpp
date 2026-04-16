#include "reader/archive_frame_jpeg_reader.h"

#include <algorithm>

archive_frame_jpeg_reader::archive_frame_jpeg_reader()
  : m_packet_transcoder({
      .video_codec = AV_CODEC_ID_H264,
      .audio_codec = AV_CODEC_ID_OPUS,
      .allow_hevc_video_passthrough = false,
      .allow_all_audio_passthrough = false
    })
{
}

bool archive_frame_jpeg_reader::read_frame_at(
  const std::string& camera_id,
  const std::chrono::system_clock::time_point& timestamp,
  std::vector<uint8_t>& jpeg_frame,
  std::string& error)
{
  jpeg_frame.clear();
  error.clear();
  m_packet_transcoder.close();

  const auto rec = m_storage.find_nearest(camera_id, timestamp);
  if (!rec)
  {
    error = "recording not found";
    return false;
  }

  auto position = std::chrono::duration_cast<std::chrono::milliseconds>(timestamp - rec->start_time);
  position = std::max(position, std::chrono::milliseconds(0));

  snapshot_reader snapshot(position);
  if (!snapshot.open(rec->file_path.string()))
  {
    error = "snapshot_reader open failed";
    return false;
  }

  const auto snapshots = snapshot.snapshot_packets();
  if (snapshots.empty())
  {
    error = "snapshot packets are empty";
    return false;
  }

  AVFormatContext* fake_format_ctx = nullptr;
  AVStream* fake_stream = nullptr;

  jpeg_streamer jpeg;
  bool jpeg_initialized = false;

  for (const auto& media : snapshots)
  {
    auto out_media = m_packet_transcoder.transcode_packet(media);

    while (out_media)
    {
      if (out_media->media_type == AVMEDIA_TYPE_VIDEO && out_media->codec_parameters)
      {
        if (!jpeg_initialized)
        {
          if (!build_stream_for_jpeg(out_media, fake_format_ctx, fake_stream, error))
          {
            if (fake_format_ctx)
              avformat_free_context(fake_format_ctx);
            return false;
          }

          if (!jpeg.init(fake_stream, error))
          {
            avformat_free_context(fake_format_ctx);
            return false;
          }

          jpeg_initialized = true;
        }

        jpeg.on_packet(out_media);
      }

      out_media = m_packet_transcoder.transcode_packet(nullptr);
    }
  }

  if (fake_format_ctx)
    avformat_free_context(fake_format_ctx);

  if (!jpeg_initialized)
  {
    error = "no video packet after transcode";
    return false;
  }

  uint64_t frame_number = 0;
  if (!jpeg.wait_next_frame(jpeg_frame, frame_number))
  {
    error = "jpeg frame not produced";
    return false;
  }

  return !jpeg_frame.empty();
}

bool archive_frame_jpeg_reader::build_stream_for_jpeg(
  const std::shared_ptr<media_packet>& media,
  AVFormatContext*& format_ctx,
  AVStream*& stream,
  std::string& error) const
{
  format_ctx = avformat_alloc_context();
  if (!format_ctx)
  {
    error = "failed to allocate format context";
    return false;
  }

  stream = avformat_new_stream(format_ctx, nullptr);
  if (!stream)
  {
    error = "failed to allocate stream";
    return false;
  }

  stream->time_base = media->time_base;
  if (avcodec_parameters_copy(stream->codecpar, media->codec_parameters.get()) < 0)
  {
    error = "failed to copy codec parameters";
    return false;
  }

  return true;
}

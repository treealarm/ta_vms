#pragma once

#include <string>
#include <vector>

#include "reader/snapshot_reader.h"
#include "stream/jpeg_streamer.h"
#include "writer/sync_packet_transcoder.h"

class archive_frame_jpeg_reader
{
public:
  archive_frame_jpeg_reader();

  bool read_frame_at(
    const std::string& file_path,
    int64_t offset_ms,
    std::vector<uint8_t>& jpeg_frame,
    std::string& error);

private:
  bool build_stream_for_jpeg(
    const std::shared_ptr<media_packet>& media,
    AVFormatContext*& format_ctx,
    AVStream*& stream,
    std::string& error) const;

private:
  sync_packet_transcoder m_packet_transcoder;
};

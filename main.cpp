extern "C" {
#include <libavformat/avformat.h>
#include <libavutil/avutil.h>
#include <libavutil/time.h>
}
#include <iostream>
#include <string>

struct AVPacketDeleter
{
  void operator()(AVPacket* pkt) const noexcept
  {
    if (pkt)
    {
      av_packet_free(&pkt);
    }
  }
};

using AVPacketPtr = std::unique_ptr<AVPacket, AVPacketDeleter>;

int main(int argc, char* argv[])
{
  const char* rtsp_url =
    "rtsp://orwell:elvees@172.16.254.105:554/cam/realmonitor?channel=1&subtype=0&unicast=true&proto=Onvif";
  if (argc > 1)
    rtsp_url = argv[1];

  const char* out_file = "record_10s.mp4";

  av_log_set_level(AV_LOG_INFO);
  avformat_network_init();

  AVFormatContext* in_ctx = nullptr;
  AVFormatContext* out_ctx = nullptr;

  AVDictionary* opts = nullptr;
  av_dict_set(&opts, "rtsp_transport", "tcp", 0);
  av_dict_set(&opts, "stimeout", "5000000", 0);

  // Открываем RTSP
  if (avformat_open_input(&in_ctx, rtsp_url, nullptr, &opts) < 0) {
    std::cerr << "Failed to open RTSP\n";
    return -1;
  }

  if (avformat_find_stream_info(in_ctx, nullptr) < 0) {
    std::cerr << "Failed to get stream info\n";
    return -1;
  }

  // Ищем видео поток
  int video_index = -1;
  for (unsigned i = 0; i < in_ctx->nb_streams; ++i) {
    if (in_ctx->streams[i]->codecpar->codec_type == AVMEDIA_TYPE_VIDEO) {
      video_index = i;
      break;
    }
  }

  if (video_index < 0) {
    std::cerr << "No video stream\n";
    return -1;
  }

  // Выходной MP4
  avformat_alloc_output_context2(&out_ctx, nullptr, "mp4", out_file);

  AVStream* in_stream = in_ctx->streams[video_index];
  AVStream* out_stream = avformat_new_stream(out_ctx, nullptr);

  avcodec_parameters_copy(out_stream->codecpar, in_stream->codecpar);
  out_stream->time_base = in_stream->time_base;

  if (!(out_ctx->oformat->flags & AVFMT_NOFILE)) {
    if (avio_open(&out_ctx->pb, out_file, AVIO_FLAG_WRITE) < 0) {
      std::cerr << "Failed to open output file\n";
      return -1;
    }
  }

  avformat_write_header(out_ctx, nullptr);

  // === Чтение 10 секунд ===
  AVPacketPtr pkt(av_packet_alloc());
  if (!pkt) {
    std::cerr << "Failed to allocate AVPacket\n";
    return -1;
  }

  int64_t start_pts = AV_NOPTS_VALUE;
  const int64_t duration_us = 10 * AV_TIME_BASE;

  int64_t start_time_us = av_gettime_relative();

  while (av_read_frame(in_ctx, pkt.get()) >= 0) 
  {
    if (pkt->stream_index != video_index) 
    {
      av_packet_unref(pkt.get());
      continue;
    }

    int64_t now_us = av_gettime_relative() - start_time_us;

    if (now_us > duration_us) {
      av_packet_unref(pkt.get());
      break;
    }


    pkt->stream_index = out_stream->index;

    av_interleaved_write_frame(out_ctx, pkt.get());
    av_packet_unref(pkt.get());
  }

  av_write_trailer(out_ctx);

  avformat_close_input(&in_ctx);
  if (!(out_ctx->oformat->flags & AVFMT_NOFILE))
    avio_closep(&out_ctx->pb);
  avformat_free_context(out_ctx);

  std::cout << "Saved 10 seconds to " << out_file << "\n";
  return 0;
}


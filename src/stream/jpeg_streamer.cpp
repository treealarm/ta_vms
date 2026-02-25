#include "stream/jpeg_streamer.h"

#include <arpa/inet.h>
#include <cerrno>
#include <cstring>
#include <netinet/in.h>
#include <sys/socket.h>
#include <unistd.h>

jpeg_streamer::jpeg_streamer(const std::string& host, uint16_t port)
    : m_host(host), m_port(port) {}

jpeg_streamer::~jpeg_streamer() {
  close();
}

bool jpeg_streamer::init(AVStream* in_stream, std::string& error) {
  std::scoped_lock lock(m_mutex);

  if (!in_stream) {
    error = "video stream is null";
    return false;
  }

  const auto fail = [&]() {
    cleanup_codec();
    cleanup_socket();
    return false;
  };

  if (!open_socket(error)) return fail();
  if (!init_decoder(in_stream, error)) return fail();
  if (!init_encoder(error)) return fail();

  m_decoded_frame = av_frame_alloc();
  m_scaled_frame = av_frame_alloc();
  if (!m_decoded_frame || !m_scaled_frame) {
    error = "failed to allocate frames";
    return fail();
  }

  m_scaled_frame->format = m_encoder_ctx->pix_fmt;
  m_scaled_frame->width = m_encoder_ctx->width;
  m_scaled_frame->height = m_encoder_ctx->height;

  if (av_frame_get_buffer(m_scaled_frame, 32) < 0) {
    error = "failed to allocate scaled frame buffer";
    return fail();
  }

  m_sws_ctx = sws_getContext(
      m_decoder_ctx->width,
      m_decoder_ctx->height,
      m_decoder_ctx->pix_fmt,
      m_encoder_ctx->width,
      m_encoder_ctx->height,
      m_encoder_ctx->pix_fmt,
      SWS_BILINEAR,
      nullptr,
      nullptr,
      nullptr);

  if (!m_sws_ctx) {
    error = "failed to create sws context";
    return fail();
  }

  return true;
}

void jpeg_streamer::on_packet(const std::shared_ptr<media_packet>& media) {
  std::scoped_lock lock(m_mutex);

  if (!media || !media->packet || !m_decoder_ctx || !m_encoder_ctx || m_socket_fd < 0) {
    return;
  }

  AVPacket* pkt = media->packet.get();
  if (avcodec_send_packet(m_decoder_ctx, pkt) < 0) {
    return;
  }

  while (avcodec_receive_frame(m_decoder_ctx, m_decoded_frame) == 0) {
    if (av_frame_make_writable(m_scaled_frame) < 0) {
      continue;
    }

    sws_scale(m_sws_ctx,
              m_decoded_frame->data,
              m_decoded_frame->linesize,
              0,
              m_decoder_ctx->height,
              m_scaled_frame->data,
              m_scaled_frame->linesize);

    if (avcodec_send_frame(m_encoder_ctx, m_scaled_frame) < 0) {
      continue;
    }

    AVPacket* out_pkt = av_packet_alloc();
    if (!out_pkt) {
      continue;
    }

    while (avcodec_receive_packet(m_encoder_ctx, out_pkt) == 0) {
      send_frame(out_pkt->data, out_pkt->size);
      av_packet_unref(out_pkt);
    }

    av_packet_free(&out_pkt);
    av_frame_unref(m_decoded_frame);
  }
}

void jpeg_streamer::close() {
  std::scoped_lock lock(m_mutex);
  cleanup_codec();
  cleanup_socket();
}

bool jpeg_streamer::open_socket(std::string& error) {
  m_socket_fd = socket(AF_INET, SOCK_STREAM, 0);
  if (m_socket_fd < 0) {
    error = "failed to create socket";
    return false;
  }

  sockaddr_in addr{};
  addr.sin_family = AF_INET;
  addr.sin_port = htons(m_port);

  if (inet_pton(AF_INET, m_host.c_str(), &addr.sin_addr) <= 0) {
    error = "invalid target host";
    return false;
  }

  if (connect(m_socket_fd, reinterpret_cast<sockaddr*>(&addr), sizeof(addr)) < 0) {
    error = "failed to connect to target: " + std::string(std::strerror(errno));
    return false;
  }

  return true;
}

bool jpeg_streamer::init_decoder(AVStream* in_stream, std::string& error) {
  const AVCodec* decoder = avcodec_find_decoder(in_stream->codecpar->codec_id);
  if (!decoder) {
    error = "failed to find input decoder";
    return false;
  }

  m_decoder_ctx = avcodec_alloc_context3(decoder);
  if (!m_decoder_ctx) {
    error = "failed to allocate decoder context";
    return false;
  }

  if (avcodec_parameters_to_context(m_decoder_ctx, in_stream->codecpar) < 0) {
    error = "failed to copy stream params to decoder";
    return false;
  }

  if (avcodec_open2(m_decoder_ctx, decoder, nullptr) < 0) {
    error = "failed to open decoder";
    return false;
  }

  return true;
}

bool jpeg_streamer::init_encoder(std::string& error) {
  const AVCodec* encoder = avcodec_find_encoder(AV_CODEC_ID_MJPEG);
  if (!encoder) {
    error = "failed to find mjpeg encoder";
    return false;
  }

  m_encoder_ctx = avcodec_alloc_context3(encoder);
  if (!m_encoder_ctx) {
    error = "failed to allocate encoder context";
    return false;
  }

  m_encoder_ctx->time_base = AVRational{1, 25};
  m_encoder_ctx->pix_fmt = AV_PIX_FMT_YUVJ420P;
  m_encoder_ctx->width = m_decoder_ctx->width;
  m_encoder_ctx->height = m_decoder_ctx->height;
  m_encoder_ctx->compression_level = 2;

  if (avcodec_open2(m_encoder_ctx, encoder, nullptr) < 0) {
    error = "failed to open mjpeg encoder";
    return false;
  }

  return true;
}

bool jpeg_streamer::send_frame(const uint8_t* data, size_t size) {
  const uint32_t net_size = htonl(static_cast<uint32_t>(size));
  if (::send(m_socket_fd, &net_size, sizeof(net_size), MSG_NOSIGNAL) != sizeof(net_size)) {
    return false;
  }

  size_t sent = 0;
  while (sent < size) {
    const auto res = ::send(m_socket_fd, data + sent, size - sent, MSG_NOSIGNAL);
    if (res <= 0) {
      return false;
    }
    sent += static_cast<size_t>(res);
  }

  return true;
}

void jpeg_streamer::cleanup_codec() {
  if (m_sws_ctx) {
    sws_freeContext(m_sws_ctx);
    m_sws_ctx = nullptr;
  }

  if (m_decoded_frame) {
    av_frame_free(&m_decoded_frame);
  }

  if (m_scaled_frame) {
    av_frame_free(&m_scaled_frame);
  }

  if (m_decoder_ctx) {
    avcodec_free_context(&m_decoder_ctx);
  }

  if (m_encoder_ctx) {
    avcodec_free_context(&m_encoder_ctx);
  }
}

void jpeg_streamer::cleanup_socket() {
  if (m_socket_fd >= 0) {
    ::close(m_socket_fd);
    m_socket_fd = -1;
  }
}

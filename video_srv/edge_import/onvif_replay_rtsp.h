#pragma once

#include <chrono>
#include <cstdint>
#include <memory>
#include <string>
#include <string_view>
#include <vector>

extern "C" {
#include <libavcodec/packet.h>
#include <libavformat/avformat.h>
}

#include "edge_import/tcp_socket.h"

/// Minimal ONVIF Replay RTSP client.
///
/// Stock FFmpeg always sends `Range: npt=…` on PLAY and cannot emit ONVIF
/// `Require: onvif-replay` / `Range: clock=` / `Rate-Control: no`. This class
/// does DESCRIBE/SETUP/PLAY itself, then depacketizes interleaved RTP into
/// `AVPacket`s for the edge-import muxer.
///
/// Supported video codecs from SDP/RTP: H.264, H.265/HEVC, JPEG (RFC 2435).
/// Audio: G.711 µ-law/A-law, G.726 and AAC. The first three are payload-in, payload-out — one RTP
/// packet is one chunk of audio with no header of its own — so only AAC needs real depacketization
/// (RFC 3640 AU headers plus AudioSpecificConfig from the SDP `config=` parameter).
class onvif_replay_rtsp
{
public:
  enum class video_codec
  {
    unknown,
    h264,
    hevc,
    jpeg,
  };

  enum class audio_codec
  {
    unknown,
    pcmu,   ///< G.711 µ-law, RTP payload type 0
    pcma,   ///< G.711 A-law, RTP payload type 8
    g726,   ///< G.726 ADPCM, dynamic payload type
    aac,    ///< mpeg4-generic (RFC 3640)
  };

  onvif_replay_rtsp() = default;
  ~onvif_replay_rtsp();

  onvif_replay_rtsp(const onvif_replay_rtsp&) = delete;
  onvif_replay_rtsp& operator=(const onvif_replay_rtsp&) = delete;

  [[nodiscard]] bool open(
    const std::string& rtsp_url,
    const std::string& user,
    const std::string& password,
    std::chrono::system_clock::time_point clock_start,
    std::chrono::system_clock::time_point clock_end,
    std::string& error);

  /// Blocking read of the next video packet. 0 / AVERROR_EOF / negative AVERROR.
  [[nodiscard]] int read_frame(AVPacket* packet);

  [[nodiscard]] AVFormatContext* format_context() const { return m_fmt.get(); }
  [[nodiscard]] video_codec codec() const { return m_codec; }
  [[nodiscard]] bool has_audio() const { return m_audio_stream_index >= 0; }

  void close();

private:
  struct track
  {
    std::string control_url;
    int payload_type = -1;
    int clock_rate = 90000;
    int interleaved = -1;
    bool is_video = false;
    bool is_audio = false;
    video_codec codec = video_codec::unknown;
    audio_codec acodec = audio_codec::unknown;
    int channels = 1;
    /// AAC: AudioSpecificConfig from the SDP `config=` parameter, and the AU-header field widths.
    std::vector<uint8_t> audio_config;
    int size_length = 13;
    int index_length = 3;
    int index_delta_length = 3;
    std::string sprop_parameter_sets;
    std::string sprop_vps;
    std::string sprop_sps;
    std::string sprop_pps;
  };

  [[nodiscard]] bool tcp_connect(const std::string& host, int port, std::string& error);
  [[nodiscard]] bool rtsp_exchange(
    const std::string& method,
    const std::string& url,
    const std::string& extra_headers,
    int& status,
    std::string& headers,
    std::string& body,
    std::string& error);
  [[nodiscard]] bool ensure_auth(
    const std::string& method,
    const std::string& url,
    int status,
    const std::string& response_headers,
    std::string& error);
  [[nodiscard]] std::string authorization_header(
    const std::string& method,
    const std::string& url) const;
  [[nodiscard]] bool parse_sdp(const std::string& sdp, std::string& error);
  [[nodiscard]] bool setup_tracks(std::string& error);
  [[nodiscard]] bool play_clock_range(
    std::chrono::system_clock::time_point clock_start,
    std::chrono::system_clock::time_point clock_end,
    std::string& error);
  [[nodiscard]] bool build_format_context(std::string& error);
  [[nodiscard]] bool fill_extradata_from_sprop(std::string& error);
  [[nodiscard]] bool probe_dimensions_from_sps(const std::vector<uint8_t>& sps);
  [[nodiscard]] bool probe_dims_from_extradata(AVCodecID codec_id);
  [[nodiscard]] int read_interleaved_rtp(std::vector<uint8_t>& rtp, int& channel);
  [[nodiscard]] int handle_video_rtp(const uint8_t* data, int size, AVPacket* packet);
  [[nodiscard]] int handle_audio_rtp(const uint8_t* data, int size, AVPacket* packet);
  [[nodiscard]] int emit_audio_packet(
    const uint8_t* data,
    int size,
    uint32_t rtp_ts,
    int au_index,
    AVPacket* packet);
  [[nodiscard]] int handle_h264_payload(
    const uint8_t* payload,
    int payload_size,
    uint32_t rtp_ts,
    bool marker,
    AVPacket* packet);
  [[nodiscard]] int handle_hevc_payload(
    const uint8_t* payload,
    int payload_size,
    uint32_t rtp_ts,
    bool marker,
    AVPacket* packet);
  [[nodiscard]] int handle_jpeg_payload(
    const uint8_t* payload,
    int payload_size,
    uint32_t rtp_ts,
    bool marker,
    AVPacket* packet);
  [[nodiscard]] int emit_length_prefixed_nal(
    const uint8_t* nal,
    int nal_size,
    uint32_t rtp_ts,
    bool key,
    AVPacket* packet);
  void apply_extradata_to_stream();
  void reset_fu();
  void reset_jpeg();

  static std::string format_clock(std::chrono::system_clock::time_point tp);
  static std::string md5_hex(const std::string& data);
  static std::vector<uint8_t> base64_decode(const std::string& in);
  static std::vector<uint8_t> build_avcc(
    const std::vector<uint8_t>& sps,
    const std::vector<uint8_t>& pps);
  static std::vector<uint8_t> hex_decode(const std::string& in);
  static std::vector<uint8_t> build_hvcc(
    const std::vector<uint8_t>& vps,
    const std::vector<uint8_t>& sps,
    const std::vector<uint8_t>& pps);
  static std::string fmtp_param(const std::string& line, std::string_view key);

  tcp_socket m_sock;
  int m_cseq = 1;
  std::string m_session;
  std::string m_base_url;
  std::string m_user;
  std::string m_password;
  std::string m_realm;
  std::string m_nonce;
  std::string m_opaque;
  bool m_auth_digest = false;
  bool m_auth_basic = false;
  std::string m_rx_pending;

  std::vector<track> m_tracks;
  int m_video_track = -1;
  int m_audio_track = -1;
  /// Index of the audio stream in m_fmt, or -1 when the recording carries no audio.
  int m_audio_stream_index = -1;
  bool m_have_audio_base_ts = false;
  int64_t m_audio_base_rtp_ts = 0;
  int m_next_interleaved = 0;
  video_codec m_codec = video_codec::unknown;

  std::vector<uint8_t> m_fu_buf;
  bool m_fu_active = false;
  uint8_t m_fu_nal_header = 0;
  uint16_t m_fu_hevc_nal_hdr = 0;
  int64_t m_fu_rtp_ts = 0;
  bool m_have_base_ts = false;
  int64_t m_base_rtp_ts = 0;

  // JPEG (RFC 2435) reassembly across RTP fragments.
  std::vector<uint8_t> m_jpeg_buf;
  uint32_t m_jpeg_rtp_ts = 0;
  bool m_jpeg_active = false;
  /// Parameter sets seen in the stream while extradata is still being assembled.
  ///
  /// Kept apart from the FU reassembly buffer on purpose: every single-NAL packet resets that one,
  /// including the packet carrying the next parameter set, so a stash placed there is wiped exactly
  /// when it is about to be used. Cameras whose SDP has no sprop-parameter-sets depend on this
  /// path — without it they produce no packets at all.
  std::vector<uint8_t> m_param_vps;
  std::vector<uint8_t> m_param_sps;

  int m_width = 0;
  int m_height = 0;
  std::vector<uint8_t> m_extradata;

  struct fmt_free
  {
    void operator()(AVFormatContext* ctx) const noexcept
    {
      if (ctx)
      {
        avformat_free_context(ctx);
      }
    }
  };
  std::unique_ptr<AVFormatContext, fmt_free> m_fmt;
};

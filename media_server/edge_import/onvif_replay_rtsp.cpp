#include "edge_import/onvif_replay_rtsp.h"

#include <algorithm>
#include <array>
#include <cstring>
#include <ctime>
#include <format>
#include <sstream>
#include <string_view>

extern "C" {
#include <libavcodec/avcodec.h>
#include <libavutil/avstring.h>
#include <libavutil/base64.h>
#include <libavutil/error.h>
#include <libavutil/md5.h>
#include <libavutil/mem.h>
}

#include "edge_import/tcp_socket.h"
#include "logger/logger.h"

namespace {

constexpr auto k_io_timeout = std::chrono::milliseconds(15000);
constexpr int k_rtp_header_min = 12;
/// One AAC access unit is 1024 samples in every profile ONVIF cameras use (LC / HE at the core).
constexpr int k_aac_frame_samples = 1024;

std::string trim_copy(std::string_view s)
{
  while (!s.empty() && (s.front() == ' ' || s.front() == '\t' || s.front() == '\r' ||
                        s.front() == '\n'))
  {
    s.remove_prefix(1);
  }
  while (!s.empty() && (s.back() == ' ' || s.back() == '\t' || s.back() == '\r' ||
                        s.back() == '\n'))
  {
    s.remove_suffix(1);
  }
  return std::string(s);
}

std::string header_value(const std::string& headers, std::string_view key)
{
  const std::string needle = std::string(key) + ":";
  std::string lower_headers = headers;
  for (char& c : lower_headers)
  {
    if (c >= 'A' && c <= 'Z')
    {
      c = static_cast<char>(c - 'A' + 'a');
    }
  }
  std::string lower_key = needle;
  for (char& c : lower_key)
  {
    if (c >= 'A' && c <= 'Z')
    {
      c = static_cast<char>(c - 'A' + 'a');
    }
  }
  const auto pos = lower_headers.find(lower_key);
  if (pos == std::string::npos)
  {
    return {};
  }
  const auto line_start = headers.find(':', pos);
  if (line_start == std::string::npos)
  {
    return {};
  }
  auto line_end = headers.find('\n', line_start);
  if (line_end == std::string::npos)
  {
    line_end = headers.size();
  }
  return trim_copy(headers.substr(line_start + 1, line_end - line_start - 1));
}

std::string digest_param(const std::string& challenge, std::string_view name)
{
  const std::string key = std::string(name) + "=";
  auto pos = challenge.find(key);
  if (pos == std::string::npos)
  {
    return {};
  }
  pos += key.size();
  if (pos < challenge.size() && challenge[pos] == '"')
  {
    ++pos;
    const auto end = challenge.find('"', pos);
    if (end == std::string::npos)
    {
      return {};
    }
    return challenge.substr(pos, end - pos);
  }
  const auto end = challenge.find_first_of(", ", pos);
  return challenge.substr(pos, end == std::string::npos ? std::string::npos : end - pos);
}

bool is_vcl_nal(uint8_t nal_type)
{
  return nal_type == 1 || nal_type == 5;
}

/// H.264 RBSP bit reader (Exp-Golomb). Used only to pull width/height from SPS.
class sps_bit_reader
{
public:
  sps_bit_reader(const uint8_t* data, size_t size)
    : m_data(data)
    , m_size(size)
  {
  }

  [[nodiscard]] bool ok() const { return !m_error; }

  uint32_t u(int n)
  {
    uint32_t v = 0;
    for (int i = 0; i < n; ++i)
    {
      v = (v << 1) | read_bit();
    }
    return v;
  }

  uint32_t ue()
  {
    int zeros = 0;
    while (ok() && read_bit() == 0)
    {
      ++zeros;
      if (zeros > 31)
      {
        m_error = true;
        return 0;
      }
    }
    if (!ok())
    {
      return 0;
    }
    uint32_t v = u(zeros);
    return ((1u << zeros) - 1u) + v;
  }

  int32_t se()
  {
    const uint32_t v = ue();
    if ((v & 1u) != 0)
    {
      return static_cast<int32_t>((v + 1) / 2);
    }
    return -static_cast<int32_t>(v / 2);
  }

private:
  uint32_t read_bit()
  {
    if (m_error || m_byte >= m_size)
    {
      m_error = true;
      return 0;
    }
    const uint32_t bit = (m_data[m_byte] >> (7 - m_bit)) & 1u;
    if (++m_bit == 8)
    {
      m_bit = 0;
      ++m_byte;
    }
    return bit;
  }

  const uint8_t* m_data = nullptr;
  size_t m_size = 0;
  size_t m_byte = 0;
  int m_bit = 0;
  bool m_error = false;
};

/// Strip emulation prevention bytes (0x00 0x00 0x03 → 0x00 0x00).
std::vector<uint8_t> sps_rbsp(const uint8_t* nal, size_t size)
{
  std::vector<uint8_t> out;
  out.reserve(size);
  for (size_t i = 0; i < size; ++i)
  {
    if (i + 2 < size && nal[i] == 0 && nal[i + 1] == 0 && nal[i + 2] == 3)
    {
      out.push_back(0);
      out.push_back(0);
      i += 2;
      continue;
    }
    out.push_back(nal[i]);
  }
  return out;
}

bool parse_sps_dimensions(const std::vector<uint8_t>& sps_nal, int& width, int& height)
{
  // Expect NAL with header byte (type 7). Some cameras omit it — tolerate both.
  size_t off = 0;
  if (sps_nal.empty())
  {
    return false;
  }
  if ((sps_nal[0] & 0x1F) == 7)
  {
    off = 1;
  }
  if (off >= sps_nal.size())
  {
    return false;
  }
  const auto rbsp = sps_rbsp(sps_nal.data() + off, sps_nal.size() - off);
  if (rbsp.size() < 3)
  {
    return false;
  }
  sps_bit_reader br(rbsp.data(), rbsp.size());
  const uint32_t profile_idc = br.u(8);
  br.u(8);  // constraint flags + reserved
  br.u(8);  // level_idc
  br.ue();  // seq_parameter_set_id

  uint32_t chroma_format_idc = 1;
  if (profile_idc == 100 || profile_idc == 110 || profile_idc == 122 ||
      profile_idc == 244 || profile_idc == 44 || profile_idc == 83 ||
      profile_idc == 86 || profile_idc == 118 || profile_idc == 128 ||
      profile_idc == 138 || profile_idc == 139 || profile_idc == 134 ||
      profile_idc == 135)
  {
    chroma_format_idc = br.ue();
    if (chroma_format_idc == 3)
    {
      br.u(1);  // separate_colour_plane_flag
    }
    br.ue();  // bit_depth_luma_minus8
    br.ue();  // bit_depth_chroma_minus8
    br.u(1);  // qpprime_y_zero_transform_bypass_flag
    if (br.u(1))
    {  // seq_scaling_matrix_present_flag
      const int count = (chroma_format_idc != 3) ? 8 : 12;
      for (int i = 0; i < count && br.ok(); ++i)
      {
        if (!br.u(1))
        {
          continue;
        }
        const int last = (i < 6) ? 16 : 64;
        int32_t next = 8;
        int32_t last_scale = 8;
        for (int j = 0; j < last && br.ok(); ++j)
        {
          const int32_t delta = br.se();
          next = (last_scale + delta + 256) % 256;
          last_scale = next;
        }
      }
    }
  }

  br.ue();  // log2_max_frame_num_minus4
  const uint32_t poc_type = br.ue();
  if (poc_type == 0)
  {
    br.ue();
  }
  else if (poc_type == 1)
  {
    br.u(1);
    br.se();
    br.se();
    const uint32_t n = br.ue();
    for (uint32_t i = 0; i < n && br.ok(); ++i)
    {
      br.se();
    }
  }
  br.ue();  // max_num_ref_frames
  br.u(1);  // gaps_in_frame_num_value_allowed_flag
  const uint32_t pic_width_in_mbs_minus1 = br.ue();
  const uint32_t pic_height_in_map_units_minus1 = br.ue();
  const uint32_t frame_mbs_only_flag = br.u(1);
  if (!frame_mbs_only_flag)
  {
    br.u(1);  // mb_adaptive_frame_field_flag
  }
  br.u(1);  // direct_8x8_inference_flag
  uint32_t crop_left = 0;
  uint32_t crop_right = 0;
  uint32_t crop_top = 0;
  uint32_t crop_bottom = 0;
  if (br.u(1))
  {  // frame_cropping_flag
    crop_left = br.ue();
    crop_right = br.ue();
    crop_top = br.ue();
    crop_bottom = br.ue();
  }
  if (!br.ok())
  {
    return false;
  }

  int w = static_cast<int>((pic_width_in_mbs_minus1 + 1) * 16);
  int h = static_cast<int>((pic_height_in_map_units_minus1 + 1) * 16);
  if (!frame_mbs_only_flag)
  {
    h *= 2;
  }
  int crop_unit_x = 1;
  int crop_unit_y = 2 - static_cast<int>(frame_mbs_only_flag);
  if (chroma_format_idc == 1)
  {  // 4:2:0
    crop_unit_x = 2;
    crop_unit_y = 2 * (2 - static_cast<int>(frame_mbs_only_flag));
  }
  else if (chroma_format_idc == 2)
  {  // 4:2:2
    crop_unit_x = 2;
    crop_unit_y = 2 - static_cast<int>(frame_mbs_only_flag);
  }
  else if (chroma_format_idc == 3)
  {  // 4:4:4
    crop_unit_x = 1;
    crop_unit_y = 2 - static_cast<int>(frame_mbs_only_flag);
  }
  w -= static_cast<int>(crop_left + crop_right) * crop_unit_x;
  h -= static_cast<int>(crop_top + crop_bottom) * crop_unit_y;
  if (w <= 0 || h <= 0)
  {
    return false;
  }
  width = w;
  height = h;
  return true;
}

}  // namespace

onvif_replay_rtsp::~onvif_replay_rtsp()
{
  close();
}

void onvif_replay_rtsp::close()
{
  if (m_sock.is_open() && !m_session.empty() && !m_base_url.empty())
  {
    int status = 0;
    std::string headers;
    std::string body;
    std::string error;
    const std::string extra = "Session: " + m_session + "\r\n";
    (void)rtsp_exchange("TEARDOWN", m_base_url, extra, status, headers, body, error);
  }
  m_sock.close();
  m_fmt.reset();
  m_tracks.clear();
  m_video_track = -1;
  m_session.clear();
  m_extradata.clear();
  m_rx_pending.clear();
  reset_fu();
  reset_jpeg();
  m_param_vps.clear();
  m_param_sps.clear();
  m_codec = video_codec::unknown;
}

std::string onvif_replay_rtsp::format_clock(std::chrono::system_clock::time_point tp)
{
  const std::time_t sec = std::chrono::system_clock::to_time_t(tp);
  const auto ms = std::chrono::duration_cast<std::chrono::milliseconds>(
                    tp.time_since_epoch())
                    .count() %
                  1000;
  // Both spellings of the thread-safe UTC conversion: POSIX has gmtime_r, Windows has gmtime_s with
  // the arguments the other way round. Plain gmtime is the one thing that must not be used here —
  // it hands back a pointer into shared storage, and this runs on the import thread while other
  // threads format their own timestamps.
  std::tm tm{};
#ifdef _WIN32
  gmtime_s(&tm, &sec);
#else
  gmtime_r(&sec, &tm);
#endif
  return std::format(
    "{:04d}{:02d}{:02d}T{:02d}{:02d}{:02d}.{:03d}Z",
    tm.tm_year + 1900,
    tm.tm_mon + 1,
    tm.tm_mday,
    tm.tm_hour,
    tm.tm_min,
    tm.tm_sec,
    static_cast<int>(ms < 0 ? -ms : ms));
}

std::vector<uint8_t> onvif_replay_rtsp::hex_decode(const std::string& in)
{
  auto nibble = [](char c) -> int {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
  };

  std::vector<uint8_t> out;
  int high = -1;
  for (const char c : in)
  {
    const int v = nibble(c);
    if (v < 0)
    {
      continue;
    }
    if (high < 0)
    {
      high = v;
      continue;
    }
    out.push_back(static_cast<uint8_t>((high << 4) | v));
    high = -1;
  }
  return out;
}

std::string onvif_replay_rtsp::md5_hex(const std::string& data)
{
  std::array<uint8_t, 16> digest{};
  av_md5_sum(digest.data(), reinterpret_cast<const uint8_t*>(data.data()), data.size());
  static constexpr char hex[] = "0123456789abcdef";
  std::string out;
  out.resize(32);
  for (int i = 0; i < 16; ++i)
  {
    out[static_cast<size_t>(i) * 2] = hex[digest[static_cast<size_t>(i)] >> 4];
    out[static_cast<size_t>(i) * 2 + 1] = hex[digest[static_cast<size_t>(i)] & 0x0F];
  }
  return out;
}

std::vector<uint8_t> onvif_replay_rtsp::base64_decode(const std::string& in)
{
  std::vector<uint8_t> out(in.size());
  const int n = av_base64_decode(out.data(), in.c_str(), static_cast<int>(out.size()));
  if (n < 0)
  {
    return {};
  }
  out.resize(static_cast<size_t>(n));
  return out;
}

std::vector<uint8_t> onvif_replay_rtsp::build_avcc(
  const std::vector<uint8_t>& sps,
  const std::vector<uint8_t>& pps)
{
  if (sps.size() < 4 || pps.empty())
  {
    return {};
  }
  std::vector<uint8_t> avcc;
  avcc.reserve(11 + sps.size() + pps.size());
  avcc.push_back(1);
  avcc.push_back(sps[1]);
  avcc.push_back(sps[2]);
  avcc.push_back(sps[3]);
  avcc.push_back(0xFF);  // lengthSizeMinusOne = 3
  avcc.push_back(0xE1);  // one SPS
  avcc.push_back(static_cast<uint8_t>((sps.size() >> 8) & 0xFF));
  avcc.push_back(static_cast<uint8_t>(sps.size() & 0xFF));
  avcc.insert(avcc.end(), sps.begin(), sps.end());
  avcc.push_back(1);  // one PPS
  avcc.push_back(static_cast<uint8_t>((pps.size() >> 8) & 0xFF));
  avcc.push_back(static_cast<uint8_t>(pps.size() & 0xFF));
  avcc.insert(avcc.end(), pps.begin(), pps.end());
  return avcc;
}

bool onvif_replay_rtsp::tcp_connect(const std::string& host, int port, std::string& error)
{
  return m_sock.connect(host, port, error);
}

std::string onvif_replay_rtsp::authorization_header(
  const std::string& method,
  const std::string& url) const
{
  if (m_auth_basic)
  {
    const std::string token = m_user + ":" + m_password;
    char b64[512];
    if (!av_base64_encode(b64, sizeof(b64), reinterpret_cast<const uint8_t*>(token.data()),
                          static_cast<int>(token.size())))
    {
      return {};
    }
    return std::string("Authorization: Basic ") + b64 + "\r\n";
  }
  if (!m_auth_digest || m_realm.empty() || m_nonce.empty())
  {
    return {};
  }
  const std::string ha1 = md5_hex(m_user + ":" + m_realm + ":" + m_password);
  const std::string ha2 = md5_hex(method + ":" + url);
  const std::string response = md5_hex(ha1 + ":" + m_nonce + ":" + ha2);
  std::ostringstream oss;
  oss << "Authorization: Digest username=\"" << m_user << "\", realm=\"" << m_realm
      << "\", nonce=\"" << m_nonce << "\", uri=\"" << url << "\", response=\"" << response
      << "\"";
  if (!m_opaque.empty())
  {
    oss << ", opaque=\"" << m_opaque << "\"";
  }
  oss << "\r\n";
  return oss.str();
}

bool onvif_replay_rtsp::ensure_auth(
  const std::string& method,
  const std::string& url,
  int status,
  const std::string& response_headers,
  std::string& error)
{
  (void)method;
  (void)url;
  if (status != 401)
  {
    return true;
  }
  const std::string challenge = header_value(response_headers, "WWW-Authenticate");
  if (challenge.empty())
  {
    error = "401 without WWW-Authenticate";
    return false;
  }
  if (challenge.find("Digest") != std::string::npos ||
      challenge.find("digest") != std::string::npos)
  {
    m_auth_digest = true;
    m_auth_basic = false;
    m_realm = digest_param(challenge, "realm");
    m_nonce = digest_param(challenge, "nonce");
    m_opaque = digest_param(challenge, "opaque");
    if (m_realm.empty() || m_nonce.empty())
    {
      error = "Digest challenge missing realm/nonce";
      return false;
    }
    return true;
  }
  if (challenge.find("Basic") != std::string::npos ||
      challenge.find("basic") != std::string::npos)
  {
    m_auth_basic = true;
    m_auth_digest = false;
    return true;
  }
  error = "unsupported WWW-Authenticate: " + challenge;
  return false;
}

bool onvif_replay_rtsp::rtsp_exchange(
  const std::string& method,
  const std::string& url,
  const std::string& extra_headers,
  int& status,
  std::string& headers,
  std::string& body,
  std::string& error)
{
  status = 0;
  headers.clear();
  body.clear();
  if (!m_sock.is_open())
  {
    error = "rtsp socket closed";
    return false;
  }

  std::ostringstream req;
  req << method << " " << url << " RTSP/1.0\r\n";
  req << "CSeq: " << m_cseq++ << "\r\n";
  req << "User-Agent: ta-vms-media/onvif-replay\r\n";
  if (!m_session.empty() && extra_headers.find("Session:") == std::string::npos)
  {
    req << "Session: " << m_session << "\r\n";
  }
  req << authorization_header(method, url);
  req << extra_headers;
  if (extra_headers.find("\r\n") == std::string::npos && !extra_headers.empty())
  {
    req << "\r\n";
  }
  req << "\r\n";
  const std::string request = req.str();

  if (!m_sock.send_all(request.data(), request.size()))
  {
    error = "rtsp send failed";
    return false;
  }

  std::string raw;
  raw.reserve(4096);
  for (;;)
  {
    if (!m_sock.wait_readable(k_io_timeout))
    {
      error = "rtsp recv timeout";
      return false;
    }
    std::array<char, 4096> buf{};
    const int n = m_sock.recv_some(buf.data(), buf.size());
    if (n <= 0)
    {
      error = "rtsp recv closed";
      return false;
    }
    // Skip interleaved RTP that may sneak in before the reply is complete.
    size_t off = 0;
    while (off < static_cast<size_t>(n))
    {
      if (buf[off] == '$')
      {
        if (off + 4 > static_cast<size_t>(n))
        {
          break;
        }
        const int len = (static_cast<unsigned char>(buf[off + 2]) << 8) |
                        static_cast<unsigned char>(buf[off + 3]);
        off += 4 + static_cast<size_t>(len);
        continue;
      }
      raw.append(buf.data() + off, static_cast<size_t>(n) - off);
      break;
    }
    const auto hdr_end = raw.find("\r\n\r\n");
    if (hdr_end == std::string::npos)
    {
      continue;
    }
    headers = raw.substr(0, hdr_end);
    const auto cl_str = header_value(headers, "Content-Length");
    const int content_length = cl_str.empty() ? 0 : std::atoi(cl_str.c_str());
    const size_t body_start = hdr_end + 4;
    while (raw.size() < body_start + static_cast<size_t>(content_length))
    {
      if (!m_sock.wait_readable(k_io_timeout))
      {
        error = "rtsp body timeout";
        return false;
      }
      const int n2 = m_sock.recv_some(buf.data(), buf.size());
      if (n2 <= 0)
      {
        error = "rtsp body closed";
        return false;
      }
      raw.append(buf.data(), static_cast<size_t>(n2));
    }
    body = raw.substr(body_start, static_cast<size_t>(content_length));
    const size_t consumed = body_start + static_cast<size_t>(content_length);
    if (consumed < raw.size())
    {
      m_rx_pending.append(raw.substr(consumed));
    }
    break;
  }

  if (headers.rfind("RTSP/1.0 ", 0) != 0 && headers.rfind("RTSP/1.1 ", 0) != 0)
  {
    error = "invalid RTSP status line: " + headers.substr(0, 64);
    return false;
  }
  status = std::atoi(headers.c_str() + 9);
  const auto sess = header_value(headers, "Session");
  if (!sess.empty())
  {
    const auto semi = sess.find(';');
    m_session = semi == std::string::npos ? sess : sess.substr(0, semi);
  }
  return true;
}


std::string onvif_replay_rtsp::fmtp_param(
  const std::string& line,
  std::string_view key)
{
  const std::string needle = std::string(key) + "=";
  const auto pos = line.find(needle);
  if (pos == std::string::npos)
  {
    return {};
  }
  auto value = line.substr(pos + needle.size());
  const auto end = value.find_first_of("; \t\r\n");
  if (end != std::string::npos)
  {
    value.resize(end);
  }
  return value;
}

std::vector<uint8_t> onvif_replay_rtsp::build_hvcc(
  const std::vector<uint8_t>& vps,
  const std::vector<uint8_t>& sps,
  const std::vector<uint8_t>& pps)
{
  // Minimal HVCC (ISO/IEC 14496-15). Profile/tier/level copied from SPS when
  // the NAL has a 2-byte header + enough RBSP bytes; otherwise safe defaults.
  if (vps.size() < 3 || sps.size() < 3 || pps.size() < 3)
  {
    return {};
  }
  uint8_t general_profile_space = 0;
  uint8_t general_tier_flag = 0;
  uint8_t general_profile_idc = 1;
  uint32_t general_profile_compatibility_flags = 0x60000000;
  uint8_t general_constraint[6]{};
  uint8_t general_level_idc = 120;
  uint8_t chroma_format_idc = 1;
  uint8_t bit_depth_luma_minus8 = 0;
  uint8_t bit_depth_chroma_minus8 = 0;
  if (sps.size() >= 15)
  {
    // sps[0..1] = NAL header; sps[2] starts profile_tier_level
    const size_t o = 2;
    general_profile_space = static_cast<uint8_t>((sps[o] >> 6) & 0x3);
    general_tier_flag = static_cast<uint8_t>((sps[o] >> 5) & 0x1);
    general_profile_idc = static_cast<uint8_t>(sps[o] & 0x1F);
    general_profile_compatibility_flags =
      (static_cast<uint32_t>(sps[o + 1]) << 24) |
      (static_cast<uint32_t>(sps[o + 2]) << 16) |
      (static_cast<uint32_t>(sps[o + 3]) << 8) |
      static_cast<uint32_t>(sps[o + 4]);
    for (int i = 0; i < 6; ++i)
    {
      general_constraint[i] = sps[o + 5 + static_cast<size_t>(i)];
    }
    general_level_idc = sps[o + 11];
  }

  auto append_array = [](std::vector<uint8_t>& out,
                         uint8_t nal_type,
                         const std::vector<uint8_t>& nal) {
    out.push_back(static_cast<uint8_t>(0x80 | (nal_type & 0x3F)));
    out.push_back(0);
    out.push_back(1);  // numNalus = 1
    out.push_back(static_cast<uint8_t>((nal.size() >> 8) & 0xFF));
    out.push_back(static_cast<uint8_t>(nal.size() & 0xFF));
    out.insert(out.end(), nal.begin(), nal.end());
  };

  std::vector<uint8_t> hvcc;
  hvcc.reserve(64 + vps.size() + sps.size() + pps.size());
  hvcc.push_back(1);  // configurationVersion
  hvcc.push_back(static_cast<uint8_t>(
    (general_profile_space << 6) | (general_tier_flag << 5) | general_profile_idc));
  hvcc.push_back(static_cast<uint8_t>((general_profile_compatibility_flags >> 24) & 0xFF));
  hvcc.push_back(static_cast<uint8_t>((general_profile_compatibility_flags >> 16) & 0xFF));
  hvcc.push_back(static_cast<uint8_t>((general_profile_compatibility_flags >> 8) & 0xFF));
  hvcc.push_back(static_cast<uint8_t>(general_profile_compatibility_flags & 0xFF));
  for (uint8_t b : general_constraint)
  {
    hvcc.push_back(b);
  }
  hvcc.push_back(general_level_idc);
  hvcc.push_back(0xF0);
  hvcc.push_back(0x00);  // min_spatial_segmentation_idc
  hvcc.push_back(0xFC);  // parallelismType = 0
  hvcc.push_back(static_cast<uint8_t>(0xFC | (chroma_format_idc & 0x3)));
  hvcc.push_back(static_cast<uint8_t>(0xF8 | (bit_depth_luma_minus8 & 0x7)));
  hvcc.push_back(static_cast<uint8_t>(0xF8 | (bit_depth_chroma_minus8 & 0x7)));
  hvcc.push_back(0);
  hvcc.push_back(0);     // avgFrameRate
  hvcc.push_back(0x03);  // constFramerate=0, numTemporalLayers=1, nested=0, lengthSizeMinusOne=3
  hvcc.push_back(3);     // numOfArrays
  append_array(hvcc, 32, vps);
  append_array(hvcc, 33, sps);
  append_array(hvcc, 34, pps);
  return hvcc;
}

bool onvif_replay_rtsp::parse_sdp(const std::string& sdp, std::string& error)
{
  m_tracks.clear();
  m_video_track = -1;
  m_audio_track = -1;
  m_audio_stream_index = -1;
  m_have_audio_base_ts = false;
  m_codec = video_codec::unknown;
  track current;
  bool in_media = false;
  std::istringstream iss(sdp);
  std::string line;
  while (std::getline(iss, line))
  {
    if (!line.empty() && line.back() == '\r')
    {
      line.pop_back();
    }
    if (line.rfind("m=", 0) == 0)
    {
      if (in_media && current.payload_type >= 0)
      {
        m_tracks.push_back(current);
      }
      current = track{};
      in_media = true;
      if (line.rfind("m=video", 0) == 0)
      {
        current.is_video = true;
      }
      else if (line.rfind("m=audio", 0) == 0)
      {
        current.is_audio = true;
      }
      std::istringstream ms(line);
      std::string tag;
      std::string port;
      std::string proto;
      ms >> tag >> port >> proto >> current.payload_type;
      // Static PT 26 = JPEG (RFC 2435).
      if (current.is_video && current.payload_type == 26)
      {
        current.codec = video_codec::jpeg;
        current.clock_rate = 90000;
      }
      // Static G.711 payload types carry no rtpmap on many devices, so seed them from the type
      // alone — otherwise the track is dropped as "unknown codec" and the import loses its audio.
      if (current.is_audio && current.payload_type == 0)
      {
        current.acodec = audio_codec::pcmu;
        current.clock_rate = 8000;
        current.channels = 1;
      }
      else if (current.is_audio && current.payload_type == 8)
      {
        current.acodec = audio_codec::pcma;
        current.clock_rate = 8000;
        current.channels = 1;
      }
      continue;
    }
    if (!in_media)
    {
      if (line.rfind("a=control:", 0) == 0)
      {
        const std::string ctrl = line.substr(10);
        if (ctrl.rfind("rtsp://", 0) == 0)
        {
          m_base_url = ctrl;
        }
      }
      continue;
    }
    if (line.rfind("a=control:", 0) == 0)
    {
      current.control_url = line.substr(10);
      continue;
    }
    if (line.rfind("a=rtpmap:", 0) == 0)
    {
      int pt = -1;
      char name[64]{};
      int rate = 90000;
      if (std::sscanf(line.c_str(), "a=rtpmap:%d %63[^/]/%d", &pt, name, &rate) >= 2)
      {
        if (pt == current.payload_type)
        {
          current.clock_rate = rate > 0 ? rate : 90000;
          std::string codec = name;
          for (char& c : codec)
          {
            if (c >= 'a' && c <= 'z')
            {
              c = static_cast<char>(c - 'a' + 'A');
            }
          }
          if (current.is_audio)
          {
            // rtpmap carries the channel count as a third field for audio: name/rate/channels.
            const auto slash = line.rfind('/');
            const auto rate_slash = line.find('/');
            if (slash != std::string::npos && slash != rate_slash)
            {
              const int ch = std::atoi(line.c_str() + slash + 1);
              current.channels = ch > 0 ? ch : 1;
            }
            if (codec.find("MPEG4-GENERIC") != std::string::npos)
            {
              current.acodec = audio_codec::aac;
            }
            else if (codec.find("PCMU") != std::string::npos)
            {
              current.acodec = audio_codec::pcmu;
            }
            else if (codec.find("PCMA") != std::string::npos)
            {
              current.acodec = audio_codec::pcma;
            }
            else if (codec.find("G726") != std::string::npos ||
                     codec.find("G721") != std::string::npos ||
                     codec.find("AAL2-G726") != std::string::npos)
            {
              current.acodec = audio_codec::g726;
            }
          }
          else if (codec.find("H264") != std::string::npos)
          {
            current.codec = video_codec::h264;
          }
          else if (codec.find("H265") != std::string::npos ||
                   codec.find("HEVC") != std::string::npos)
          {
            current.codec = video_codec::hevc;
          }
          else if (codec.find("JPEG") != std::string::npos ||
                   codec.find("MJPEG") != std::string::npos)
          {
            current.codec = video_codec::jpeg;
          }
        }
      }
      continue;
    }
    if (line.rfind("a=fmtp:", 0) == 0)
    {
      current.sprop_parameter_sets = fmtp_param(line, "sprop-parameter-sets");
      current.sprop_vps = fmtp_param(line, "sprop-vps");
      current.sprop_sps = fmtp_param(line, "sprop-sps");
      current.sprop_pps = fmtp_param(line, "sprop-pps");
      if (current.is_audio)
      {
        const std::string config = fmtp_param(line, "config");
        if (!config.empty())
        {
          current.audio_config = hex_decode(config);
        }
        const std::string size_len = fmtp_param(line, "sizelength");
        const std::string idx_len = fmtp_param(line, "indexlength");
        const std::string idx_delta = fmtp_param(line, "indexdeltalength");
        if (!size_len.empty()) current.size_length = std::atoi(size_len.c_str());
        if (!idx_len.empty()) current.index_length = std::atoi(idx_len.c_str());
        if (!idx_delta.empty()) current.index_delta_length = std::atoi(idx_delta.c_str());
      }
    }
  }
  if (in_media && current.payload_type >= 0)
  {
    m_tracks.push_back(current);
  }
  for (size_t i = 0; i < m_tracks.size(); ++i)
  {
    if (m_tracks[i].is_video && m_tracks[i].codec != video_codec::unknown)
    {
      m_video_track = static_cast<int>(i);
      m_codec = m_tracks[i].codec;
      break;
    }
  }
  // Audio is optional and never fatal: a recording without it, or with a codec we do not speak,
  // still has to import its video rather than fail outright.
  for (size_t i = 0; i < m_tracks.size(); ++i)
  {
    if (m_tracks[i].is_audio && m_tracks[i].acodec != audio_codec::unknown)
    {
      if (m_tracks[i].acodec == audio_codec::aac && m_tracks[i].audio_config.empty())
      {
        logger::instance().warning(
          "onvif replay: AAC track without a config= parameter, importing video only");
        break;
      }
      m_audio_track = static_cast<int>(i);
      break;
    }
  }
  if (m_video_track < 0)
  {
    error = "SDP has no H264/H265/JPEG video track";
    return false;
  }
  return true;
}

bool onvif_replay_rtsp::setup_tracks(std::string& error)
{
  auto resolve_control = [this](const std::string& ctrl) -> std::string {
    if (ctrl.rfind("rtsp://", 0) == 0)
    {
      return ctrl;
    }
    if (ctrl == "*")
    {
      return m_base_url;
    }
    if (!m_base_url.empty() && m_base_url.back() == '/')
    {
      return m_base_url + ctrl;
    }
    return m_base_url + "/" + ctrl;
  };

  for (size_t i = 0; i < m_tracks.size(); ++i)
  {
    auto& tr = m_tracks[i];
    const bool wanted = static_cast<int>(i) == m_video_track || static_cast<int>(i) == m_audio_track;
    if (!wanted)
    {
      continue;
    }
    tr.control_url = resolve_control(tr.control_url.empty() ? "*" : tr.control_url);
    tr.interleaved = m_next_interleaved;
    m_next_interleaved += 2;

    const std::string extra = std::format(
      "Transport: RTP/AVP/TCP;unicast;interleaved={}-{}\r\n"
      "Require: onvif-replay\r\n",
      tr.interleaved,
      tr.interleaved + 1);

    int status = 0;
    std::string headers;
    std::string body;
    if (!rtsp_exchange("SETUP", tr.control_url, extra, status, headers, body, error))
    {
      return false;
    }
    if (status == 401)
    {
      if (!ensure_auth("SETUP", tr.control_url, status, headers, error))
      {
        return false;
      }
      if (!rtsp_exchange("SETUP", tr.control_url, extra, status, headers, body, error))
      {
        return false;
      }
    }
    if (status == 551)
    {
      const std::string extra2 = std::format(
        "Transport: RTP/AVP/TCP;unicast;interleaved={}-{}\r\n",
        tr.interleaved,
        tr.interleaved + 1);
      if (!rtsp_exchange("SETUP", tr.control_url, extra2, status, headers, body, error))
      {
        return false;
      }
    }
    if (status < 200 || status >= 300)
    {
      error = std::format("SETUP failed status={} url={}", status, tr.control_url);
      return false;
    }
  }
  return true;
}

bool onvif_replay_rtsp::play_clock_range(
  std::chrono::system_clock::time_point clock_start,
  std::chrono::system_clock::time_point clock_end,
  std::string& error)
{
  const std::string extra = std::format(
    "Require: onvif-replay\r\n"
    "Range: clock={}-{}\r\n"
    "Rate-Control: no\r\n",
    format_clock(clock_start),
    format_clock(clock_end));

  int status = 0;
  std::string headers;
  std::string body;
  if (!rtsp_exchange("PLAY", m_base_url, extra, status, headers, body, error))
  {
    return false;
  }
  if (status == 401)
  {
    if (!ensure_auth("PLAY", m_base_url, status, headers, error))
    {
      return false;
    }
    if (!rtsp_exchange("PLAY", m_base_url, extra, status, headers, body, error))
    {
      return false;
    }
  }
  if (status < 200 || status >= 300)
  {
    error = std::format(
      "ONVIF PLAY clock={}..{} failed status={}",
      format_clock(clock_start),
      format_clock(clock_end),
      status);
    return false;
  }
  logger::instance().info(std::format(
    "onvif replay PLAY ok codec={} range clock={}-{} session='{}'",
    m_codec == video_codec::h264     ? "h264"
      : m_codec == video_codec::hevc ? "hevc"
      : m_codec == video_codec::jpeg ? "jpeg"
                                     : "?",
    format_clock(clock_start),
    format_clock(clock_end),
    m_session));
  return true;
}

bool onvif_replay_rtsp::fill_extradata_from_sprop(std::string& error)
{
  if (m_codec == video_codec::jpeg)
  {
    return true;
  }
  const auto& tr = m_tracks[static_cast<size_t>(m_video_track)];
  if (m_codec == video_codec::h264)
  {
    if (tr.sprop_parameter_sets.empty())
    {
      return true;
    }
    const auto comma = tr.sprop_parameter_sets.find(',');
    if (comma == std::string::npos)
    {
      error = "sprop-parameter-sets missing PPS";
      return false;
    }
    auto pps_b64 = tr.sprop_parameter_sets.substr(comma + 1);
    const auto next_comma = pps_b64.find(',');
    if (next_comma != std::string::npos)
    {
      pps_b64.resize(next_comma);
    }
    const auto sps = base64_decode(tr.sprop_parameter_sets.substr(0, comma));
    const auto pps = base64_decode(pps_b64);
    m_extradata = build_avcc(sps, pps);
    if (m_extradata.empty())
    {
      error = "failed to build avcC from sprop-parameter-sets";
      return false;
    }
    if (!probe_dimensions_from_sps(sps) &&
        !probe_dims_from_extradata(AV_CODEC_ID_H264))
    {
      logger::instance().warning(std::format(
        "onvif replay: H264 SPS dimensions pending (sps_bytes={})",
        sps.size()));
    }
    return true;
  }

  // HEVC: prefer sprop-vps/sps/pps; fall back to comma-separated parameter-sets.
  std::vector<uint8_t> vps;
  std::vector<uint8_t> sps;
  std::vector<uint8_t> pps;
  if (!tr.sprop_vps.empty() && !tr.sprop_sps.empty() && !tr.sprop_pps.empty())
  {
    vps = base64_decode(tr.sprop_vps);
    sps = base64_decode(tr.sprop_sps);
    pps = base64_decode(tr.sprop_pps);
  }
  else if (!tr.sprop_parameter_sets.empty())
  {
    std::vector<std::string> parts;
    std::string rest = tr.sprop_parameter_sets;
    while (!rest.empty())
    {
      const auto c = rest.find(',');
      parts.push_back(c == std::string::npos ? rest : rest.substr(0, c));
      if (c == std::string::npos)
      {
        break;
      }
      rest = rest.substr(c + 1);
    }
    if (parts.size() >= 3)
    {
      vps = base64_decode(parts[0]);
      sps = base64_decode(parts[1]);
      pps = base64_decode(parts[2]);
    }
  }
  if (vps.empty() || sps.empty() || pps.empty())
  {
    return true;  // wait for in-band parameter sets
  }
  m_extradata = build_hvcc(vps, sps, pps);
  if (m_extradata.empty())
  {
    error = "failed to build hvcC from sprop";
    return false;
  }
  if (!probe_dims_from_extradata(AV_CODEC_ID_HEVC))
  {
    logger::instance().warning(
      "onvif replay: HEVC dimensions pending after sprop");
  }
  return true;
}

bool onvif_replay_rtsp::probe_dimensions_from_sps(const std::vector<uint8_t>& sps)
{
  int w = 0;
  int h = 0;
  if (!parse_sps_dimensions(sps, w, h))
  {
    return false;
  }
  m_width = w;
  m_height = h;
  if (m_fmt && m_fmt->nb_streams > 0)
  {
    m_fmt->streams[0]->codecpar->width = m_width;
    m_fmt->streams[0]->codecpar->height = m_height;
  }
  return true;
}

bool onvif_replay_rtsp::probe_dims_from_extradata(AVCodecID codec_id)
{
  if (m_extradata.empty())
  {
    return false;
  }
  const AVCodec* dec = avcodec_find_decoder(codec_id);
  if (!dec)
  {
    return false;
  }
  AVCodecContext* ctx = avcodec_alloc_context3(dec);
  if (!ctx)
  {
    return false;
  }
  ctx->extradata = static_cast<uint8_t*>(
    av_mallocz(m_extradata.size() + AV_INPUT_BUFFER_PADDING_SIZE));
  if (!ctx->extradata)
  {
    avcodec_free_context(&ctx);
    return false;
  }
  std::memcpy(ctx->extradata, m_extradata.data(), m_extradata.size());
  ctx->extradata_size = static_cast<int>(m_extradata.size());
  const int open_rc = avcodec_open2(ctx, dec, nullptr);
  const bool ok = open_rc >= 0 && ctx->width > 0 && ctx->height > 0;
  if (ok)
  {
    m_width = ctx->width;
    m_height = ctx->height;
    if (m_fmt && m_fmt->nb_streams > 0)
    {
      m_fmt->streams[0]->codecpar->width = m_width;
      m_fmt->streams[0]->codecpar->height = m_height;
    }
  }
  avcodec_free_context(&ctx);
  return ok;
}

void onvif_replay_rtsp::apply_extradata_to_stream()
{
  if (!m_fmt || m_fmt->nb_streams == 0 || m_extradata.empty())
  {
    return;
  }
  AVCodecParameters* par = m_fmt->streams[0]->codecpar;
  par->width = m_width;
  par->height = m_height;
  av_freep(&par->extradata);
  par->extradata = static_cast<uint8_t*>(
    av_mallocz(m_extradata.size() + AV_INPUT_BUFFER_PADDING_SIZE));
  if (!par->extradata)
  {
    return;
  }
  std::memcpy(par->extradata, m_extradata.data(), m_extradata.size());
  par->extradata_size = static_cast<int>(m_extradata.size());
}

bool onvif_replay_rtsp::build_format_context(std::string& error)
{
  AVFormatContext* ctx = avformat_alloc_context();
  if (!ctx)
  {
    error = "alloc format context";
    return false;
  }
  m_fmt.reset(ctx);
  AVStream* st = avformat_new_stream(ctx, nullptr);
  if (!st)
  {
    error = "alloc video stream";
    m_fmt.reset();
    return false;
  }
  st->id = 0;
  st->time_base = AVRational{ 1, 90000 };
  st->avg_frame_rate = AVRational{ 25, 1 };
  st->codecpar->codec_type = AVMEDIA_TYPE_VIDEO;
  st->codecpar->codec_tag = 0;
  st->codecpar->width = m_width;
  st->codecpar->height = m_height;
  switch (m_codec)
  {
  case video_codec::h264:
    st->codecpar->codec_id = AV_CODEC_ID_H264;
    break;
  case video_codec::hevc:
    st->codecpar->codec_id = AV_CODEC_ID_HEVC;
    break;
  case video_codec::jpeg:
    st->codecpar->codec_id = AV_CODEC_ID_MJPEG;
    break;
  default:
    error = "unsupported video codec";
    m_fmt.reset();
    return false;
  }
  apply_extradata_to_stream();

  if (m_audio_track >= 0)
  {
    const track& atr = m_tracks[static_cast<size_t>(m_audio_track)];
    AVStream* ast = avformat_new_stream(ctx, nullptr);
    if (!ast)
    {
      error = "alloc audio stream";
      m_fmt.reset();
      return false;
    }
    ast->id = 1;
    // Its own RTP clock, not the video's 90 kHz.
    ast->time_base = AVRational{ 1, atr.clock_rate > 0 ? atr.clock_rate : 8000 };
    ast->codecpar->codec_type = AVMEDIA_TYPE_AUDIO;
    ast->codecpar->codec_tag = 0;
    ast->codecpar->sample_rate = atr.clock_rate > 0 ? atr.clock_rate : 8000;
    av_channel_layout_default(&ast->codecpar->ch_layout, atr.channels > 0 ? atr.channels : 1);
    switch (atr.acodec)
    {
    case audio_codec::pcmu:
      ast->codecpar->codec_id = AV_CODEC_ID_PCM_MULAW;
      break;
    case audio_codec::pcma:
      ast->codecpar->codec_id = AV_CODEC_ID_PCM_ALAW;
      break;
    case audio_codec::g726:
      // The RTP payload is the packed ADPCM bitstream, which is what the "big-endian" decoder wants.
      ast->codecpar->codec_id = AV_CODEC_ID_ADPCM_G726;
      break;
    case audio_codec::aac:
      ast->codecpar->codec_id = AV_CODEC_ID_AAC;
      break;
    default:
      error = "unsupported audio codec";
      m_fmt.reset();
      return false;
    }

    if (!atr.audio_config.empty())
    {
      // AudioSpecificConfig straight from the SDP: without it the AAC frames are undecodable, and
      // a muxer cannot even write a valid sample entry for them.
      ast->codecpar->extradata = static_cast<uint8_t*>(
        av_mallocz(atr.audio_config.size() + AV_INPUT_BUFFER_PADDING_SIZE));
      if (!ast->codecpar->extradata)
      {
        error = "alloc audio extradata";
        m_fmt.reset();
        return false;
      }
      std::memcpy(ast->codecpar->extradata, atr.audio_config.data(), atr.audio_config.size());
      ast->codecpar->extradata_size = static_cast<int>(atr.audio_config.size());
    }

    m_audio_stream_index = static_cast<int>(ctx->nb_streams) - 1;
  }

  return true;
}

bool onvif_replay_rtsp::open(
  const std::string& rtsp_url,
  const std::string& user,
  const std::string& password,
  std::chrono::system_clock::time_point clock_start,
  std::chrono::system_clock::time_point clock_end,
  std::string& error)
{
  close();
  m_user = user;
  m_password = password;

  char proto[32]{};
  char auth[512]{};
  char host[256]{};
  char path[1024]{};
  int port = 0;
  av_url_split(
    proto,
    sizeof(proto),
    auth,
    sizeof(auth),
    host,
    sizeof(host),
    &port,
    path,
    sizeof(path),
    rtsp_url.c_str());
  if (std::strcmp(proto, "rtsp") != 0 && std::strcmp(proto, "rtsps") != 0)
  {
    error = "replay URI is not rtsp";
    return false;
  }
  if (port <= 0)
  {
    port = 554;
  }
  if (auth[0] != '\0' && m_user.empty())
  {
    const char* colon = std::strchr(auth, ':');
    if (colon)
    {
      m_user = std::string(auth, static_cast<size_t>(colon - auth));
      m_password = colon + 1;
    }
    else
    {
      m_user = auth;
    }
  }
  m_base_url = std::format("rtsp://{}:{}{}", host, port, path[0] ? path : "/");

  if (!tcp_connect(host, port, error))
  {
    return false;
  }

  int status = 0;
  std::string headers;
  std::string body;
  if (!rtsp_exchange("DESCRIBE", m_base_url, "Accept: application/sdp\r\n", status, headers,
                     body, error))
  {
    return false;
  }
  if (status == 401)
  {
    if (!ensure_auth("DESCRIBE", m_base_url, status, headers, error))
    {
      return false;
    }
    if (!rtsp_exchange("DESCRIBE", m_base_url, "Accept: application/sdp\r\n", status, headers,
                       body, error))
    {
      return false;
    }
  }
  if (status < 200 || status >= 300)
  {
    error = std::format("DESCRIBE failed status={}", status);
    return false;
  }
  const auto content_base = header_value(headers, "Content-Base");
  if (!content_base.empty())
  {
    m_base_url = trim_copy(content_base);
  }
  if (!parse_sdp(body, error) || !fill_extradata_from_sprop(error) ||
      !setup_tracks(error) || !play_clock_range(clock_start, clock_end, error))
  {
    close();
    return false;
  }
  if (m_width <= 0 || m_height <= 0)
  {
    m_width = 0;
    m_height = 0;
  }
  if (!build_format_context(error))
  {
    close();
    return false;
  }
  return true;
}

void onvif_replay_rtsp::reset_fu()
{
  m_fu_buf.clear();
  m_fu_active = false;
  m_fu_nal_header = 0;
  m_fu_hevc_nal_hdr = 0;
  m_fu_rtp_ts = 0;
}

void onvif_replay_rtsp::reset_jpeg()
{
  m_jpeg_buf.clear();
  m_jpeg_active = false;
  m_jpeg_rtp_ts = 0;
}

int onvif_replay_rtsp::read_interleaved_rtp(std::vector<uint8_t>& rtp, int& channel)
{
  rtp.clear();
  channel = -1;

  auto read_exact = [this](uint8_t* dst, size_t need) -> int {
    size_t got = 0;
    while (got < need)
    {
      if (!m_rx_pending.empty())
      {
        const size_t n = std::min(need - got, m_rx_pending.size());
        std::memcpy(dst + got, m_rx_pending.data(), n);
        m_rx_pending.erase(0, n);
        got += n;
        continue;
      }
      if (!m_sock.wait_readable(k_io_timeout))
      {
        // After PLAY + Rate-Control: no the camera often goes silent when the
        // clock range is exhausted. Treat idle-after-media as EOF, not hard I/O
        // failure (otherwise a successful import ends as "Input/output error").
        return m_have_base_ts ? AVERROR_EOF : AVERROR(EIO);
      }
      const int n = m_sock.recv_some(dst + got, need - got);
      if (n <= 0)
      {
        return AVERROR_EOF;
      }
      got += static_cast<size_t>(n);
    }
    return 0;
  };

  for (;;)
  {
    uint8_t hdr[4];
    const int hr = read_exact(hdr, 4);
    if (hr < 0)
    {
      return hr;
    }
    if (hdr[0] != '$')
    {
      std::string discard(reinterpret_cast<char*>(hdr), 4);
      while (discard.size() < 8192 && !discard.ends_with("\r\n\r\n"))
      {
        uint8_t c;
        const int cr = read_exact(&c, 1);
        if (cr < 0)
        {
          return cr;
        }
        discard.push_back(static_cast<char>(c));
      }
      continue;
    }
    channel = hdr[1];
    const int len = (hdr[2] << 8) | hdr[3];
    if (len <= 0 || len > 65535)
    {
      return AVERROR_INVALIDDATA;
    }
    rtp.resize(static_cast<size_t>(len));
    const int br = read_exact(rtp.data(), static_cast<size_t>(len));
    if (br < 0)
    {
      return br;
    }
    return 0;
  }
}

int onvif_replay_rtsp::emit_length_prefixed_nal(
  const uint8_t* nal,
  int nal_size,
  uint32_t rtp_ts,
  bool key,
  AVPacket* packet)
{
  if (nal_size <= 0)
  {
    return AVERROR(EAGAIN);
  }
  if (m_width <= 0 || m_height <= 0 || m_extradata.empty())
  {
    return AVERROR(EAGAIN);
  }
  if (!m_have_base_ts)
  {
    m_base_rtp_ts = rtp_ts;
    m_have_base_ts = true;
  }
  const int64_t pts =
    static_cast<int64_t>(rtp_ts - static_cast<uint32_t>(m_base_rtp_ts));
  if (av_new_packet(packet, nal_size + 4) < 0)
  {
    return AVERROR(ENOMEM);
  }
  packet->data[0] = static_cast<uint8_t>((nal_size >> 24) & 0xFF);
  packet->data[1] = static_cast<uint8_t>((nal_size >> 16) & 0xFF);
  packet->data[2] = static_cast<uint8_t>((nal_size >> 8) & 0xFF);
  packet->data[3] = static_cast<uint8_t>(nal_size & 0xFF);
  std::memcpy(packet->data + 4, nal, static_cast<size_t>(nal_size));
  packet->stream_index = 0;
  packet->pts = pts;
  packet->dts = pts;
  packet->duration = 3600;
  packet->flags = key ? AV_PKT_FLAG_KEY : 0;
  return 0;
}

int onvif_replay_rtsp::handle_h264_payload(
  const uint8_t* payload,
  int payload_size,
  uint32_t rtp_ts,
  bool /*marker*/,
  AVPacket* packet)
{
  auto on_param_nal = [&](const uint8_t* nal, int nal_size) -> int {
    const uint8_t nal_type = nal[0] & 0x1F;
    if (nal_type == 7)
    {
      const std::vector<uint8_t> sps(nal, nal + nal_size);
      (void)probe_dimensions_from_sps(sps);
      if (m_extradata.empty())
      {
        m_param_sps = sps;
      }
      return AVERROR(EAGAIN);
    }
    if (nal_type == 8 && m_extradata.empty())
    {
      if (m_param_sps.empty())
      {
        return AVERROR(EAGAIN);
      }
      const std::vector<uint8_t> pps(nal, nal + nal_size);
      m_extradata = build_avcc(m_param_sps, pps);
      (void)probe_dimensions_from_sps(m_param_sps);
      apply_extradata_to_stream();
      m_param_sps.clear();
      return AVERROR(EAGAIN);
    }
    return AVERROR(EAGAIN);
  };

  const uint8_t nal_type = payload[0] & 0x1F;
  if (nal_type >= 1 && nal_type <= 23)
  {
    reset_fu();
    if (nal_type == 7 || nal_type == 8)
    {
      return on_param_nal(payload, payload_size);
    }
    if (!is_vcl_nal(nal_type))
    {
      return AVERROR(EAGAIN);
    }
    return emit_length_prefixed_nal(
      payload, payload_size, rtp_ts, nal_type == 5, packet);
  }
  if (nal_type == 28)
  {
    if (payload_size < 2)
    {
      return AVERROR(EAGAIN);
    }
    const uint8_t fu_indicator = payload[0];
    const uint8_t fu_header = payload[1];
    const bool start = (fu_header & 0x80) != 0;
    const bool end = (fu_header & 0x40) != 0;
    const uint8_t orig_type = fu_header & 0x1F;
    if (start)
    {
      m_fu_nal_header = static_cast<uint8_t>((fu_indicator & 0xE0) | orig_type);
      m_fu_buf.clear();
      m_fu_buf.push_back(m_fu_nal_header);
      m_fu_buf.insert(m_fu_buf.end(), payload + 2, payload + payload_size);
      m_fu_active = true;
      m_fu_rtp_ts = rtp_ts;
      return AVERROR(EAGAIN);
    }
    if (!m_fu_active)
    {
      return AVERROR(EAGAIN);
    }
    m_fu_buf.insert(m_fu_buf.end(), payload + 2, payload + payload_size);
    if (!end)
    {
      return AVERROR(EAGAIN);
    }
    std::vector<uint8_t> nal = std::move(m_fu_buf);
    reset_fu();
    if (orig_type == 7 || orig_type == 8)
    {
      return on_param_nal(nal.data(), static_cast<int>(nal.size()));
    }
    if (!is_vcl_nal(orig_type))
    {
      return AVERROR(EAGAIN);
    }
    return emit_length_prefixed_nal(
      nal.data(), static_cast<int>(nal.size()), rtp_ts, orig_type == 5, packet);
  }
  if (nal_type == 24)
  {
    int pos = 1;
    while (pos + 2 <= payload_size)
    {
      const int nal_size = (payload[pos] << 8) | payload[pos + 1];
      pos += 2;
      if (nal_size <= 0 || pos + nal_size > payload_size)
      {
        break;
      }
      const uint8_t t = payload[pos] & 0x1F;
      if (t == 7 || t == 8)
      {
        (void)on_param_nal(payload + pos, nal_size);
      }
      else if (is_vcl_nal(t))
      {
        const int rc = emit_length_prefixed_nal(
          payload + pos, nal_size, rtp_ts, t == 5, packet);
        if (rc == 0)
        {
          return 0;
        }
      }
      pos += nal_size;
    }
    return AVERROR(EAGAIN);
  }
  return AVERROR(EAGAIN);
}

int onvif_replay_rtsp::handle_hevc_payload(
  const uint8_t* payload,
  int payload_size,
  uint32_t rtp_ts,
  bool /*marker*/,
  AVPacket* packet)
{
  if (payload_size < 2)
  {
    return AVERROR(EAGAIN);
  }

  auto hevc_type = [](const uint8_t* nal) -> int {
    return (nal[0] >> 1) & 0x3F;
  };
  auto is_hevc_key = [](int t) {
    return t == 19 || t == 20 || t == 21;  // IDR / CRA
  };
  auto is_hevc_vcl = [](int t) { return t <= 31; };

  auto on_param = [&](const uint8_t* nal, int nal_size) -> int {
    const int t = hevc_type(nal);
    // hvcC needs all three, and they arrive as separate packets, so each is kept until the last
    // one turns up.
    if (t == 32)
    {  // VPS
      if (m_extradata.empty())
      {
        m_param_vps.assign(nal, nal + nal_size);
      }
      return AVERROR(EAGAIN);
    }
    if (t == 33)
    {  // SPS
      if (m_extradata.empty())
      {
        m_param_sps.assign(nal, nal + nal_size);
      }
      return AVERROR(EAGAIN);
    }
    if (t == 34 && m_extradata.empty())
    {  // PPS
      if (m_param_vps.empty() || m_param_sps.empty())
      {
        return AVERROR(EAGAIN);
      }
      const std::vector<uint8_t> pps(nal, nal + nal_size);
      m_extradata = build_hvcc(m_param_vps, m_param_sps, pps);
      (void)probe_dims_from_extradata(AV_CODEC_ID_HEVC);
      apply_extradata_to_stream();
      m_param_vps.clear();
      m_param_sps.clear();
      return AVERROR(EAGAIN);
    }
    return AVERROR(EAGAIN);
  };

  const int payload_type = (payload[0] >> 1) & 0x3F;
  // Single NAL unit packet (type != AP/FU)
  if (payload_type != 48 && payload_type != 49)
  {
    reset_fu();
    if (payload_type == 32 || payload_type == 33 || payload_type == 34)
    {
      return on_param(payload, payload_size);
    }
    if (!is_hevc_vcl(payload_type))
    {
      return AVERROR(EAGAIN);
    }
    return emit_length_prefixed_nal(
      payload, payload_size, rtp_ts, is_hevc_key(payload_type), packet);
  }
  if (payload_type == 49)
  {
    // FU: PayloadHdr(2) + FU header(1) + payload
    if (payload_size < 3)
    {
      return AVERROR(EAGAIN);
    }
    const uint8_t fu_header = payload[2];
    const bool start = (fu_header & 0x80) != 0;
    const bool end = (fu_header & 0x40) != 0;
    const int fu_type = fu_header & 0x3F;
    if (start)
    {
      // Reconstruct NAL header: type from FU, layerId/TID from payload hdr.
      const uint8_t nal0 =
        static_cast<uint8_t>((payload[0] & 0x81) | (static_cast<uint8_t>(fu_type) << 1));
      const uint8_t nal1 = payload[1];
      m_fu_hevc_nal_hdr = static_cast<uint16_t>((nal0 << 8) | nal1);
      m_fu_buf.clear();
      m_fu_buf.push_back(nal0);
      m_fu_buf.push_back(nal1);
      m_fu_buf.insert(m_fu_buf.end(), payload + 3, payload + payload_size);
      m_fu_active = true;
      m_fu_rtp_ts = rtp_ts;
      return AVERROR(EAGAIN);
    }
    if (!m_fu_active)
    {
      return AVERROR(EAGAIN);
    }
    m_fu_buf.insert(m_fu_buf.end(), payload + 3, payload + payload_size);
    if (!end)
    {
      return AVERROR(EAGAIN);
    }
    std::vector<uint8_t> nal = std::move(m_fu_buf);
    reset_fu();
    if (fu_type == 32 || fu_type == 33 || fu_type == 34)
    {
      return on_param(nal.data(), static_cast<int>(nal.size()));
    }
    if (!is_hevc_vcl(fu_type))
    {
      return AVERROR(EAGAIN);
    }
    return emit_length_prefixed_nal(
      nal.data(),
      static_cast<int>(nal.size()),
      rtp_ts,
      is_hevc_key(fu_type),
      packet);
  }
  if (payload_type == 48)
  {
    // Aggregation Packet: skip DONL if present — assume no DONL (sprop-max-don-diff absent).
    int pos = 2;
    while (pos + 2 <= payload_size)
    {
      const int nal_size = (payload[pos] << 8) | payload[pos + 1];
      pos += 2;
      if (nal_size < 2 || pos + nal_size > payload_size)
      {
        break;
      }
      const int t = hevc_type(payload + pos);
      if (t == 32 || t == 33 || t == 34)
      {
        (void)on_param(payload + pos, nal_size);
      }
      else if (is_hevc_vcl(t))
      {
        const int rc = emit_length_prefixed_nal(
          payload + pos, nal_size, rtp_ts, is_hevc_key(t), packet);
        if (rc == 0)
        {
          return 0;
        }
      }
      pos += nal_size;
    }
    return AVERROR(EAGAIN);
  }
  return AVERROR(EAGAIN);
}

int onvif_replay_rtsp::handle_jpeg_payload(
  const uint8_t* payload,
  int payload_size,
  uint32_t rtp_ts,
  bool marker,
  AVPacket* packet)
{
  // RFC 2435 JPEG header (8 bytes) + optional restart/q-table + entropy data.
  if (payload_size < 8)
  {
    return AVERROR(EAGAIN);
  }
  const uint8_t type_specific = payload[0];
  (void)type_specific;
  const uint32_t frag_offset =
    (static_cast<uint32_t>(payload[1]) << 16) |
    (static_cast<uint32_t>(payload[2]) << 8) |
    static_cast<uint32_t>(payload[3]);
  const uint8_t type = payload[4];
  const uint8_t q = payload[5];
  const int width = static_cast<int>(payload[6]) * 8;
  const int height = static_cast<int>(payload[7]) * 8;
  int pos = 8;

  if (width > 0 && height > 0)
  {
    m_width = width;
    m_height = height;
    if (m_fmt && m_fmt->nb_streams > 0)
    {
      m_fmt->streams[0]->codecpar->width = m_width;
      m_fmt->streams[0]->codecpar->height = m_height;
    }
  }

  // Restart marker header when type >= 64 && type < 128
  if (type >= 64 && type <= 127)
  {
    if (pos + 4 > payload_size)
    {
      return AVERROR(EAGAIN);
    }
    pos += 4;
  }
  // Quantization table header when frag_offset==0 and q>=128
  if (frag_offset == 0 && q >= 128)
  {
    if (pos + 4 > payload_size)
    {
      return AVERROR(EAGAIN);
    }
    const int qb_len = (payload[pos + 2] << 8) | payload[pos + 3];
    pos += 4;
    if (pos + qb_len > payload_size)
    {
      return AVERROR(EAGAIN);
    }
    // Skip Q-table bytes — many cameras already embed DQT in the JPEG bitstream.
    pos += qb_len;
  }

  if (pos > payload_size)
  {
    return AVERROR(EAGAIN);
  }
  const uint8_t* jpeg_part = payload + pos;
  const int jpeg_part_size = payload_size - pos;

  if (frag_offset == 0)
  {
    reset_jpeg();
    m_jpeg_active = true;
    m_jpeg_rtp_ts = rtp_ts;
    m_jpeg_buf.clear();
    // Ensure SOI if camera omitted it (rare).
    if (jpeg_part_size < 2 || jpeg_part[0] != 0xFF || jpeg_part[1] != 0xD8)
    {
      m_jpeg_buf.push_back(0xFF);
      m_jpeg_buf.push_back(0xD8);
    }
  }
  else if (!m_jpeg_active || m_jpeg_rtp_ts != rtp_ts)
  {
    return AVERROR(EAGAIN);
  }

  m_jpeg_buf.insert(
    m_jpeg_buf.end(), jpeg_part, jpeg_part + jpeg_part_size);

  if (!marker)
  {
    return AVERROR(EAGAIN);
  }

  if (m_width <= 0 || m_height <= 0 || m_jpeg_buf.size() < 4)
  {
    reset_jpeg();
    return AVERROR(EAGAIN);
  }
  if (!m_have_base_ts)
  {
    m_base_rtp_ts = rtp_ts;
    m_have_base_ts = true;
  }
  const int64_t pts =
    static_cast<int64_t>(rtp_ts - static_cast<uint32_t>(m_base_rtp_ts));
  if (av_new_packet(packet, static_cast<int>(m_jpeg_buf.size())) < 0)
  {
    reset_jpeg();
    return AVERROR(ENOMEM);
  }
  std::memcpy(packet->data, m_jpeg_buf.data(), m_jpeg_buf.size());
  packet->stream_index = 0;
  packet->pts = pts;
  packet->dts = pts;
  packet->duration = 3600;
  packet->flags = AV_PKT_FLAG_KEY;
  reset_jpeg();
  return 0;
}

int onvif_replay_rtsp::handle_video_rtp(
  const uint8_t* data,
  int size,
  AVPacket* packet)
{
  if (size < k_rtp_header_min)
  {
    return AVERROR(EAGAIN);
  }
  const int cc = data[0] & 0x0F;
  const bool marker = (data[1] & 0x80) != 0;
  const int pt = data[1] & 0x7F;
  const uint32_t rtp_ts = (static_cast<uint32_t>(data[4]) << 24) |
                          (static_cast<uint32_t>(data[5]) << 16) |
                          (static_cast<uint32_t>(data[6]) << 8) |
                          static_cast<uint32_t>(data[7]);
  int offset = k_rtp_header_min + cc * 4;
  if (offset >= size)
  {
    return AVERROR(EAGAIN);
  }
  if (data[0] & 0x10)
  {
    if (offset + 4 > size)
    {
      return AVERROR(EAGAIN);
    }
    const int ext_len = ((data[offset + 2] << 8) | data[offset + 3]) * 4;
    offset += 4 + ext_len;
    if (offset >= size)
    {
      return AVERROR(EAGAIN);
    }
  }

  const track& tr = m_tracks[static_cast<size_t>(m_video_track)];
  if (pt != tr.payload_type)
  {
    return AVERROR(EAGAIN);
  }
  const uint8_t* payload = data + offset;
  const int payload_size = size - offset;
  if (payload_size <= 0)
  {
    return AVERROR(EAGAIN);
  }

  switch (m_codec)
  {
  case video_codec::h264:
    return handle_h264_payload(payload, payload_size, rtp_ts, marker, packet);
  case video_codec::hevc:
    return handle_hevc_payload(payload, payload_size, rtp_ts, marker, packet);
  case video_codec::jpeg:
    return handle_jpeg_payload(payload, payload_size, rtp_ts, marker, packet);
  default:
    return AVERROR(EINVAL);
  }
}

int onvif_replay_rtsp::emit_audio_packet(
  const uint8_t* data,
  int size,
  uint32_t rtp_ts,
  int au_index,
  AVPacket* packet)
{
  if (size <= 0)
  {
    return AVERROR(EAGAIN);
  }

  const track& atr = m_tracks[static_cast<size_t>(m_audio_track)];
  if (!m_have_audio_base_ts)
  {
    m_audio_base_rtp_ts = rtp_ts;
    m_have_audio_base_ts = true;
  }

  // Rebased against the audio track's own first packet rather than the video's: the two RTP clocks
  // are unrelated (8/16 kHz against 90 kHz) and have independent random origins. Both streams begin
  // at the start of the requested clock range, so this keeps them aligned to within one packet.
  int64_t pts = static_cast<int64_t>(rtp_ts - static_cast<uint32_t>(m_audio_base_rtp_ts));
  // Several AAC access units can share one RTP timestamp; spread them over the frame duration the
  // configuration implies, or the muxer sees a run of identical timestamps and drops all but one.
  if (au_index > 0)
  {
    pts += static_cast<int64_t>(au_index) * k_aac_frame_samples;
  }

  if (av_new_packet(packet, size) < 0)
  {
    return AVERROR(ENOMEM);
  }
  std::memcpy(packet->data, data, static_cast<size_t>(size));
  packet->stream_index = m_audio_stream_index;
  packet->pts = pts;
  packet->dts = pts;
  packet->duration = atr.acodec == audio_codec::aac
    ? k_aac_frame_samples
    : size / std::max(1, atr.channels);
  // Every audio frame is independently decodable; without the flag a muxer treats the track as if
  // it had no sync samples at all.
  packet->flags = AV_PKT_FLAG_KEY;
  return 0;
}

int onvif_replay_rtsp::handle_audio_rtp(const uint8_t* data, int size, AVPacket* packet)
{
  if (size < k_rtp_header_min || m_audio_track < 0)
  {
    return AVERROR(EAGAIN);
  }

  const int csrc_count = data[0] & 0x0F;
  const bool has_extension = (data[0] & 0x10) != 0;
  int header_size = k_rtp_header_min + csrc_count * 4;
  if (size < header_size)
  {
    return AVERROR(EAGAIN);
  }
  if (has_extension)
  {
    if (size < header_size + 4)
    {
      return AVERROR(EAGAIN);
    }
    const int ext_words = (data[header_size + 2] << 8) | data[header_size + 3];
    header_size += 4 + ext_words * 4;
    if (size < header_size)
    {
      return AVERROR(EAGAIN);
    }
  }

  const uint32_t rtp_ts = (static_cast<uint32_t>(data[4]) << 24) |
                          (static_cast<uint32_t>(data[5]) << 16) |
                          (static_cast<uint32_t>(data[6]) << 8) |
                          static_cast<uint32_t>(data[7]);
  const uint8_t* payload = data + header_size;
  const int payload_size = size - header_size;
  if (payload_size <= 0)
  {
    return AVERROR(EAGAIN);
  }

  const track& atr = m_tracks[static_cast<size_t>(m_audio_track)];
  if (atr.acodec != audio_codec::aac)
  {
    // G.711 and G.726 have no payload header of their own: the RTP payload already is the frame.
    return emit_audio_packet(payload, payload_size, rtp_ts, 0, packet);
  }

  // RFC 3640 mpeg4-generic: a 16-bit AU-headers-length in bits, then one header per access unit
  // carrying that unit's size (and an index we do not need for the interleave-free modes cameras
  // use). Only the first unit of a packet is emitted here; the rest are buffered by the caller loop
  // being re-entered, so a packet carrying several units is delivered one unit at a time.
  if (payload_size < 2)
  {
    return AVERROR(EAGAIN);
  }
  const int headers_bits = (payload[0] << 8) | payload[1];
  const int headers_bytes = (headers_bits + 7) / 8;
  if (headers_bits <= 0 || payload_size < 2 + headers_bytes)
  {
    return AVERROR(EAGAIN);
  }

  const int au_header_bits = atr.size_length + atr.index_length;
  if (au_header_bits <= 0)
  {
    return AVERROR(EAGAIN);
  }

  const uint8_t* headers = payload + 2;
  const uint8_t* units = payload + 2 + headers_bytes;
  int units_size = payload_size - 2 - headers_bytes;

  auto read_bits = [&](int bit_offset, int count) -> int {
    int value = 0;
    for (int i = 0; i < count; ++i)
    {
      const int bit = bit_offset + i;
      const int byte = bit / 8;
      if (byte >= headers_bytes)
      {
        return -1;
      }
      value = (value << 1) | ((headers[byte] >> (7 - (bit % 8))) & 1);
    }
    return value;
  };

  // Emit the first unit and stash the remainder: read_frame hands back one AVPacket per call.
  int bit = 0;
  int emitted = -1;
  int index = 0;
  int offset = 0;
  while (bit + atr.size_length <= headers_bits && offset < units_size)
  {
    const int au_size = read_bits(bit, atr.size_length);
    bit += au_header_bits;
    if (au_size <= 0 || offset + au_size > units_size)
    {
      break;
    }
    if (emitted < 0)
    {
      emitted = emit_audio_packet(units + offset, au_size, rtp_ts, index, packet);
      if (emitted < 0)
      {
        return emitted;
      }
    }
    else
    {
      // Additional units in the same RTP packet are rare in practice (cameras send one AAC frame
      // per packet); dropping them silently would desynchronise audio, so say so once.
      logger::instance().debug("onvif replay: extra AAC access unit in one RTP packet dropped");
    }
    offset += au_size;
    ++index;
    // Subsequent headers use the delta index width.
    bit += atr.index_delta_length - atr.index_length;
  }

  return emitted < 0 ? AVERROR(EAGAIN) : emitted;
}

int onvif_replay_rtsp::read_frame(AVPacket* packet)
{
  if (!packet || !m_sock.is_open() || m_video_track < 0)
  {
    return AVERROR(EINVAL);
  }
  av_packet_unref(packet);
  const int video_ch = m_tracks[static_cast<size_t>(m_video_track)].interleaved;
  const int audio_ch = m_audio_track >= 0
    ? m_tracks[static_cast<size_t>(m_audio_track)].interleaved
    : -1;
  for (;;)
  {
    std::vector<uint8_t> rtp;
    int channel = -1;
    const int rc = read_interleaved_rtp(rtp, channel);
    if (rc < 0)
    {
      return rc;
    }
    int hr = AVERROR(EAGAIN);
    if (channel == video_ch)
    {
      hr = handle_video_rtp(rtp.data(), static_cast<int>(rtp.size()), packet);
    }
    else if (audio_ch >= 0 && channel == audio_ch && m_audio_stream_index >= 0)
    {
      hr = handle_audio_rtp(rtp.data(), static_cast<int>(rtp.size()), packet);
    }
    else
    {
      continue;
    }
    if (hr == AVERROR(EAGAIN))
    {
      continue;
    }
    return hr;
  }
}

#include "onvif_metadata_assembler.h"

#include <format>
#include <string_view>
#include <utility>

#include "logger/logger.h"

namespace {

/// Root element of every ONVIF metadata document (ver10/schema).
constexpr std::string_view kRootLocalName = "MetadataStream";

/// A document past this size is not a document we can use. Dropping the buffer whole keeps a
/// camera that speaks some other application protocol from growing us without bound.
constexpr size_t kMaxBufferBytes = 256 * 1024;

bool is_name_terminator(char c)
{
  return c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '>' || c == '/';
}

/// Compares the tag name starting at `from` against `kRootLocalName`, ignoring any namespace
/// prefix ("tt:MetadataStream", "m:MetadataStream" and a bare "MetadataStream" all match).
/// `end_out` receives the index just past the name. Returns false when the name is not yet
/// complete in the buffer — the caller must then wait for more bytes rather than decide.
bool match_root_name(const std::string& s, size_t from, size_t& end_out, bool& matched)
{
  size_t i = from;
  size_t name_start = from;
  while (i < s.size() && !is_name_terminator(s[i]))
  {
    // Everything up to the colon is the namespace prefix, which varies by vendor.
    if (s[i] == ':')
      name_start = i + 1;
    ++i;
  }

  if (i >= s.size())
    return false;  // name still truncated

  end_out = i;
  matched = std::string_view(s).substr(name_start, i - name_start) == kRootLocalName;
  return true;
}

/// Index of the '<' that opens the root element, or npos while undecidable.
size_t find_root_start(const std::string& s)
{
  size_t pos = 0;
  while ((pos = s.find('<', pos)) != std::string::npos)
  {
    if (pos + 1 >= s.size())
      return std::string::npos;  // wait for the character after '<'

    const char next = s[pos + 1];
    // Closing tags, processing instructions (<?xml ?>) and declarations (<!-- -->) are not roots.
    if (next == '/' || next == '?' || next == '!')
    {
      ++pos;
      continue;
    }

    size_t name_end = 0;
    bool matched = false;
    if (!match_root_name(s, pos + 1, name_end, matched))
      return std::string::npos;

    if (matched)
      return pos;

    ++pos;
  }
  return std::string::npos;
}

/// Index just past the '>' of the root's closing tag, or npos while undecidable.
size_t find_root_end(const std::string& s)
{
  size_t pos = 0;
  while ((pos = s.find("</", pos)) != std::string::npos)
  {
    size_t name_end = 0;
    bool matched = false;
    if (!match_root_name(s, pos + 2, name_end, matched))
      return std::string::npos;

    if (matched)
    {
      // "</tt:MetadataStream   >" is legal XML, so skip trailing whitespace before the '>'.
      size_t i = name_end;
      while (i < s.size() && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n'))
        ++i;
      if (i >= s.size())
        return std::string::npos;
      if (s[i] == '>')
        return i + 1;
    }

    pos += 2;
  }
  return std::string::npos;
}

}  // namespace

onvif_metadata_assembler::onvif_metadata_assembler(document_handler on_document)
  : m_on_document(std::move(on_document))
{
}

void onvif_metadata_assembler::push(const uint8_t* data, size_t len)
{
  if (!data || len == 0)
    return;

  m_buffer.append(reinterpret_cast<const char*>(data), len);
  drain();

  if (m_buffer.size() > kMaxBufferBytes)
  {
    ++m_documents_dropped;
    logger::instance().warning(std::format(
      "ONVIF metadata: no complete document within {} bytes, dropping buffer (dropped={})",
      kMaxBufferBytes, m_documents_dropped));
    m_buffer.clear();
  }
}

void onvif_metadata_assembler::drain()
{
  while (true)
  {
    const size_t start = find_root_start(m_buffer);
    if (start == std::string::npos)
      return;

    // Leading noise (an XML declaration, or the tail of a document we joined mid-flight).
    if (start > 0)
      m_buffer.erase(0, start);

    const size_t end = find_root_end(m_buffer);
    if (end == std::string::npos)
      return;

    std::string document = m_buffer.substr(0, end);
    m_buffer.erase(0, end);
    ++m_documents_emitted;

    // Once per session. Finding the track only proves the camera offered it; this proves data is
    // actually flowing, which is the difference between "misconfigured" and "nothing arriving" when
    // no boxes show up on the monitor.
    if (m_documents_emitted == 1)
    {
      logger::instance().info(std::format(
        "ONVIF metadata: first document assembled, {} bytes", document.size()));
    }

    if (m_on_document)
      m_on_document(std::move(document));
  }
}

void onvif_metadata_assembler::reset()
{
  m_buffer.clear();
}

#pragma once

#include <cstddef>
#include <cstdint>
#include <functional>
#include <string>

/// Reassembles ONVIF metadata documents from the RTSP metadata track.
///
/// libavformat has no depacketizer for "application/vnd.onvif.metadata", so it falls back to its
/// generic RTP path (rtpdec.c: av_new_packet + memcpy) and hands us one AVPacket per RTP packet
/// with the raw payload inside. A metadata document larger than the MTU therefore arrives in
/// pieces, and nothing above us puts them back together — that is this class's whole job.
///
/// Boundaries come from the payload itself rather than the RTP marker bit, which the generic path
/// does not surface: the payload is plain XML, so a document ends at the closing tag whose local
/// name is "MetadataStream" (any namespace prefix). Anything before the first plausible opening
/// tag is discarded, and a buffer that grows past the cap without closing is dropped whole — a
/// camera that speaks something else must not turn into an unbounded allocation.
///
/// Not thread-safe: one instance per reader, touched only by that reader's thread.
class onvif_metadata_assembler {
public:
  using document_handler = std::function<void(std::string&&)>;

  explicit onvif_metadata_assembler(document_handler on_document);

  /// Feeds one RTP payload. Emits every complete document it completes, in order.
  void push(const uint8_t* data, size_t len);

  /// Drops whatever is half-assembled. Called when the RTSP session ends: the next session starts
  /// at a document boundary, and a torso from the old one would corrupt the first new document.
  void reset();

  uint64_t documents_emitted() const { return m_documents_emitted; }
  uint64_t documents_dropped() const { return m_documents_dropped; }

private:
  /// Cuts out every complete document currently in the buffer.
  void drain();

  document_handler m_on_document;
  std::string m_buffer;
  uint64_t m_documents_emitted = 0;
  uint64_t m_documents_dropped = 0;
};

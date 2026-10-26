# ta_roienc: two gaps in how Transcoder takes compressed input

Found 2026-10-26 by roi_transcode's `transcode_device_test`, written for QSV on D3D11; both are
older than that change and in the library's decode path (`src/core/picture_input.cpp`), not in
any encoder.

- **The last access unit of an Annex-B stream is lost.** For Annex-B input without extradata
  `PictureDecoder` puts FFmpeg's parser in front of the decoder (`Framing::Arbitrary`, the
  default), and the parser emits an access unit only when the next one begins. `flush()` sends the
  decoder its end-of-stream packet but never asks the parser for what it still holds
  (`av_parser_parse2` with an empty buffer), so 30 access units in give 29 frames out. Harmless on a
  live stream, which never ends on purpose; a recompressed clip loses its last frame.
- **The caller's buffer is read past its end.** `Transcoder::submit(std::span<const uint8_t>)`
  hands the span to `av_parser_parse2` as it is, and FFmpeg's parser copies
  `AV_INPUT_BUFFER_PADDING_SIZE` (64) bytes beyond it (`ff_combine_frame`): AddressSanitizer
  reports a heap-buffer-overflow on an unpadded `std::vector`. vms and roitrc are safe because
  their packets come from libavformat, which pads; `transcoder.h` says nothing of it, so the next
  caller is not. Either the header states the requirement or `push` copies into a padded buffer
  of its own (one copy per access unit, small next to a decode).

The test works around both — it pads its input and counts the frames the encoder took
(`Outcome::frames`) rather than the ones it submitted — and should stop doing so once they are
fixed.

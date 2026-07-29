namespace web_vms;

// Shape of the fmp4 fragment index that VmsFs parses out of a clip and returns over
// GetClipIndexes. web_vms.Server only consumes it — the box parsing itself lives in
// VmsFs/Grpc/ClipIndexParser.cs, next to the files it reads.
public record Fmp4FragmentEntry(ulong Offset, ulong Size, double Duration);

// InitSize = bytes of ftyp+moov at the head of the clip (the #EXT-X-MAP range).
// Fragments = one entry per moof, in file order. Empty when the clip has no mfra index.
public record Fmp4SegmentIndex(ulong InitSize, IReadOnlyList<Fmp4FragmentEntry> Fragments);

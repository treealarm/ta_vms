using Grpc.Core;
using Vms.Fs;

namespace web_vms;

public static class Fmp4SidxParser
{
  public record FragmentEntry(ulong Offset, ulong Size, double Duration);

  // InitSize = byte length of ftyp+moov (the EXT-X-MAP init section).
  // Fragments is empty when the file has no mfra: serve media as one whole segment.
  public record SegmentIndex(ulong InitSize, IReadOnlyList<FragmentEntry> Fragments);

  // Returns null on read error or if the file is not fMP4.
  public static async Task<SegmentIndex?> ReadAsync(
    FsService.FsServiceClient fsClient, string clipUid, CancellationToken ct)
  {
    // Stat for file size (needed to locate mfra at the end).
    StatArchiveTsResponse stat;
    try
    {
      stat = await fsClient.StatArchiveTsAsync(
        new StatArchiveTsRequest { ClipUid = clipUid }, cancellationToken: ct);
    }
    catch (RpcException) { return null; }
    if (!stat.Exists || stat.SizeBytes < 32) return null;
    var fileSize = stat.SizeBytes;

    // Read start of file: ftyp + moov (up to 64 KB).
    var headerData = await ReadRangeAsync(fsClient, clipUid, 0, Math.Min(65536UL, fileSize), ct);
    if (headerData == null) return null;
    if (!TryParseHeader(headerData, out var initSize, out var timescale, out var videoTrackId)) return null;

    // mfro is the last 16 bytes of the file.
    // Its field box_size gives the total byte size of the mfra container.
    if (fileSize < 16) return null;
    var mfroData = await ReadRangeAsync(fsClient, clipUid, fileSize - 16, 16, ct);
    if (mfroData == null) return null;
    if (ReadUInt32Be(mfroData, 0) != 16 || ReadFourCc(mfroData, 4) != "mfro") return null;
    var mfraBoxSize = ReadUInt32Be(mfroData, 12);
    if (mfraBoxSize > fileSize || mfraBoxSize < 16) return null;

    var mfraStart = fileSize - mfraBoxSize;

    // Read and parse mfra.
    var mfraData = await ReadRangeAsync(fsClient, clipUid, mfraStart, mfraBoxSize, ct);
    if (mfraData == null) return null;
    var entries = ParseMfra(mfraData, videoTrackId);
    if (entries == null || entries.Count == 0)
      return new SegmentIndex(initSize, Array.Empty<FragmentEntry>());

    // Build fragment list from tfra entries.
    // Fragment i spans bytes [moof_offset[i], moof_offset[i+1]) in the file.
    // The last fragment ends at mfra_start.
    var fragments = new List<FragmentEntry>(entries.Count);
    for (int i = 0; i < entries.Count; i++)
    {
      var (time, moofOffset) = entries[i];
      ulong fragEnd = i + 1 < entries.Count ? entries[i + 1].MoofOffset : mfraStart;
      ulong fragSize = fragEnd > moofOffset ? fragEnd - moofOffset : 0;

      double duration;
      if (i + 1 < entries.Count)
        duration = (double)(entries[i + 1].Time - time) / timescale;
      else if (entries.Count >= 2)
        duration = (double)(entries[i].Time - entries[i - 1].Time) / timescale; // repeat last known interval
      else
        duration = (double)fragSize / 50_000; // rough fallback
      fragments.Add(new FragmentEntry(moofOffset, fragSize, duration));
    }

    return new SegmentIndex(initSize, fragments);
  }

  // Parse ftyp+moov to find init section end, video track timescale, and video track_ID.
  private static bool TryParseHeader(byte[] data, out ulong initSize, out uint timescale, out uint videoTrackId)
  {
    initSize = 0;
    timescale = 90000;
    videoTrackId = 1;
    ulong pos = 0;

    while (pos + 8 <= (ulong)data.Length)
    {
      var size = ReadUInt32Be(data, pos);
      if (size < 8) return false;
      var type = ReadFourCc(data, pos + 4);

      if (type is "ftyp" or "moov")
        initSize = pos + size;

      if (type == "moov")
      {
        ParseMoovVideoTrack(data, pos + 8, pos + size, ref timescale, ref videoTrackId);
        return true;
      }

      if (type is "moof" or "mdat")
        return initSize > 0;

      pos += size;
    }

    return initSize > 0;
  }

  // Walk moov children; for each trak, check if it is the video track (has vmhd)
  // and if so, read its tkhd track_ID and mdhd timescale.
  private static void ParseMoovVideoTrack(
    byte[] data, ulong start, ulong end, ref uint timescale, ref uint videoTrackId)
  {
    ulong pos = start;
    while (pos + 8 <= end && pos + 8 <= (ulong)data.Length)
    {
      var size = ReadUInt32Be(data, pos);
      if (size < 8) return;
      var type = ReadFourCc(data, pos + 4);

      if (type == "trak")
      {
        uint tId = 0;
        uint tScale = 0;
        bool isVideo = false;
        ParseTrak(data, pos + 8, Math.Min(pos + size, end), ref tId, ref tScale, ref isVideo);
        if (isVideo && tId > 0 && tScale > 0)
        {
          videoTrackId = tId;
          timescale = tScale;
          return;
        }
      }

      pos += size;
    }
  }

  private static void ParseTrak(
    byte[] data, ulong start, ulong end, ref uint trackId, ref uint timescale, ref bool isVideo)
  {
    ulong pos = start;
    while (pos + 8 <= end && pos + 8 <= (ulong)data.Length)
    {
      var size = ReadUInt32Be(data, pos);
      if (size < 8) return;
      var type = ReadFourCc(data, pos + 4);
      var childEnd = Math.Min(pos + size, end);

      if (type == "tkhd")
      {
        // FullBox + version(1) + flags(3); version=0: create(4)+modify(4)+track_ID(4)
        //                                  version=1: create(8)+modify(8)+track_ID(4)
        if (pos + 12 < (ulong)data.Length)
        {
          int v = data[(int)(pos + 8)];
          ulong idOff = pos + 12 + (v == 1 ? 16UL : 8UL);
          if (idOff + 4 <= (ulong)data.Length)
            trackId = ReadUInt32Be(data, idOff);
        }
      }
      else if (type == "mdia")
        ParseMdia(data, pos + 8, childEnd, ref timescale, ref isVideo);

      pos += size;
    }
  }

  private static void ParseMdia(
    byte[] data, ulong start, ulong end, ref uint timescale, ref bool isVideo)
  {
    ulong pos = start;
    while (pos + 8 <= end && pos + 8 <= (ulong)data.Length)
    {
      var size = ReadUInt32Be(data, pos);
      if (size < 8) return;
      var type = ReadFourCc(data, pos + 4);
      var childEnd = Math.Min(pos + size, end);

      if (type == "mdhd")
      {
        if (pos + 12 < (ulong)data.Length)
        {
          int v = data[(int)(pos + 8)];
          ulong tsOff = pos + 12 + (v == 1 ? 16UL : 8UL);
          if (tsOff + 4 <= (ulong)data.Length)
            timescale = ReadUInt32Be(data, tsOff);
        }
      }
      else if (type == "minf")
        ParseMinf(data, pos + 8, childEnd, ref isVideo);

      pos += size;
    }
  }

  private static void ParseMinf(byte[] data, ulong start, ulong end, ref bool isVideo)
  {
    ulong pos = start;
    while (pos + 8 <= end && pos + 8 <= (ulong)data.Length)
    {
      var size = ReadUInt32Be(data, pos);
      if (size < 8) return;
      if (ReadFourCc(data, pos + 4) == "vmhd") { isVideo = true; return; }
      pos += size;
    }
  }

  private record struct TfraEntry(ulong Time, ulong MoofOffset);

  // Parse mfra box (data starts at offset 0).
  // Prefers the tfra whose track_ID matches videoTrackId; falls back to any non-empty tfra.
  private static List<TfraEntry>? ParseMfra(byte[] data, uint videoTrackId)
  {
    if (data.Length < 8) return null;
    if (ReadFourCc(data, 4) != "mfra") return null;

    ulong mfraSize = ReadUInt32Be(data, 0);
    ulong pos = 8;
    List<TfraEntry>? fallback = null;

    while (pos + 8 <= mfraSize && pos + 8 <= (ulong)data.Length)
    {
      var size = ReadUInt32Be(data, pos);
      if (size < 8) return null;
      var type = ReadFourCc(data, pos + 4);

      if (type == "tfra")
      {
        // track_ID is the first field after the FullBox header (offset 12)
        uint trackId = pos + 16 <= (ulong)data.Length ? ReadUInt32Be(data, pos + 12) : 0;
        var entries = ParseTfra(data, pos, pos + size);
        if (entries != null && entries.Count > 0)
        {
          if (trackId == videoTrackId) return entries;
          fallback ??= entries;
        }
      }

      pos += size;
    }

    return fallback;
  }

  // Parse a single tfra box.
  private static List<TfraEntry>? ParseTfra(byte[] data, ulong offset, ulong end)
  {
    // FullBox: size(4) + "tfra"(4) + version(1) + flags(3) = 12 bytes
    // then: track_ID(4) + packed_field(4) + number_of_entry(4) = 12 bytes
    if (offset + 24 > (ulong)data.Length) return null;
    int version = data[(int)(offset + 8)];

    ulong pos = offset + 12;
    pos += 4; // skip track_ID

    // packed_field bits: [5..4]=traf_len_sz, [3..2]=trun_len_sz, [1..0]=sample_len_sz
    uint packed = ReadUInt32Be(data, pos); pos += 4;
    int szTraf = (int)((packed >> 4) & 3) + 1;
    int szTrun = (int)((packed >> 2) & 3) + 1;
    int szSample = (int)(packed & 3) + 1;
    int varSize = szTraf + szTrun + szSample;

    uint count = ReadUInt32Be(data, pos); pos += 4;
    int entrySize = (version == 0 ? 8 : 16) + varSize;

    if (pos + (ulong)(count * entrySize) > (ulong)data.Length) return null;

    var entries = new List<TfraEntry>((int)count);
    for (uint i = 0; i < count; i++)
    {
      ulong time, moofOffset;
      if (version == 0)
      {
        time = ReadUInt32Be(data, pos);
        moofOffset = ReadUInt32Be(data, pos + 4);
        pos += 8;
      }
      else
      {
        time = ReadUInt64Be(data, pos);
        moofOffset = ReadUInt64Be(data, pos + 8);
        pos += 16;
      }
      pos += (ulong)varSize;
      entries.Add(new TfraEntry(time, moofOffset));
    }

    return entries;
  }

  private static async Task<byte[]?> ReadRangeAsync(
    FsService.FsServiceClient fsClient, string clipUid,
    ulong offset, ulong length, CancellationToken ct)
  {
    var buffer = new List<byte>((int)Math.Min(length, 65536));
    try
    {
      var req = new ReadArchiveTsRequest { ClipUid = clipUid, Offset = offset, Length = length };
      using var call = fsClient.ReadArchiveTs(req, cancellationToken: ct);
      while (await call.ResponseStream.MoveNext(ct))
      {
        var chunk = call.ResponseStream.Current;
        if (chunk.Data.Length > 0)
          buffer.AddRange(chunk.Data);
        if ((ulong)buffer.Count >= length)
          break;
      }
    }
    catch (RpcException) { return null; }
    return buffer.ToArray();
  }

  private static uint ReadUInt32Be(byte[] data, ulong offset)
  {
    var o = (int)offset;
    return (uint)((data[o] << 24) | (data[o + 1] << 16) | (data[o + 2] << 8) | data[o + 3]);
  }

  private static ulong ReadUInt64Be(byte[] data, ulong offset) =>
    ((ulong)ReadUInt32Be(data, offset) << 32) | ReadUInt32Be(data, offset + 4);

  private static string ReadFourCc(byte[] data, ulong offset)
  {
    var o = (int)offset;
    return new string(new[] { (char)data[o], (char)data[o + 1], (char)data[o + 2], (char)data[o + 3] });
  }
}

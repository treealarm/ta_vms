using System.Text.Json;

namespace VmsFs;

// Parses the fmp4 fragment index (init size + per-moof offsets/durations) directly from a local
// clip file. This is the VmsFs-local counterpart of web_srv.Server's Fmp4SidxParser, which reads the
// same structure over gRPC range requests — moving it here lets the manifest be built from one batch
// call plus a persisted cache instead of several range reads per clip on every playback.
public static class ClipIndexParser
{
  public record FragmentEntry(ulong Offset, ulong Size, double Duration);
  public record SegmentIndex(ulong InitSize, IReadOnlyList<FragmentEntry> Fragments);

  // Compact persisted form: init size + [offset, size, duration] per fragment.
  private sealed record FragJson(ulong O, ulong S, double D);
  private sealed record IndexJson(ulong Init, List<FragJson> Frags);

  public static string Serialize(SegmentIndex idx) =>
    JsonSerializer.Serialize(new IndexJson(
      idx.InitSize, idx.Fragments.Select(f => new FragJson(f.Offset, f.Size, f.Duration)).ToList()));

  public static SegmentIndex? Deserialize(string json)
  {
    try
    {
      var j = JsonSerializer.Deserialize<IndexJson>(json);
      if (j is null) return null;
      return new SegmentIndex(j.Init, j.Frags.Select(f => new FragmentEntry(f.O, f.S, f.D)).ToList());
    }
    catch { return null; }
  }

  // Returns null on read error or if the file is not fMP4.
  public static async Task<SegmentIndex?> ReadAsync(string absPath, CancellationToken ct)
  {
    long len;
    try
    {
      var fi = new FileInfo(absPath);
      if (!fi.Exists || fi.Length < 32) return null;
      len = fi.Length;
    }
    catch { return null; }
    var fileSize = (ulong)len;

    // Start of file: ftyp + moov (up to 64 KB) -> init size, video timescale + track id.
    var headerData = await ReadRangeAsync(absPath, 0, Math.Min(65536UL, fileSize), ct);
    if (headerData == null) return null;
    if (!TryParseHeader(headerData, out var initSize, out var timescale, out var videoTrackId)) return null;

    if (fileSize < 16) return null;

    // The index normally comes from the trailer, which is one cheap read at the end of the file.
    var indexEnd = fileSize;
    List<TfraEntry>? entries = null;

    var mfroData = await ReadRangeAsync(absPath, fileSize - 16, 16, ct);
    if (mfroData is { Length: 16 }
        && ReadUInt32Be(mfroData, 0) == 16
        && ReadFourCc(mfroData, 4) == "mfro")
    {
      var mfraBoxSize = ReadUInt32Be(mfroData, 12);
      if (mfraBoxSize <= fileSize && mfraBoxSize >= 16)
      {
        var mfraStart = fileSize - mfraBoxSize;
        var mfraData = await ReadRangeAsync(absPath, mfraStart, mfraBoxSize, ct);
        if (mfraData != null)
        {
          entries = ParseMfra(mfraData, videoTrackId);
          if (entries is { Count: > 0 })
            indexEnd = mfraStart;
        }
      }
    }

    // No trailer means the writer was killed before it could finish the file. The fragments
    // themselves are all there and self-describing, so the index is rebuilt by walking them —
    // otherwise the recording would be unplayable and would look to everything else like time the
    // archive simply does not have.
    if (entries is not { Count: > 0 })
      entries = await ScanFragmentsAsync(absPath, fileSize, initSize, videoTrackId, ct);

    if (entries is not { Count: > 0 })
      return new SegmentIndex(initSize, Array.Empty<FragmentEntry>());

    var fragments = new List<FragmentEntry>(entries.Count);
    for (int i = 0; i < entries.Count; i++)
    {
      var (time, moofOffset) = entries[i];
      ulong fragEnd = i + 1 < entries.Count ? entries[i + 1].MoofOffset : indexEnd;
      ulong fragSize = fragEnd > moofOffset ? fragEnd - moofOffset : 0;

      double duration;
      if (i + 1 < entries.Count)
        duration = (double)(entries[i + 1].Time - time) / timescale;
      else if (entries.Count >= 2)
        duration = (double)(entries[i].Time - entries[i - 1].Time) / timescale;
      else
        duration = (double)fragSize / 50_000;
      fragments.Add(new FragmentEntry(moofOffset, fragSize, duration));
    }

    return new SegmentIndex(initSize, fragments);
  }


  /// <summary>
  /// Rebuilds the fragment list by walking the file, for recordings that have no trailer.
  /// </summary>
  /// <remarks>
  /// Only the box headers and the small moof boxes are read; the media payload is skipped over. A
  /// truncated final box ends the walk — a process that was killed mid-write leaves one, and the
  /// fragments before it are perfectly good.
  /// </remarks>
  private static async Task<List<TfraEntry>> ScanFragmentsAsync(
    string absPath, ulong fileSize, ulong initSize, uint videoTrackId, CancellationToken ct)
  {
    var entries = new List<TfraEntry>();
    var offset = initSize;

    while (offset + 8 <= fileSize && entries.Count < 100_000)
    {
      ct.ThrowIfCancellationRequested();

      var header = await ReadRangeAsync(absPath, offset, 8, ct);
      if (header is not { Length: 8 })
        break;

      ulong boxSize = ReadUInt32Be(header, 0);
      var type = ReadFourCc(header, 4);
      if (boxSize == 1)
      {
        var large = await ReadRangeAsync(absPath, offset + 8, 8, ct);
        if (large is not { Length: 8 })
          break;
        boxSize = ReadUInt64Be(large, 0);
      }
      // Zero means "to end of file" and anything shorter than a header is corruption; either way
      // there is nothing further to walk.
      if (boxSize < 8 || offset + boxSize > fileSize)
        break;

      if (type == "moof")
      {
        var moof = await ReadRangeAsync(absPath, offset, boxSize, ct);
        if (moof is null)
          break;
        if (TryReadFragmentTime(moof, videoTrackId, out var time))
          entries.Add(new TfraEntry(time, offset));
      }

      offset += boxSize;
    }

    return entries;
  }

  /// <summary>Decode time of a fragment, from the tfdt of its video track.</summary>
  private static bool TryReadFragmentTime(byte[] moof, uint videoTrackId, out ulong time)
  {
    time = 0;
    // moof -> traf -> { tfhd (which track), tfdt (when) }
    for (var pos = 8; pos + 8 <= moof.Length;)
    {
      var size = (int)ReadUInt32Be(moof, (ulong)pos);
      if (size < 8 || pos + size > moof.Length)
        return false;
      if (ReadFourCc(moof, (ulong)(pos + 4)) != "traf")
      {
        pos += size;
        continue;
      }

      uint trackId = 0;
      ulong baseTime = 0;
      var haveTime = false;
      for (var inner = pos + 8; inner + 8 <= pos + size;)
      {
        var innerSize = (int)ReadUInt32Be(moof, (ulong)inner);
        if (innerSize < 8 || inner + innerSize > pos + size)
          break;
        var innerType = ReadFourCc(moof, (ulong)(inner + 4));
        if (innerType == "tfhd" && inner + 16 <= moof.Length)
        {
          trackId = ReadUInt32Be(moof, (ulong)(inner + 12));
        }
        else if (innerType == "tfdt" && inner + 16 <= moof.Length)
        {
          var version = moof[inner + 8];
          baseTime = version == 1 && inner + 20 <= moof.Length
            ? ReadUInt64Be(moof, (ulong)(inner + 12))
            : ReadUInt32Be(moof, (ulong)(inner + 12));
          haveTime = true;
        }
        inner += innerSize;
      }

      if (haveTime && (videoTrackId == 0 || trackId == videoTrackId))
      {
        time = baseTime;
        return true;
      }
      pos += size;
    }
    return false;
  }

  private static async Task<byte[]?> ReadRangeAsync(string absPath, ulong offset, ulong length, CancellationToken ct)
  {
    try
    {
      await using var fs = new FileStream(absPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
      if ((ulong)fs.Length < offset) return null;
      var toRead = (int)Math.Min(length, (ulong)fs.Length - offset);
      var buf = new byte[toRead];
      fs.Seek((long)offset, SeekOrigin.Begin);
      var read = 0;
      while (read < toRead)
      {
        var got = await fs.ReadAsync(buf.AsMemory(read, toRead - read), ct);
        if (got == 0) break;
        read += got;
      }
      return read == toRead ? buf : buf[..read];
    }
    catch { return null; }
  }

  // ---- fMP4 box parsing (identical to web_srv.Server's Fmp4SidxParser) ----

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

  private static void ParseMoovVideoTrack(byte[] data, ulong start, ulong end, ref uint timescale, ref uint videoTrackId)
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

  private static void ParseTrak(byte[] data, ulong start, ulong end, ref uint trackId, ref uint timescale, ref bool isVideo)
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

  private static void ParseMdia(byte[] data, ulong start, ulong end, ref uint timescale, ref bool isVideo)
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

  private static List<TfraEntry>? ParseTfra(byte[] data, ulong offset, ulong end)
  {
    if (offset + 24 > (ulong)data.Length) return null;
    int version = data[(int)(offset + 8)];

    ulong pos = offset + 12;
    pos += 4; // skip track_ID

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

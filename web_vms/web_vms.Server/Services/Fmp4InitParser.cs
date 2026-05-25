using Grpc.Core;
using Vms.Fs;

namespace web_vms;

public static class Fmp4InitParser
{
  // Returns the byte length of the ftyp+moov init section at the start of a fragmented MP4 file,
  // or null if the file is not fMP4 (no moov before the first moof/mdat).
  public static async Task<ulong?> ReadInitSizeAsync(
    FsService.FsServiceClient fsClient, string clipUid, CancellationToken ct)
  {
    // Read enough bytes to cover ftyp (typically ~24 B) + moov header (8 B).
    // We fetch 64 KB which is more than enough for any real moov box.
    const ulong readLen = 65536;

    var req = new ReadArchiveTsRequest { ClipUid = clipUid, Offset = 0, Length = readLen };
    var buffer = new List<byte>((int)readLen);

    try
    {
      using var call = fsClient.ReadArchiveTs(req, cancellationToken: ct);
      while (await call.ResponseStream.MoveNext(ct))
      {
        var chunk = call.ResponseStream.Current;
        if (chunk.Data.Length > 0)
          buffer.AddRange(chunk.Data);
        if (buffer.Count >= (int)readLen)
          break;
      }
    }
    catch (RpcException)
    {
      return null;
    }

    return ParseInitSize(buffer);
  }

  private static ulong? ParseInitSize(List<byte> data)
  {
    ulong pos = 0;
    ulong initEnd = 0;
    var bytes = data.ToArray();

    while (pos + 8 <= (ulong)bytes.Length)
    {
      var boxSize = ReadUInt32Be(bytes, pos);
      if (boxSize < 8) return null; // malformed

      var boxType = ReadFourCc(bytes, pos + 4);

      if (boxType is "moof" or "mdat")
        return initEnd > 0 ? initEnd : null;

      if (boxType is "ftyp" or "moov")
        initEnd = pos + boxSize;

      pos += boxSize;
    }

    return initEnd > 0 ? initEnd : null;
  }

  private static uint ReadUInt32Be(byte[] data, ulong offset)
  {
    var o = (int)offset;
    return (uint)((data[o] << 24) | (data[o + 1] << 16) | (data[o + 2] << 8) | data[o + 3]);
  }

  private static string ReadFourCc(byte[] data, ulong offset)
  {
    var o = (int)offset;
    return new string(new[] { (char)data[o], (char)data[o + 1], (char)data[o + 2], (char)data[o + 3] });
  }
}

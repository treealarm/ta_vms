using System.Runtime.CompilerServices;
using Grpc.Core;
using Vms.Fs;

namespace web_vms;

public sealed class ArchiveTsStreamer(FsService.FsServiceClient fsClient) : IArchiveTsStreamer
{
  public async Task<ArchiveStreamResult> OpenAsync(
    string path, ulong offset, ulong length, string? rangeHeader, CancellationToken ct)
  {
    StatArchiveTsResponse stat;
    try
    {
      stat = await fsClient.StatArchiveTsAsync(
        new StatArchiveTsRequest { RelativePath = path }, cancellationToken: ct);
    }
    catch (RpcException ex)
    {
      return new ArchiveStreamResult.Error(ex.Status.Detail);
    }

    if (!stat.Exists)
      return new ArchiveStreamResult.NotFound(
        string.IsNullOrWhiteSpace(stat.Message) ? "not found" : stat.Message);

    var fileSize = stat.SizeBytes;
    if (offset > fileSize)
      return new ArchiveStreamResult.BadRequest("offset past end of file");

    var maxFromBase = fileSize - offset;
    var entityLen = length > 0 ? Math.Min(length, maxFromBase) : maxFromBase;

    var (rngStart, rngLen, hasRange) = ParseHttpRange(rangeHeader, entityLen);

    var req = new ReadArchiveTsRequest { RelativePath = path };
    if (hasRange)
    {
      req.Offset = offset + rngStart;
      req.Length = rngLen;
    }
    else
    {
      req.Offset = offset;
      if (length > 0)
        req.Length = entityLen;
    }

    RangeInfo? range = hasRange ? new RangeInfo(rngStart, rngLen, entityLen) : null;
    var contentLength = (long)(hasRange ? rngLen : entityLen);

    return new ArchiveStreamResult.Ok(ContentType(path), contentLength, range, StreamChunksAsync(req, ct));
  }

  private async IAsyncEnumerable<ReadOnlyMemory<byte>> StreamChunksAsync(
    ReadArchiveTsRequest req,
    [EnumeratorCancellation] CancellationToken ct)
  {
    using var call = fsClient.ReadArchiveTs(req, cancellationToken: ct);
    var stream = call.ResponseStream;
    while (await stream.MoveNext(ct))
    {
      var chunk = stream.Current;
      if (chunk.Data.Length > 0)
        yield return chunk.Data.Memory;
    }
  }

  private static string ContentType(string filePath) =>
    filePath.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) ? "video/x-matroska" : "video/mp2t";

  private static (ulong start, ulong length, bool hasRange) ParseHttpRange(string? hdr, ulong entityLen)
  {
    hdr = hdr?.Trim() ?? "";
    if (hdr.Length == 0 || entityLen == 0)
      return (0, 0, false);
    const string prefix = "bytes=";
    if (!hdr.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
      return (0, 0, false);

    var s = hdr.AsSpan(prefix.Length).Trim();
    var comma = s.IndexOf(',');
    if (comma >= 0)
      s = s[..comma].Trim();

    var dash = s.IndexOf('-');
    if (dash < 0)
      return (0, 0, false);

    var startStr = s[..dash].Trim();
    var endStr = s[(dash + 1)..].Trim();
    if (startStr.Length == 0)
      return (0, 0, false);

    if (!ulong.TryParse(startStr, out var start) || start >= entityLen)
      return (0, 0, false);

    ulong rngLen;
    if (endStr.Length == 0)
      rngLen = entityLen - start;
    else
    {
      if (!ulong.TryParse(endStr, out var end) || end < start || end >= entityLen)
        return (0, 0, false);
      rngLen = end - start + 1;
    }

    return (start, rngLen, true);
  }
}

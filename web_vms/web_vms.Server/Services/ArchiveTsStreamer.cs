using System.Runtime.CompilerServices;
using Dapr.Client;
using Grpc.Core;
using Vms.Fs;

namespace web_vms;

public sealed class ArchiveTsStreamer(ServerPairRegistry registry) : IArchiveTsStreamer
{
  public async Task<ArchiveStreamResult> OpenAsync(
    string clipUid, ulong offset, ulong length, string? rangeHeader, CancellationToken ct)
  {
    // Fan-out to all FS instances — clipUid is local to the FS that stored it.
    FsService.FsServiceClient? owner = null;
    StatArchiveTsResponse? stat = null;
    foreach (var fsId in registry.AllFsIds)
    {
      var candidate = new FsService.FsServiceClient(DaprClient.CreateInvocationInvoker(fsId));
      try
      {
        var s = await candidate.StatArchiveTsAsync(
          new StatArchiveTsRequest { ClipUid = clipUid }, cancellationToken: ct);
        if (s.Exists)
        {
          owner = candidate;
          stat = s;
          break;
        }
      }
      catch (RpcException) { /* FS unavailable — try next */ }
    }

    if (owner is null || stat is null)
      return new ArchiveStreamResult.NotFound("clip not found");

    var fileSize = stat.SizeBytes;
    if (offset > fileSize)
      return new ArchiveStreamResult.BadRequest("offset past end of file");

    var maxFromBase = fileSize - offset;
    var entityLen = length > 0 ? Math.Min(length, maxFromBase) : maxFromBase;

    var httpRange = ParseHttpRange(rangeHeader, entityLen);

    var req = new ReadArchiveTsRequest { ClipUid = clipUid };
    if (httpRange is not null)
    {
      req.Offset = offset + httpRange.Start;
      req.Length = httpRange.Length;
    }
    else
    {
      req.Offset = offset;
      if (length > 0)
        req.Length = entityLen;
    }

    RangeInfo? range = httpRange is not null ? new RangeInfo(httpRange.Start, httpRange.Length, entityLen) : null;
    var contentLength = (long)(httpRange?.Length ?? entityLen);

    return new ArchiveStreamResult.Ok(ContentType(stat.FilePath), contentLength, range, StreamChunksAsync(owner, req, ct));
  }

  private static async IAsyncEnumerable<ReadOnlyMemory<byte>> StreamChunksAsync(
    FsService.FsServiceClient fsClient,
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
    filePath.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) ? "video/x-matroska" :
    filePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ? "video/mp4" :
    "video/mp2t";

  private static HttpRange? ParseHttpRange(string? hdr, ulong entityLen)
  {
    hdr = hdr?.Trim() ?? "";
    if (hdr.Length == 0 || entityLen == 0)
      return null;
    const string prefix = "bytes=";
    if (!hdr.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
      return null;

    var s = hdr.AsSpan(prefix.Length).Trim();
    var comma = s.IndexOf(',');
    if (comma >= 0)
      s = s[..comma].Trim();

    var dash = s.IndexOf('-');
    if (dash < 0)
      return null;

    var startStr = s[..dash].Trim();
    var endStr = s[(dash + 1)..].Trim();
    if (startStr.Length == 0)
      return null;

    if (!ulong.TryParse(startStr, out var start) || start >= entityLen)
      return null;

    ulong rngLen;
    if (endStr.Length == 0)
      rngLen = entityLen - start;
    else
    {
      if (!ulong.TryParse(endStr, out var end) || end < start || end >= entityLen)
        return null;
      rngLen = end - start + 1;
    }

    return new HttpRange(start, rngLen);
  }
}

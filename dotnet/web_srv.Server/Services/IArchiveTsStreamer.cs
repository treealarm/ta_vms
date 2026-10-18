namespace web_srv;

public abstract record ArchiveStreamResult
{
  public sealed record Ok(
    string ContentType,
    long ContentLength,
    RangeInfo? Range,
    IAsyncEnumerable<ReadOnlyMemory<byte>> Data
  ) : ArchiveStreamResult;

  public sealed record NotFound(string Reason) : ArchiveStreamResult;
  public sealed record BadRequest(string Reason) : ArchiveStreamResult;
  public sealed record Error(string Reason) : ArchiveStreamResult;
}

public record HttpRange(ulong Start, ulong Length);

public record RangeInfo(ulong Start, ulong Length, ulong Total);

public interface IArchiveTsStreamer
{
  Task<ArchiveStreamResult> OpenAsync(
    string clipUid, ulong offset, ulong length, string? rangeHeader, CancellationToken ct);
}

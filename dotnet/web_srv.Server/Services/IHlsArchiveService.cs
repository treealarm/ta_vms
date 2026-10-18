namespace web_srv;

public abstract record HlsPlaylistResult
{
  public sealed record Ok(string Content) : HlsPlaylistResult;
  public sealed record NotFound(string Reason) : HlsPlaylistResult;
  public sealed record Error(string Reason) : HlsPlaylistResult;
}

public interface IHlsArchiveService
{
  Task<HlsPlaylistResult> BuildPreviewPlaylistAsync(
    string cameraId, string? streamName, TimelineSlice range, CancellationToken ct, DateTime? startAt = null);
  Task<HlsPlaylistResult> BuildFilesPlaylistAsync(string cameraId, string startClipUid, uint count, CancellationToken ct, DateTime? from = null);
  Task<HlsPlaylistResult> BuildEventPlaylistAsync(string eventUuid, string? streamName, DateTime? fromTs, DateTime? toTs, CancellationToken ct);
}

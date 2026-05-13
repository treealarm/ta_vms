namespace web_vms;

public abstract record HlsPlaylistResult
{
  public sealed record Ok(string Content) : HlsPlaylistResult;
  public sealed record NotFound(string Reason) : HlsPlaylistResult;
  public sealed record Error(string Reason) : HlsPlaylistResult;
}

public interface IHlsArchiveService
{
  Task<HlsPlaylistResult> BuildPreviewPlaylistAsync(string cameraId, TimelineSlice range, CancellationToken ct);
  Task<HlsPlaylistResult> BuildFilesPlaylistAsync(string cameraId, string startClipUid, uint count, CancellationToken ct);
  Task<HlsPlaylistResult> BuildEventPlaylistAsync(string eventId, string? streamName, CancellationToken ct);
}

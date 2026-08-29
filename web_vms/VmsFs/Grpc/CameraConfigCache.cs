using System.Collections.Concurrent;
using Vms.Cfg;
using web_vms.Types.Recording;

namespace VmsFs;

public sealed record CameraConfig(
  RecordingMode RecordingMode,
  int PreRecordSeconds,
  int PostRecordSeconds);

public sealed class CameraConfigCache(CfgService.CfgServiceClient cfgClient)
{
  private readonly ConcurrentDictionary<string, CameraConfig> _cache = new();

  public async Task<CameraConfig?> GetAsync(string cameraId, CancellationToken ct, string? streamName = null)
  {
    var key = streamName is null ? cameraId : $"{cameraId}:{streamName}";
    if (_cache.TryGetValue(key, out var cached))
      return cached;

    GetCameraResponse resp;
    try
    {
      resp = await cfgClient.GetCameraAsync(
        new SourceRequest { SourceId = cameraId }, cancellationToken: ct);
    }
    catch
    {
      return null;
    }

    if (!resp.Found || resp.Setup is null)
      return null;

    // Use the specific stream's profile when streamName is provided,
    // so per-stream override modes are respected during clip ingestion.
    var stream = streamName is not null
      ? (resp.Setup.Streams.FirstOrDefault(s => s.StreamName == streamName)
         ?? resp.Setup.Streams.FirstOrDefault())
      : resp.Setup.Streams.FirstOrDefault();

    var profile = stream?.OverrideProfile ?? stream?.RecordingProfile;

    var mode = RecordingModeProto.ToProto(RecordingRules.Effective(
      resp.Setup.ScheduleActive,
      stream is null || !stream.HasRecordable || stream.Recordable,
      stream?.OverrideProfile is null ? null : RecordingModeProto.FromProto(stream.OverrideProfile.Mode),
      stream?.RecordingProfile is null ? null : RecordingModeProto.FromProto(stream.RecordingProfile.Mode)));

    var config = new CameraConfig(
      mode,
      profile != null ? (int)profile.PreRecordSeconds : 0,
      profile != null ? (int)profile.PostRecordSeconds : 0);

    _cache.TryAdd(key, config);
    return config;
  }

  public void Invalidate(string cameraId)
  {
    foreach (var key in _cache.Keys.Where(k => k == cameraId || k.StartsWith($"{cameraId}:")))
      _cache.TryRemove(key, out _);
  }

  /// For "everything on this node changed", where the event names no cameras.
  public void InvalidateAll() => _cache.Clear();
}

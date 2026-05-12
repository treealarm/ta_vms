using System.Collections.Concurrent;
using Vms.Cfg;

namespace VmsFs;

public sealed record CameraConfig(
  RecordingMode RecordingMode,
  int PreRecordSeconds,
  int PostRecordSeconds);

public sealed class CameraConfigCache(CfgService.CfgServiceClient cfgClient)
{
  private readonly ConcurrentDictionary<string, CameraConfig> _cache = new();

  public async Task<CameraConfig?> GetAsync(string cameraId, CancellationToken ct)
  {
    if (_cache.TryGetValue(cameraId, out var cached))
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

    var firstStream = resp.Setup.Streams.FirstOrDefault();
    var profile = firstStream?.RecordingProfile;

    var config = new CameraConfig(
      profile?.Mode ?? RecordingMode.RecordingOff,
      profile != null ? (int)profile.PreRecordSeconds : 0,
      profile != null ? (int)profile.PostRecordSeconds : 0);

    _cache.TryAdd(cameraId, config);
    return config;
  }

  public void Invalidate(string cameraId) => _cache.TryRemove(cameraId, out _);
}

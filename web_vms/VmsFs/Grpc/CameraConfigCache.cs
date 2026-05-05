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

    CameraStateResponse resp;
    try
    {
      resp = await cfgClient.GetCameraStateAsync(
        new SourceRequest { SourceId = cameraId }, cancellationToken: ct);
    }
    catch
    {
      return null;
    }

    if (!resp.Found)
      return null;

    var config = new CameraConfig(
      resp.State.RecordingMode,
      resp.Setup.PreAlarm != null ? (int)resp.Setup.PreAlarm.PreRecordSeconds : 30,
      resp.Setup.PreAlarm != null ? (int)resp.Setup.PreAlarm.PostRecordSeconds : 30);

    _cache.TryAdd(cameraId, config);
    return config;
  }

  public void Invalidate(string cameraId) => _cache.TryRemove(cameraId, out _);
}

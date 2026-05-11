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

    var recordingMode = resp.Setup.Streams.FirstOrDefault()?.RecordingMode ?? RecordingMode.RecordingOff;
    var config = new CameraConfig(
      recordingMode,
      resp.Setup.PreAlarm != null ? (int)resp.Setup.PreAlarm.PreRecordSeconds : 10,
      resp.Setup.PreAlarm != null ? (int)resp.Setup.PreAlarm.PostRecordSeconds : 10);

    _cache.TryAdd(cameraId, config);
    return config;
  }

  public void Invalidate(string cameraId) => _cache.TryRemove(cameraId, out _);
}

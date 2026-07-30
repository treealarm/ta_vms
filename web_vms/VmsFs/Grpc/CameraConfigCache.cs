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

    // An open recording schedule window forces continuous recording, so every clip ingested while
    // it lasts must be retained regardless of the configured profile.
    var mode = resp.Setup.ScheduleActive
      ? RecordingMode.RecordingContinuous
      : profile?.Mode ?? RecordingMode.RecordingOff;

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
}

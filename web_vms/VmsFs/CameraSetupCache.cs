using System.Collections.Concurrent;
using Vms.Cfg;

namespace VmsFs;

public sealed record CameraStreamSetup(
  string StreamName,
  string ProfileToken,
  string Path,
  bool WebRtc,
  RecordingMode BaseMode,
  uint BaseSegmentSeconds);

public sealed record CameraSetup(
  string ServerId,
  IReadOnlyList<CameraStreamSetup> Streams,
  Credentials? Cred);

public sealed class CameraSetupCache(CfgService.CfgServiceClient cfgClient)
{
  private readonly ConcurrentDictionary<string, CameraSetup?> _cache = new();

  public async Task<CameraSetup?> GetAsync(string cameraId, CancellationToken ct)
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

    if (!resp.Found || resp.Setup is null || string.IsNullOrWhiteSpace(resp.Setup.ServerId))
    {
      _cache.TryAdd(cameraId, null);
      return null;
    }

    var setup = new CameraSetup(
      resp.Setup.ServerId,
      resp.Setup.Streams
        .Select(s =>
        {
          var profile = s.RecordingProfile;
          // BaseMode is what a stream falls back to once its alarm override expires, so it must
          // already account for an open recording schedule window — otherwise a camera inside a
          // window would be pushed back to its raw profile mode and stop recording.
          var baseMode = resp.Setup.ScheduleActive
            ? RecordingMode.RecordingContinuous
            : profile?.Mode ?? RecordingMode.RecordingOff;
          return new CameraStreamSetup(
            s.StreamName,
            s.ProfileToken,
            s.Path,
            s.WebRtc,
            baseMode,
            profile?.SegmentSeconds ?? 5u);
        })
        .ToList(),
      resp.Setup.Cred);

    _cache.TryAdd(cameraId, setup);
    return setup;
  }

  public void Invalidate(string cameraId) => _cache.TryRemove(cameraId, out _);
}

using System.Collections.Concurrent;
using Vms.Cfg;
using web_vms.Types.Actors;

namespace VmsOnvif;

public sealed record StreamRtspEntry(string ProfileToken, string Path);

public sealed record CameraConnData(
  CameraConnInfo? Conn,
  IReadOnlyList<StreamRtspEntry> Streams);

public sealed class CameraConnCache(CfgService.CfgServiceClient cfgClient)
{
  private readonly ConcurrentDictionary<string, CameraConnData> _cache = new();

  public async Task<CameraConnData?> GetAsync(string cameraId, CancellationToken ct)
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

    var setup = resp.Setup;

    CameraConnInfo? conn = null;
    if (setup.OnvifConn != null
      && !string.IsNullOrEmpty(setup.OnvifConn.Ip)
      && setup.OnvifConn.Port > 0)
    {
      conn = new CameraConnInfo(
        setup.OnvifConn.Ip,
        setup.OnvifConn.Port,
        setup.Cred?.User ?? string.Empty,
        setup.Cred?.Pass ?? string.Empty);
    }

    var streams = setup.Streams
      .Select(s => new StreamRtspEntry(s.ProfileToken, s.Path))
      .ToList();

    var data = new CameraConnData(conn, streams);
    _cache.TryAdd(cameraId, data);
    return data;
  }

  public void Invalidate(string cameraId) => _cache.TryRemove(cameraId, out _);
}

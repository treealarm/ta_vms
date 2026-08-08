using System.Collections.Concurrent;
using Vms.Onvif;

namespace web_vms;

/// Picks the VmsOnvif instance that can actually reach a given camera.
///
/// VmsOnvif is per node because ONVIF talks to a camera over the same local subnet RTSP does, so
/// "which instance" is decided by which media server owns the camera. Everything here is that
/// lookup, cached: PTZ arrives at joystick rates, and resolving the owner through VmsCfg on every
/// call would put a per-request config read back into a hot path.
///
/// The mapping only changes when an operator moves a camera between servers, so a short TTL is
/// enough. web_vms.Server runs in several replicas, which rules out pub/sub invalidation — each
/// replica would need a broadcast, and a Dapr topic delivers to exactly one of them.
public sealed class OnvifRouter(ICameraManager cameras, ServerPairRegistry registry)
{
  private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

  private readonly ConcurrentDictionary<string, (string ServerId, DateTime ResolvedAt)> _cache =
    new(StringComparer.Ordinal);

  public async Task<OnvifService.OnvifServiceClient> OnvifAsync(string cameraId, CancellationToken ct) =>
    registry.GetOnvifClient(await ServerIdAsync(cameraId, ct));

  public async Task<PtzService.PtzServiceClient> PtzAsync(string cameraId, CancellationToken ct) =>
    registry.GetPtzClient(await ServerIdAsync(cameraId, ct));

  public async Task<DeviceIoService.DeviceIoServiceClient> DeviceIoAsync(string cameraId, CancellationToken ct) =>
    registry.GetDeviceIoClient(await ServerIdAsync(cameraId, ct));

  private async Task<string> ServerIdAsync(string cameraId, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(cameraId))
      throw new InvalidOperationException("cameraId must not be empty for ONVIF routing");

    if (_cache.TryGetValue(cameraId, out var cached) && DateTime.UtcNow - cached.ResolvedAt < Ttl)
      return cached.ServerId;

    var camera = await cameras.Get(cameraId, ct);
    if (camera is null || string.IsNullOrWhiteSpace(camera.ServerId))
      throw new InvalidOperationException($"Camera '{cameraId}' is unknown or has no media server assigned");

    _cache[cameraId] = (camera.ServerId, DateTime.UtcNow);
    return camera.ServerId;
  }
}

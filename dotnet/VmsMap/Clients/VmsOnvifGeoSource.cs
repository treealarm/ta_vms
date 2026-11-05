using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Dapr.Client;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Vms.Onvif;

namespace VmsMap.Clients
{
  // Asks the camera's own node's VmsOnvif, because ONVIF only reaches cameras on its local subnet.
  // The call is short and best-effort: a camera that cannot be asked this second is asked again on
  // the next tick, and nothing else on the map waits for it.
  internal sealed class VmsOnvifGeoSource : ICameraGeoSource
  {
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);

    private readonly IMediaServerRegistry _servers;
    private readonly ILogger<VmsOnvifGeoSource> _logger;
    private readonly ConcurrentDictionary<string, DeviceIoService.DeviceIoServiceClient> _clients =
      new(StringComparer.Ordinal);

    public VmsOnvifGeoSource(IMediaServerRegistry servers, ILogger<VmsOnvifGeoSource> logger)
    {
      _servers = servers ?? throw new ArgumentNullException(nameof(servers));
      _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<CameraGeoPoint?> GetAsync(CameraInfo camera, CancellationToken ct)
    {
      if (string.IsNullOrWhiteSpace(camera.ServerId))
        return null;

      try
      {
        var onvifAppId = await _servers.OnvifForServerAsync(camera.ServerId, ct);
        var response = await Client(onvifAppId).GetCameraGeoLocationAsync(
          new CameraRequest { SourceId = camera.CameraId },
          new CallOptions(deadline: DateTime.UtcNow.Add(CallTimeout), cancellationToken: ct));

        // found=false is an answer, not a failure: the camera is simply not reporting a place.
        if (!string.IsNullOrEmpty(response.Error) || !response.Found)
          return null;

        // Both coordinates or none: half a position is not a place, and an absent latitude is not
        // the equator.
        return response.HasLat && response.HasLon
          ? new CameraGeoPoint(response.Lat, response.Lon)
          : null;
      }
      catch (OperationCanceledException) when (ct.IsCancellationRequested)
      {
        throw;
      }
      catch (Exception ex)
      {
        _logger.LogWarning(ex, "Reading the ONVIF position of camera '{CameraId}' failed", camera.CameraId);
        return null;
      }
    }

    // One invoker per node, reused: the follower asks every flagged camera every five seconds.
    private DeviceIoService.DeviceIoServiceClient Client(string onvifAppId) =>
      _clients.GetOrAdd(onvifAppId, appId => new DeviceIoService.DeviceIoServiceClient(
        DaprClient.CreateInvocationInvoker(appId)));
  }
}

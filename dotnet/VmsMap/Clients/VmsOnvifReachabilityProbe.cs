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
  // Asks the camera's own node's VmsOnvif whether the camera is present on the network. The call is
  // short and best-effort: a camera that cannot be asked this second is asked again on the next
  // tick, and nothing on the map waits for it.
  internal sealed class VmsOnvifReachabilityProbe : ICameraReachabilityProbe
  {
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(6);

    private readonly IMediaServerRegistry _servers;
    private readonly ILogger<VmsOnvifReachabilityProbe> _logger;
    private readonly ConcurrentDictionary<string, OnvifService.OnvifServiceClient> _clients =
      new(StringComparer.Ordinal);

    public VmsOnvifReachabilityProbe(
      IMediaServerRegistry servers, ILogger<VmsOnvifReachabilityProbe> logger)
    {
      _servers = servers ?? throw new ArgumentNullException(nameof(servers));
      _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<CameraReachability> ProbeAsync(CameraInfo camera, CancellationToken ct)
    {
      if (string.IsNullOrWhiteSpace(camera.ServerId))
        return CameraReachability.Unknown;
      if (!camera.HasOnvif)
        return CameraReachability.NoOnvif;

      try
      {
        var onvifAppId = await _servers.TryOnvifForServerAsync(camera.ServerId, ct);
        if (onvifAppId is null)
          return CameraReachability.NoOnvif; // this node has no VmsOnvif at all

        var options = new CallOptions(
          deadline: DateTime.UtcNow.Add(CallTimeout), cancellationToken: ct);
        var response = await Client(onvifAppId).ProbeReachabilityAsync(
          new CameraRequest { SourceId = camera.CameraId }, options);

        return response.Outcome switch
        {
          Reachability.Reachable => CameraReachability.Reachable,
          Reachability.Unreachable => CameraReachability.Unreachable,
          Reachability.NoOnvif => CameraReachability.NoOnvif,
          _ => CameraReachability.Unknown,
        };
      }
      catch (OperationCanceledException) when (ct.IsCancellationRequested)
      {
        throw;
      }
      catch (Exception ex)
      {
        // The question could not be put. That is not the camera's answer: the caller keeps the
        // last known state rather than turning every camera red when VmsOnvif is down.
        _logger.LogDebug(ex, "Reachability probe failed for camera '{CameraId}'", camera.CameraId);
        return CameraReachability.Unknown;
      }
    }

    private OnvifService.OnvifServiceClient Client(string onvifAppId) =>
      _clients.GetOrAdd(onvifAppId, appId => new OnvifService.OnvifServiceClient(
        DaprClient.CreateInvocationInvoker(appId)));
  }
}

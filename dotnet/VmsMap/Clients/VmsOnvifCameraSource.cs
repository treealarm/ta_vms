using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapr.Client;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Vms.Onvif;

namespace VmsMap.Clients
{
  // Everything the map asks a camera about itself: where it stands, and what its relays and inputs
  // are doing. The question goes to the camera's own node's VmsOnvif — ONVIF only reaches cameras on
  // its local subnet — and every call is short and best-effort: a camera that cannot be asked this
  // second is asked again on the next tick, and nothing else on the map waits for it.
  internal sealed class VmsOnvifCameraSource : ICameraGeoSource, ICameraDeviceIoSource
  {
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);

    private readonly IMediaServerRegistry _servers;
    private readonly ILogger<VmsOnvifCameraSource> _logger;
    private readonly ConcurrentDictionary<string, DeviceIoService.DeviceIoServiceClient> _clients =
      new(StringComparer.Ordinal);

    public VmsOnvifCameraSource(IMediaServerRegistry servers, ILogger<VmsOnvifCameraSource> logger)
    {
      _servers = servers ?? throw new ArgumentNullException(nameof(servers));
      _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<CameraGeoPoint?> GetAsync(CameraInfo camera, CancellationToken ct)
    {
      var response = await AskAsync(camera, (client, options) =>
        client.GetCameraGeoLocationAsync(new CameraRequest { SourceId = camera.CameraId }, options), ct);

      // found=false is an answer, not a failure: the camera is simply not reporting a place.
      if (response is null || !string.IsNullOrEmpty(response.Error) || !response.Found)
        return null;

      // Both coordinates or none: half a position is not a place, and an absent latitude is not the
      // equator. The heading is the camera's to say or not: a fixed one reports the bearing it was
      // mounted at, one on a vehicle the bearing it is driving at.
      return response.HasLat && response.HasLon
        ? new CameraGeoPoint(response.Lat, response.Lon,
            response.HasHeadingDeg ? ((response.HeadingDeg % 360) + 360) % 360 : null)
        : null;
    }

    public async Task<CameraDeviceState> GetRelayAsync(CameraInfo camera, CancellationToken ct)
    {
      var response = await AskAsync(camera, (client, options) =>
        client.GetRelayOutputsAsync(new CameraRequest { SourceId = camera.CameraId }, options), ct);
      if (response is null || !string.IsNullOrEmpty(response.Error))
        return new CameraDeviceState(false, null);

      var relay = response.Relays.FirstOrDefault();
      return relay is null
        ? new CameraDeviceState(false, null)
        : new CameraDeviceState(true, relay.HasLiveActive ? relay.LiveActive : null);
    }

    public async Task<CameraDeviceState> GetInputAsync(CameraInfo camera, CancellationToken ct)
    {
      var response = await AskAsync(camera, (client, options) =>
        client.GetDigitalInputsAsync(new CameraRequest { SourceId = camera.CameraId }, options), ct);
      if (response is null || !string.IsNullOrEmpty(response.Error))
        return new CameraDeviceState(false, null);

      var input = response.Inputs.FirstOrDefault();
      return input is null
        ? new CameraDeviceState(false, null)
        : new CameraDeviceState(true, input.HasLiveActive ? input.LiveActive : null);
    }

    private async Task<T?> AskAsync<T>(
      CameraInfo camera,
      Func<DeviceIoService.DeviceIoServiceClient, CallOptions, AsyncUnaryCall<T>> call,
      CancellationToken ct)
      where T : class
    {
      if (string.IsNullOrWhiteSpace(camera.ServerId))
        return null;

      try
      {
        var onvifAppId = await _servers.OnvifForServerAsync(camera.ServerId, ct);
        var options = new CallOptions(
          deadline: DateTime.UtcNow.Add(CallTimeout), cancellationToken: ct);
        return await call(Client(onvifAppId), options);
      }
      catch (OperationCanceledException) when (ct.IsCancellationRequested)
      {
        throw;
      }
      catch (Exception ex)
      {
        _logger.LogWarning(ex, "Asking camera '{CameraId}' over ONVIF failed", camera.CameraId);
        return null;
      }
    }

    // One invoker per node, reused: the follower asks every flagged camera every five seconds.
    private DeviceIoService.DeviceIoServiceClient Client(string onvifAppId) =>
      _clients.GetOrAdd(onvifAppId, appId => new DeviceIoService.DeviceIoServiceClient(
        DaprClient.CreateInvocationInvoker(appId)));
  }
}

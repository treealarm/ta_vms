using Dapr.Client;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Vms.Cfg;
using Vms.Control;
using RecordingMode = Vms.Cfg.RecordingMode;

namespace VmsFs;

// Activates alarm recording overrides. Expiry is not handled here: it is global periodic work over
// VmsCfg's own table and lives in VmsCfg.AlarmOverrideExpiryService, so it runs once for the whole
// system instead of once per VmsFs instance.
public sealed class AlarmRecordingService(
  CfgService.CfgServiceClient cfgClient,
  CameraSetupCache setupCache,
  ILogger<AlarmRecordingService> logger)
{
  private static readonly TimeSpan CfgCallTimeout = TimeSpan.FromSeconds(5);
  private static readonly TimeSpan MediaCallTimeout = TimeSpan.FromSeconds(5);

  private static CallOptions CfgOptions(CancellationToken ct) =>
    new(deadline: DateTime.UtcNow.Add(CfgCallTimeout), cancellationToken: ct);

  private static CallOptions MediaOptions(CancellationToken ct) =>
    new(deadline: DateTime.UtcNow.Add(MediaCallTimeout), cancellationToken: ct);

  private static CameraControlService.CameraControlServiceClient GetMediaClient(string serverId) =>
    new(DaprClient.CreateInvocationInvoker(serverId));

  public async Task TryActivateAsync(string cameraId, string? streamName, DateTime until, CancellationToken ct)
  {
    try
    {
      var req = new ActivateAlarmOverrideRequest
      {
        CameraId = cameraId,
        Until    = Timestamp.FromDateTime(until),
      };
      if (streamName is not null)
        req.StreamName = streamName;

      var resp = await cfgClient.ActivateAlarmOverrideAsync(req, CfgOptions(ct));
      if (resp.Activated)
      {
        logger.LogInformation("Alarm recording activated for camera '{CameraId}' stream='{StreamName}' until={Until}", cameraId, streamName, until);
        await PushCameraStateAsync(cameraId, streamName, ct);
      }
    }
    catch (Exception ex)
    {
      logger.LogError(ex, "AlarmRecordingService.TryActivateAsync '{CameraId}'", cameraId);
    }
  }

  // Pushes the override straight to the owning media_server rather than going through a config
  // change event: an alarm has to start recording now, and the server is on this same node.
  // streamName: null = all streams on the camera; non-null = only that stream.
  private async Task PushCameraStateAsync(string cameraId, string? streamName, CancellationToken ct)
  {
    var setup = await setupCache.GetAsync(cameraId, ct);
    if (setup is null)
    {
      logger.LogWarning("AlarmRecordingService: no setup cached for '{CameraId}'", cameraId);
      return;
    }

    foreach (var stream in setup.Streams)
    {
      if (streamName is not null && stream.StreamName != streamName)
        continue;

      // WebRtc deliberately left unset: alarm overrides only change the recording policy, and
      // setting it would stop a MediaMTX push started on demand (live view, analytics).
      var state = new CameraState
      {
        CameraId      = cameraId,
        StreamName    = stream.StreamName,
        RecordingMode = RecordingMode.RecordingContinuous,
      };

      try
      {
        await GetMediaClient(setup.ServerId).SetCameraStateAsync(state, MediaOptions(ct));
      }
      catch (Exception ex)
      {
        logger.LogError(ex, "AlarmRecordingService: SetCameraState failed for '{CameraId}/{StreamName}' on server '{ServerId}'", cameraId, stream.StreamName, setup.ServerId);
      }
    }
  }
}

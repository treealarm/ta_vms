using Dapr.Client;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Vms.Cfg;
using Vms.Control;
using RecordingMode = Vms.Cfg.RecordingMode;

namespace VmsFs;

public sealed class AlarmRecordingService(
  CfgService.CfgServiceClient cfgClient,
  CameraSetupCache setupCache) : BackgroundService
{
  private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
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
        await PushCameraStateAsync(cameraId, streamName, RecordingMode.RecordingContinuous, ct);
    }
    catch (Exception ex)
    {
      Console.Error.WriteLine($"AlarmRecordingService.TryActivateAsync '{cameraId}': {ex.Message}");
    }
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    while (!stoppingToken.IsCancellationRequested)
    {
      await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
      await SweepExpiredAsync(stoppingToken).ConfigureAwait(false);
    }
  }

  private async Task SweepExpiredAsync(CancellationToken ct)
  {
    ListAlarmOverrideStreamsResponse expired;
    try
    {
      expired = await cfgClient.ListAlarmOverrideStreamsAsync(
        new ListAlarmOverrideStreamsRequest(), CfgOptions(ct));
    }
    catch (Exception ex)
    {
      Console.Error.WriteLine($"AlarmRecordingService: ListAlarmOverrideStreams failed: {ex.Message}");
      return;
    }

    foreach (var stream in expired.Streams)
    {
      try
      {
        var resp = await cfgClient.ClearAlarmOverrideAsync(
          new ClearAlarmOverrideRequest { CameraId = stream.CameraId, StreamName = stream.StreamName },
          CfgOptions(ct));
        if (resp.Success)
          await PushCameraStateAsync(stream.CameraId, stream.StreamName, null, ct);
      }
      catch (Exception ex)
      {
        Console.Error.WriteLine(
          $"AlarmRecordingService: error clearing '{stream.CameraId}/{stream.StreamName}': {ex.Message}");
      }
    }
  }

  // streamName: null = all streams on the camera; non-null = only that stream.
  // overrideMode: the mode to push (null = read from cached base profile).
  private async Task PushCameraStateAsync(string cameraId, string? streamName, RecordingMode? overrideMode, CancellationToken ct)
  {
    var setup = await setupCache.GetAsync(cameraId, ct);
    if (setup is null)
    {
      Console.Error.WriteLine($"AlarmRecordingService: no setup cached for '{cameraId}'");
      return;
    }

    foreach (var stream in setup.Streams)
    {
      if (streamName is not null && stream.StreamName != streamName)
        continue;
      var mode = overrideMode ?? stream.BaseMode;

      var state = new CameraState
      {
        CameraId      = cameraId,
        StreamName    = stream.StreamName,
        ProfileToken  = stream.ProfileToken,
        RtspUrl       = stream.RtspUrl,
        WebRtc        = stream.WebRtc,
        RecordingMode = mode,
      };

      if (mode == RecordingMode.RecordingPrealarm)
        state.SegmentSeconds = stream.BaseSegmentSeconds > 0 ? stream.BaseSegmentSeconds : 5u;

      if (setup.Cred != null)
        state.Cred = new Vms.Control.Credentials { User = setup.Cred.User, Pass = setup.Cred.Pass };

      try
      {
        await GetMediaClient(setup.ServerId).SetCameraStateAsync(state, MediaOptions(ct));
      }
      catch (Exception ex)
      {
        Console.Error.WriteLine($"AlarmRecordingService: SetCameraState failed for '{cameraId}/{stream.StreamName}' on server '{setup.ServerId}': {ex.Message}");
      }
    }
  }
}

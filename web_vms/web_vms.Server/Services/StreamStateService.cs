using Dapr.Client;
using Grpc.Core;
using Vms.Cfg;
using Vms.Control;
using web_vms.Types.Dto;
using RecordingMode = Vms.Cfg.RecordingMode;

namespace web_vms;

public sealed class StreamStateService(ICameraManager manager)
{
  private static readonly TimeSpan MediaCallTimeout = TimeSpan.FromSeconds(5);

  private static CallOptions MediaCallOptions(CancellationToken ct) =>
    new(deadline: DateTime.UtcNow.Add(MediaCallTimeout), cancellationToken: ct);

  private static CameraControlService.CameraControlServiceClient GetMediaClient(string serverId) =>
    new(DaprClient.CreateInvocationInvoker(serverId));

  public async Task PushStreamStateAsync(string cameraId, string streamName, CancellationToken ct)
  {
    var camera = await manager.Get(cameraId, ct);
    if (string.IsNullOrWhiteSpace(camera?.ServerId)) return;

    var stream = (camera.Streams ?? []).FirstOrDefault(s => s.StreamName == streamName);
    if (stream is null) return;

    var profile = stream.OverrideProfile ?? stream.RecordingProfile;
    // An open recording schedule window wins over the profile and over any override.
    var recordingMode = camera.ScheduleActive
      ? RecordingMode.RecordingContinuous
      : profile?.Mode switch
        {
          "continuous" => RecordingMode.RecordingContinuous,
          "prealarm"   => RecordingMode.RecordingPrealarm,
          _            => RecordingMode.RecordingOff,
        };

    // WebRtc deliberately left unset: this is a recording-state push only, and setting it
    // would stop a MediaMTX push started on demand (live view, analytics).
    var state = new CameraState
    {
      CameraId      = cameraId,
      StreamName    = streamName,
      RecordingMode = recordingMode,
    };

    if (recordingMode == RecordingMode.RecordingPrealarm && profile != null)
      state.SegmentSeconds = profile.SegmentSeconds > 0 ? profile.SegmentSeconds : 5u;

    try
    {
      await GetMediaClient(camera.ServerId).SetCameraStateAsync(state, MediaCallOptions(ct));
    }
    catch (Exception ex)
    {
      Console.Error.WriteLine($"StreamStateService: SetCameraState failed for '{cameraId}/{streamName}' on server '{camera.ServerId}': {ex.Message}");
    }
  }
}

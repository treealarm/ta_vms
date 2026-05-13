using Dapr.Client;
using Grpc.Core;
using Microsoft.AspNetCore.Mvc;
using Vms.Cfg;
using Vms.Control;
using web_vms.Types.Dto;
using RecordingMode = Vms.Cfg.RecordingMode;

namespace web_vms;

[ApiController]
[Route("api/recording-profiles")]
public class RecordingProfileController(
  CfgService.CfgServiceClient _cfgClient,
  ICameraManager _manager) : ControllerBase
{
  private static readonly TimeSpan CfgCallTimeout   = TimeSpan.FromSeconds(5);
  private static readonly TimeSpan MediaCallTimeout = TimeSpan.FromSeconds(5);

  private static CallOptions CfgOptions(CancellationToken ct) =>
    new(deadline: DateTime.UtcNow.Add(CfgCallTimeout), cancellationToken: ct);

  private static CallOptions MediaCallOptions(CancellationToken ct) =>
    new(deadline: DateTime.UtcNow.Add(MediaCallTimeout), cancellationToken: ct);

  private static CameraControlService.CameraControlServiceClient GetMediaClient(string serverId) =>
    new(DaprClient.CreateInvocationInvoker(serverId));

  [HttpGet]
  public async Task<IActionResult> List(CancellationToken ct)
  {
    var response = await _cfgClient.ListRecordingProfilesAsync(new ListRecordingProfilesRequest(), CfgOptions(ct));
    return Ok(response.Profiles.Select(ToDto));
  }

  [HttpPut]
  public async Task<IActionResult> Upsert([FromBody] RecordingProfileDto dto, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(dto.Name))
      return BadRequest("name must not be empty");

    var response = await _cfgClient.UpsertRecordingProfileAsync(ToProto(dto), CfgOptions(ct));
    return Ok(new ApiResponse(response.Success, response.Message));
  }

  [HttpDelete("{id}")]
  public async Task<IActionResult> Remove(string id, CancellationToken ct)
  {
    var response = await _cfgClient.RemoveRecordingProfileAsync(
      new RecordingProfileRequest { Id = id }, CfgOptions(ct));
    return Ok(new ApiResponse(response.Success, response.Message));
  }

  [HttpPost("stream-override")]
  public async Task<IActionResult> SetStreamOverride([FromBody] StreamOverrideDto dto, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(dto.CameraId) || string.IsNullOrWhiteSpace(dto.StreamName) || string.IsNullOrWhiteSpace(dto.ProfileId))
      return BadRequest("cameraId, streamName and profileId are required");

    var response = await _cfgClient.SetStreamOverrideAsync(new SetStreamOverrideRequest
    {
      CameraId   = dto.CameraId,
      StreamName = dto.StreamName,
      ProfileId  = dto.ProfileId,
    }, CfgOptions(ct));

    if (response.Success)
      await PushStreamStateToMediaServer(dto.CameraId, dto.StreamName, ct);

    return Ok(new ApiResponse(response.Success, response.Message));
  }

  [HttpDelete("stream-override")]
  public async Task<IActionResult> ClearStreamOverride([FromQuery] string cameraId, [FromQuery] string streamName, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(cameraId) || string.IsNullOrWhiteSpace(streamName))
      return BadRequest("cameraId and streamName are required");

    var response = await _cfgClient.ClearStreamOverrideAsync(new ClearStreamOverrideRequest
    {
      CameraId   = cameraId,
      StreamName = streamName,
    }, CfgOptions(ct));

    if (response.Success)
      await PushStreamStateToMediaServer(cameraId, streamName, ct);

    return Ok(new ApiResponse(response.Success, response.Message));
  }

  private async Task PushStreamStateToMediaServer(string cameraId, string streamName, CancellationToken ct)
  {
    var camera = await _manager.Get(cameraId);
    if (string.IsNullOrWhiteSpace(camera?.ServerId)) return;

    var stream = (camera.Streams ?? []).FirstOrDefault(s => s.StreamName == streamName);
    if (stream == null) return;

    var profile = stream.OverrideProfile ?? stream.RecordingProfile;
    var recordingMode = profile?.Mode switch
    {
      "continuous" => RecordingMode.RecordingContinuous,
      "prealarm"   => RecordingMode.RecordingPrealarm,
      _            => RecordingMode.RecordingOff,
    };

    var state = new CameraState
    {
      CameraId      = cameraId,
      StreamName    = streamName,
      RtspUrl       = stream.RtspUrl,
      WebRtc        = stream.WebRtc,
      RecordingMode = recordingMode,
    };

    if (recordingMode == RecordingMode.RecordingPrealarm && profile != null)
      state.SegmentSeconds = profile.SegmentSeconds > 0 ? profile.SegmentSeconds : 5u;

    if (camera.Cred != null)
      state.Cred = new Vms.Control.Credentials { User = camera.Cred.User, Pass = camera.Cred.Pass };

    try
    {
      await GetMediaClient(camera.ServerId).SetCameraStateAsync(state, MediaCallOptions(ct));
    }
    catch { /* best-effort */ }
  }

  private static RecordingProfileDto ToDto(RecordingProfile p) => new()
  {
    Id                = p.Id,
    Name              = p.Name,
    Mode              = RecordingModeToString(p.Mode),
    PreRecordSeconds  = p.PreRecordSeconds,
    PostRecordSeconds = p.PostRecordSeconds,
    SegmentSeconds    = p.SegmentSeconds,
    IsOverridePreset  = p.IsOverridePreset,
  };

  private static RecordingProfile ToProto(RecordingProfileDto dto) => new()
  {
    Id                = dto.Id ?? string.Empty,
    Name              = dto.Name,
    Mode              = ParseRecordingMode(dto.Mode),
    PreRecordSeconds  = dto.PreRecordSeconds,
    PostRecordSeconds = dto.PostRecordSeconds,
    SegmentSeconds    = dto.SegmentSeconds,
    IsOverridePreset  = dto.IsOverridePreset,
  };

  private static string RecordingModeToString(RecordingMode mode) => mode switch
  {
    RecordingMode.RecordingContinuous => "continuous",
    RecordingMode.RecordingPrealarm   => "prealarm",
    _                                 => "off",
  };

  private static RecordingMode ParseRecordingMode(string mode) => mode switch
  {
    "continuous" => RecordingMode.RecordingContinuous,
    "prealarm"   => RecordingMode.RecordingPrealarm,
    _            => RecordingMode.RecordingOff,
  };
}

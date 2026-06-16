using Grpc.Core;
using Microsoft.AspNetCore.Mvc;
using Vms.Cfg;
using web_vms.Types.Dto;
using RecordingMode = Vms.Cfg.RecordingMode;

namespace web_vms;

[ApiController]
[Route("api/recording-profiles")]
public class RecordingProfileController(
  CfgService.CfgServiceClient _cfgClient,
  StreamStateService _streamState) : ControllerBase
{
  private static readonly TimeSpan CfgCallTimeout = TimeSpan.FromSeconds(5);

  private static CallOptions CfgOptions(CancellationToken ct) =>
    new(deadline: DateTime.UtcNow.Add(CfgCallTimeout), cancellationToken: ct);

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
      await _streamState.PushStreamStateAsync(dto.CameraId, dto.StreamName, ct);

    return Ok(new ApiResponse(response.Success, response.Message));
  }

  [HttpPost("stream-profile")]
  public async Task<IActionResult> SetStreamRecordingProfile([FromBody] StreamOverrideDto dto, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(dto.CameraId) || string.IsNullOrWhiteSpace(dto.StreamName) || string.IsNullOrWhiteSpace(dto.ProfileId))
      return BadRequest("cameraId, streamName and profileId are required");

    var response = await _cfgClient.SetStreamRecordingProfileAsync(new SetStreamRecordingProfileRequest
    {
      CameraId   = dto.CameraId,
      StreamName = dto.StreamName,
      ProfileId  = dto.ProfileId,
    }, CfgOptions(ct));

    if (response.Success)
      await _streamState.PushStreamStateAsync(dto.CameraId, dto.StreamName, ct);

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
      await _streamState.PushStreamStateAsync(cameraId, streamName, ct);

    return Ok(new ApiResponse(response.Success, response.Message));
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
    IsBuiltin         = p.IsBuiltin,
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

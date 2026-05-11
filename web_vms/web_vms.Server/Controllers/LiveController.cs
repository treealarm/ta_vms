using Dapr.Client;
using Grpc.Core;
using Microsoft.AspNetCore.Mvc;
using Vms.Control;
using Vms.Fs;
using web_vms.Types.Dto;
using RecordingMode = Vms.Cfg.RecordingMode;

namespace web_vms;

[ApiController]
[Route("api/[controller]")]
public class LiveController(
  ICameraManager _manager,
  FsService.FsServiceClient _fsClient
  ) : ControllerBase
{
  private static readonly TimeSpan MediaCallTimeout = TimeSpan.FromSeconds(5);

  private CameraControlService.CameraControlServiceClient GetMediaClient(string? serverId)
  {
    if (string.IsNullOrWhiteSpace(serverId))
      throw new InvalidOperationException("serverId must not be empty for media server requests");

    return new CameraControlService.CameraControlServiceClient(DaprClient.CreateInvocationInvoker(serverId));
  }

  private static CallOptions MediaCallOptions(CancellationToken ct) =>
    new(deadline: DateTime.UtcNow.Add(MediaCallTimeout), cancellationToken: ct);

  private static string MediaServerError(RpcException ex) => ex.StatusCode switch
  {
    Grpc.Core.StatusCode.DeadlineExceeded => "Media server did not respond within timeout (is it running?)",
    Grpc.Core.StatusCode.Unavailable      => "Media server is unavailable",
    _                                     => string.IsNullOrWhiteSpace(ex.Status.Detail)
                                              ? $"Media server error: {ex.StatusCode}"
                                              : $"Media server error: {ex.Status.Detail}"
  };

  private static CameraState ToControlState(CameraSetupDto request)
  {
    var n = CameraManager.Normalize(request);
    var state = new CameraState
    {
      CameraId = n.CameraId,
      RtspUrl = n.Streams.FirstOrDefault()?.RtspUrl ?? n.RtspUrl ?? "",
      WebRtc = n.WebRtc,
      Cred = n.Cred != null
        ? new Credentials
        {
          User = n.Cred.User,
          Pass = n.Cred.Pass
        }
        : null,
      RecordingMode = n.RecordingMode switch
      {
        "continuous" => RecordingMode.RecordingContinuous,
        "prealarm" => RecordingMode.RecordingPrealarm,
        _ => RecordingMode.RecordingOff,
      }
    };

    if (state.RecordingMode == RecordingMode.RecordingPrealarm && n.PreAlarm != null)
      state.SegmentSeconds = n.PreAlarm.SegmentSeconds > 0 ? n.PreAlarm.SegmentSeconds : 5;

    return state;
  }

  [HttpPost("cameras")]
  public async Task<IActionResult> AddCamera([FromBody] CameraSetupDto request, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(request.ServerId))
    {
      return BadRequest("serverId must not be empty");
    }

    var normalized = CameraManager.Normalize(request);
    Vms.Control.OperationStatus response;
    try
    {
      response = await GetMediaClient(normalized.ServerId!).SetCameraStateAsync(
        ToControlState(normalized), MediaCallOptions(ct));
    }
    catch (RpcException ex)
    {
      return Ok(new ApiResponse(false, MediaServerError(ex)));
    }

    if (!response.Success)
      return Ok(ApiResponseMapper.ToApiResponse(response));

    try
    {
      await _manager.Add(normalized, ct);
    }
    catch (RpcException ex)
    {
      return Ok(new ApiResponse(false, $"Config service error: {MediaServerError(ex)}"));
    }

    return Ok(new ApiResponse(true, "ok"));
  }

  [HttpPut("cameras/{cameraId}/parent")]
  public async Task<IActionResult> ChangeCameraParent(
    string cameraId,
    [FromBody] CameraSetupDto request,
    CancellationToken ct)
  {
    if (cameraId != request.CameraId)
    {
      return BadRequest("cameraId in route and body must match");
    }

    if (string.IsNullOrWhiteSpace(request.ServerId))
    {
      return BadRequest("serverId must not be empty");
    }

    var current = await _manager.Get(cameraId);
    if (current is null)
    {
      return NotFound();
    }

    if (current.ServerId == request.ServerId)
    {
      await _manager.Add(CameraManager.Normalize(request), ct);
      return Ok(new ApiResponse(true, "Camera parent is unchanged"));
    }

    Vms.Control.OperationStatus removeResponse;
    try
    {
      removeResponse = await GetMediaClient(current.ServerId).RemoveCameraAsync(
        new SourceRequest { SourceId = cameraId }, MediaCallOptions(ct));
    }
    catch (RpcException ex)
    {
      return Ok(new ApiResponse(false, MediaServerError(ex)));
    }

    if (!removeResponse.Success)
      return Ok(ApiResponseMapper.ToApiResponse(removeResponse));

    var normalized = CameraManager.Normalize(request);
    Vms.Control.OperationStatus addResponse;
    try
    {
      addResponse = await GetMediaClient(normalized.ServerId!).SetCameraStateAsync(
        ToControlState(normalized), MediaCallOptions(ct));
    }
    catch (RpcException ex)
    {
      return Ok(new ApiResponse(false, MediaServerError(ex)));
    }

    if (!addResponse.Success)
      return Ok(ApiResponseMapper.ToApiResponse(addResponse));

    try
    {
      await _manager.Add(normalized, ct);
    }
    catch (RpcException ex)
    {
      return Ok(new ApiResponse(false, $"Config service error: {MediaServerError(ex)}"));
    }

    return Ok(new ApiResponse(true, "ok"));
  }

  [HttpDelete("cameras/{cameraId}")]
  public async Task<IActionResult> RemoveCamera(string cameraId, CancellationToken ct)
  {
    var current = await _manager.Get(cameraId);
    if (string.IsNullOrWhiteSpace(current?.ServerId))
    {
      return NotFound();
    }

    Vms.Control.OperationStatus response;
    try
    {
      response = await GetMediaClient(current.ServerId).RemoveCameraAsync(
        new SourceRequest { SourceId = cameraId }, MediaCallOptions(ct));
    }
    catch (RpcException ex)
    {
      return Ok(new ApiResponse(false, MediaServerError(ex)));
    }

    if (response.Success)
      await _manager.Remove(cameraId, ct);

    return Ok(ApiResponseMapper.ToApiResponse(response));
  }

  [HttpGet("servers")]
  public async Task<IActionResult> ListServers(CancellationToken ct)
  {
    return Ok(await _manager.ListServers());
  }

  [HttpGet("cameras")]
  public async Task<IActionResult> ListCameras(CancellationToken ct)
  {
    return Ok(await _manager.List());
  }

  [HttpPost("cameras/state")]
  public async Task<IActionResult> SetCameraState([FromBody] CameraSetupDto state, CancellationToken ct)
  {
    var curState = await _manager.Get(state.CameraId);
    if (curState != null && state.OnvifConn == null)
    {
      state.OnvifConn = curState.OnvifConn;
    }

    var targetServerId = state.ServerId ?? curState?.ServerId;
    if (string.IsNullOrWhiteSpace(targetServerId))
    {
      return NotFound();
    }

    var normalized = CameraManager.Normalize(state with { ServerId = targetServerId });
    Vms.Control.OperationStatus response;
    try
    {
      response = await GetMediaClient(targetServerId).SetCameraStateAsync(
        ToControlState(normalized), MediaCallOptions(ct));
    }
    catch (RpcException ex)
    {
      return Ok(new ApiResponse(false, MediaServerError(ex)));
    }

    if (response.Success)
      await _manager.SetSetup(normalized, ct);

    return Ok(ApiResponseMapper.ToApiResponse(response));
  }

  [HttpGet("cameras/{cameraId}/state")]
  public async Task<IActionResult> GetCameraState(string cameraId, CancellationToken ct)
  {
    var camera = await _manager.Get(cameraId);
    if (camera is null)
    {
      return NotFound();
    }

    var normalized = CameraManager.Normalize(camera);

    if (string.Equals(normalized.RecordingMode, "off", StringComparison.OrdinalIgnoreCase))
    {
      return Ok(normalized with { ActiveRetentionWindow = false });
    }

    try
    {
      var retention = await _fsClient.GetCameraRetentionStatusAsync(
        new GetCameraRetentionStatusRequest { CameraId = cameraId },
        cancellationToken: ct);
      return Ok(normalized with { ActiveRetentionWindow = retention.ActiveRetentionWindow });
    }
    catch (RpcException)
    {
      return Ok(normalized with { ActiveRetentionWindow = false });
    }
  }
}

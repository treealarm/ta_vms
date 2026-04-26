using Dapr.Client;
using Microsoft.AspNetCore.Mvc;
using Vms.Control;
using web_vms.Types.Dto;

namespace web_vms;

[ApiController]
[Route("api/[controller]")]
public class LiveController(
  ICameraManager _manager
  ) : ControllerBase
{
  private CameraControlService.CameraControlServiceClient GetMediaClient(string? serverId)
  {
    if (string.IsNullOrWhiteSpace(serverId))
    {
      throw new InvalidOperationException("serverId must not be empty for media server requests");
    }

    return new CameraControlService.CameraControlServiceClient(DaprClient.CreateInvocationInvoker(serverId));
  }

  private static CameraState ToControlState(CameraStateDto request) => new()
  {
    CameraId = request.CameraId,
    RtspUrl = request.RtspUrl,
    Recording = request.Recording,
    WebRtc = request.WebRtc,
    Cred = request.Cred != null
      ? new Credentials
      {
        User = request.Cred.User,
        Pass = request.Cred.Pass
      }
      : null
  };

  [HttpPost("cameras")]
  public async Task<IActionResult> AddCamera([FromBody] CameraStateDto request, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(request.ServerId))
    {
      return BadRequest("serverId must not be empty");
    }

    var response = await GetMediaClient(request.ServerId).AddCameraAsync(ToControlState(request), cancellationToken: ct);

    if (response.Success)
    {
      await _manager.Add(request);
    }

    return Ok(ApiResponseMapper.ToApiResponse(response));
  }

  [HttpPut("cameras/{cameraId}/parent")]
  public async Task<IActionResult> ChangeCameraParent(
    string cameraId,
    [FromBody] CameraStateDto request,
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
      await _manager.Add(request);
      return Ok(new ApiResponse(true, "Camera parent is unchanged"));
    }

    var removeResponse = await GetMediaClient(current.ServerId).RemoveCameraAsync(
      new SourceRequest { SourceId = cameraId },
      cancellationToken: ct);

    if (!removeResponse.Success)
    {
      return Ok(ApiResponseMapper.ToApiResponse(removeResponse));
    }

    var addResponse = await GetMediaClient(request.ServerId).AddCameraAsync(ToControlState(request), cancellationToken: ct);
    if (addResponse.Success)
    {
      await _manager.Add(request);
    }

    return Ok(ApiResponseMapper.ToApiResponse(addResponse));
  }

  [HttpDelete("cameras/{cameraId}")]
  public async Task<IActionResult> RemoveCamera(string cameraId, CancellationToken ct)
  {
    var current = await _manager.Get(cameraId);
    if (string.IsNullOrWhiteSpace(current?.ServerId))
    {
      return NotFound();
    }

    var response = await GetMediaClient(current.ServerId).RemoveCameraAsync(new SourceRequest
    {
      SourceId = cameraId
    }, cancellationToken: ct);

    if (response.Success)
    {
      await _manager.Remove(cameraId);
    }

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
  public async Task<IActionResult> SetCameraState([FromBody] CameraStateDto state, CancellationToken ct)
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

    var response = await GetMediaClient(targetServerId).SetCameraStateAsync(ToControlState(state), cancellationToken: ct);

    if (response.Success)
    {
      await _manager.SetState(state);
    }

    return Ok(ApiResponseMapper.ToApiResponse(response));
  }

  [HttpGet("cameras/{cameraId}/state")]
  public async Task<IActionResult> GetCameraState(string cameraId, CancellationToken ct)
  {
    var camera = await _manager.Get(cameraId);
    return camera is null ? NotFound() : Ok(camera);
  }
}

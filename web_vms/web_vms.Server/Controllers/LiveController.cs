using DeviceServiceReference;
using Microsoft.AspNetCore.Mvc;
using Vms.Control;
using web_vms.Types.Dto;

namespace web_vms;

[ApiController]
[Route("api/[controller]")]
public class LiveController(
  CameraControlService.CameraControlServiceClient _grpcClient,
  ICameraManager _manager
  ) : ControllerBase
{
  [HttpPost("cameras")]
  public async Task<IActionResult> AddCamera([FromBody] CameraStateDto request, CancellationToken ct)
  {
    var response = await _grpcClient.AddCameraAsync(new CameraState
    {
      CameraId = request.CameraId,
      RtspUrl = request.RtspUrl,
      Recording = request.Recording,
      WebRtc = request.WebRtc,
      Cred = request.Cred!=null ?
      new Credentials()
      { 
        User= request.Cred.User,
        Pass = request.Cred.Pass
      } : null

    }, cancellationToken: ct);

    await _manager.Add(request);

    return Ok(ApiResponseMapper.ToApiResponse(response));
  }

  [HttpDelete("cameras/{cameraId}")]
  public async Task<IActionResult> RemoveCamera(string cameraId, CancellationToken ct)
  {
    var response = await _grpcClient.RemoveCameraAsync(new SourceRequest
    {
      SourceId = cameraId
    }, cancellationToken: ct);

    await _manager.Remove(cameraId);
    return Ok(ApiResponseMapper.ToApiResponse(response));
  }

  [HttpGet("cameras")]
  public async Task<IActionResult> ListCameras(CancellationToken ct)
  {
    var response = await _grpcClient.ListCamerasAsync(new ListCamerasRequest(), cancellationToken: ct);
    var cameras = response.Cameras.Select(camera =>
        new CameraStateDto
        {
          CameraId = camera.CameraId,
          Recording = camera.Recording,
          WebRtc = camera.WebRtc,
          RtspUrl = camera.RtspUrl,
          Cred = camera.Cred != null
                ? new CredentialsDto
                {
                  User = camera.Cred.User,
                  Pass = camera.Cred.Pass
                }
                : null
        }
    );

    return Ok(cameras);
  }

  [HttpPost("cameras/state")]
  public async Task<IActionResult> SetCameraState([FromBody] CameraStateDto state, CancellationToken ct)
  {
    var response = await _grpcClient.SetCameraStateAsync(new CameraState
    {
      CameraId = state.CameraId,
      Recording = state.Recording,
      WebRtc = state.WebRtc,
      RtspUrl = state.RtspUrl
      ,
      Cred = state.Cred != null ?
      new Credentials()
      {
        User = state.Cred.User,
        Pass = state.Cred.Pass
      } : null
    }, cancellationToken: ct);

    var curState = await _manager.Get(state.CameraId);
    if (curState != null && state.OnvifConn == null)
    {
      state.OnvifConn = curState.OnvifConn;
    }
    await _manager.SetState(state);
    return Ok(ApiResponseMapper.ToApiResponse(response));
  }

  [HttpGet("cameras/{cameraId}/state")]
  public async Task<IActionResult> GetCameraState(string cameraId, CancellationToken ct)
  {
    var camera = await _grpcClient.GetCameraStateAsync(new SourceRequest
    {
      SourceId = cameraId
    }, cancellationToken: ct);

    var dto = new CameraStateDto
    {
      Recording = camera.Recording,
      WebRtc = camera.WebRtc,
      CameraId = camera.CameraId,
      RtspUrl = camera.RtspUrl,
      Cred = camera.Cred != null
        ? new CredentialsDto
        {
          User = camera.Cred.User,
          Pass = camera.Cred.Pass
        }
        : null
    };

    return Ok(dto);
  }
}

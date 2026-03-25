using Microsoft.AspNetCore.Mvc;
using Vms.Control;

namespace web_vms;

[ApiController]
[Route("api/[controller]")]
public class LiveController(
  CameraControlService.CameraControlServiceClient grpcClient) : ControllerBase
{
  [HttpPost("cameras")]
  public async Task<IActionResult> AddCamera([FromBody] CameraStateDto request, CancellationToken ct)
  {
    var response = await grpcClient.AddCameraAsync(new CameraState
    {
      CameraId = request.CameraId,
      RtspUrl = request.RtspUrl,
      Recording = request.Recording,
      WebRtc = true

    }, cancellationToken: ct);

    return Ok(ApiResponseMapper.ToApiResponse(response));
  }

  [HttpDelete("cameras/{cameraId}")]
  public async Task<IActionResult> RemoveCamera(string cameraId, CancellationToken ct)
  {
    var response = await grpcClient.RemoveCameraAsync(new SourceRequest
    {
      SourceId = cameraId
    }, cancellationToken: ct);

    return Ok(ApiResponseMapper.ToApiResponse(response));
  }

  [HttpGet("cameras")]
  public async Task<IActionResult> ListCameras(CancellationToken ct)
  {
    var response = await grpcClient.ListCamerasAsync(new ListCamerasRequest(), cancellationToken: ct);
    var cameras = response.Cameras.Select(
      camera =>
      new CameraStateDto
      {
        CameraId = camera.CameraId,
        Recording = camera.Recording,
        WebRtc = camera.WebRtc,
        RtspUrl = camera.RtspUrl
      }
      );
    return Ok(cameras);
  }

  [HttpPost("cameras/state")]
  public async Task<IActionResult> SetCameraState([FromBody] CameraStateDto state, CancellationToken ct)
  {
    var response = await grpcClient.SetCameraStateAsync(new CameraState
    {
      CameraId = state.CameraId,
      Recording = state.Recording,
      WebRtc = state.WebRtc
    }, cancellationToken: ct);

    return Ok(ApiResponseMapper.ToApiResponse(response));
  }

  [HttpGet("cameras/{cameraId}/state")]
  public async Task<IActionResult> GetCameraState(string cameraId, CancellationToken ct)
  {
    var grpcResponse = await grpcClient.GetCameraStateAsync(new SourceRequest
    {
      SourceId = cameraId
    }, cancellationToken: ct);

    var dto = new CameraStateDto
    {
      Recording = grpcResponse.Recording,
      WebRtc = grpcResponse.WebRtc,
      CameraId = grpcResponse.CameraId
    };

    return Ok(dto);
  }
}

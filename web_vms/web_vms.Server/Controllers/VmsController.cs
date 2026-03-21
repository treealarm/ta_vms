using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Mvc;
using Vms.Control;

namespace web_vms;

[ApiController]
[Route("api/vms")]
public class VmsController(
  CameraControlService.CameraControlServiceClient grpcClient) : ControllerBase
{
  [HttpPost("cameras")]
  public async Task<IActionResult> AddCamera([FromBody] AddCameraDto request, CancellationToken ct)
  {
    var response = await grpcClient.AddCameraAsync(new AddCameraRequest
    {
      CameraId = request.CameraId,
      RtspUrl = request.RtspUrl,
      State = new CameraState
      {
        Recording = request.State?.Recording ?? false,
        WebRtc = true
      }
    }, cancellationToken: ct);

    return Ok(ToApiResponse(response));
  }

  [HttpPost("players")]
  public async Task<IActionResult> AddPlayer([FromBody] AddPlayerDto request, CancellationToken ct)
  {
      var dt = request.StartTime ?? DateTime.UtcNow;

      if (dt.Kind != DateTimeKind.Utc)
        dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);

      var StartTimeStamp = Timestamp.FromDateTime(dt);

      var startPlayResponse = await grpcClient.AddArchPlayerAsync(new PlayerState
      {
        CameraId = request.CameraId,
        Position = StartTimeStamp,
        ClientId = request.ClientId
      }, cancellationToken: ct);

      return Ok(ToApiResponse(startPlayResponse));
  }


  [HttpPost("players/state")]
  public async Task<IActionResult> SetPlayerState([FromBody] PlayerStateDto dto, CancellationToken ct)
  {
    if (dto == null)
      return BadRequest("Request body is missing");

    var request = new PlayerState
    {
      CameraId = dto.CameraId,
      ClientId = dto.ClientId
    };

    if (dto.Position.HasValue)
    {
      var dt = dto.Position.Value;

      DateTime dtUtc;

      if (dt.Kind == DateTimeKind.Utc)
      {
        dtUtc = dt;
      }
      else if (dt.Kind == DateTimeKind.Local)
      {
        dtUtc = dt.ToUniversalTime();
      }
      else // Unspecified
      {
        // считаем, что фронт прислал UTC (обычно так и есть)
        dtUtc = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
      }

      request.Position = Timestamp.FromDateTime(dtUtc);
    }

    var grpcResponse = await grpcClient.SetPlayerStateAsync(request, cancellationToken: ct);

    return Ok(new
    {
      success = grpcResponse.Success,
      message = grpcResponse.Message
    });
  }

  [HttpDelete("cameras/{cameraId}")]
  public async Task<IActionResult> RemoveCamera(string cameraId, CancellationToken ct)
  {
    OperationStatus response;

    {
      response = await grpcClient.RemoveCameraAsync(new SourceRequest
      {
        SourceId = cameraId
      }, cancellationToken: ct);
    }

    return Ok(ToApiResponse(response));
  }

  [HttpGet("cameras")]
  public async Task<IActionResult> ListCameras(CancellationToken ct)
  {
    var response = await grpcClient.ListCamerasAsync(new ListCamerasRequest(), cancellationToken: ct);
    var cameras = response.Cameras.Select(static camera => new CameraDto(camera.CameraId, camera.RtspUrl));
    return Ok(cameras);
  }

  [HttpDelete("players/{clientId}")]
  public async Task<IActionResult> RemovePlayer(string clientId, CancellationToken ct)
  {
    OperationStatus response;

      response = await grpcClient.RemoveArchPlayerAsync(new SourceRequest
      {
        SourceId = clientId
      }, cancellationToken: ct);

    return Ok(ToApiResponse(response));
  }

  [HttpGet("players")]
  public async Task<IActionResult> GetPlayers(CancellationToken ct)
  {
    var grpcResponse = await grpcClient.ListPlayersAsync(
        new ListPlayersRequest(),
        cancellationToken: ct);

    var result = grpcResponse.Players.Select(p => new PlayerStateDto
    {
      CameraId = p.CameraId,
      ClientId = p.ClientId,
      Position = p.Position.ToDateTime() // или ToDateTimeOffset()
    });

    return Ok(result);
  }

  [HttpPost("cameras/{cameraId}/recording/start")]
  public async Task<IActionResult> StartRecording(string cameraId, CancellationToken ct)
  {
    var response = await grpcClient.SetCameraStateAsync(new CameraState
    {
      CameraId = cameraId,
      Recording = true,
      WebRtc = true
    }, cancellationToken: ct);

    return Ok(ToApiResponse(response));
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

    return Ok(ToApiResponse(response));
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

  private static ApiResponse ToApiResponse(OperationStatus status) => new(status.Success, status.Message);
}

public record AddCameraDto(string CameraId,
                           string RtspUrl,
                           CameraStateDto? State);

public record AddPlayerDto(
  string CameraId,
  string ClientId,
  DateTime? StartTime);

public record CameraDto(string CameraId, string RtspUrl);

public record CameraStateDto
{
  required public string CameraId { get; init; }
  public bool Recording { get; init; }
  public bool WebRtc { get; init; }
}

public record ApiResponse(bool Success, string Message);


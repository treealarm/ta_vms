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
  public async Task<IActionResult> AddCamera([FromBody] CameraStateDto request, CancellationToken ct)
  {
    var response = await grpcClient.AddCameraAsync(new CameraState
    {
      CameraId = request.CameraId,
      RtspUrl = request.RtspUrl,
      Recording = request.Recording,
      WebRtc = true

    }, cancellationToken: ct);

    return Ok(ToApiResponse(response));
  }

  [HttpPost("players")]
  public async Task<IActionResult> AddPlayer([FromBody] PlayerStateDto request, CancellationToken ct)
  {
      var dt = request.Position ?? DateTime.UtcNow;

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

  [HttpPost("recordings")]
  public async Task<IActionResult> GetRecordings(
  [FromBody] RecordingsQueryDto query,
  CancellationToken ct)
  {
    var request = new RecordingsRequest
    {
      CameraId = query.CameraId,
      Limit = query.Limit ?? 50
    };

    if (query.Timestamp.HasValue)
    {
      var dt = query.Timestamp.Value;

      DateTime dtUtc =
        dt.Kind == DateTimeKind.Utc
          ? dt
          : dt.Kind == DateTimeKind.Local
            ? dt.ToUniversalTime()
            : DateTime.SpecifyKind(dt, DateTimeKind.Utc);

      request.Timestamp = Timestamp.FromDateTime(dtUtc);
    }

    var grpcResponse = await grpcClient.GetRecordingsAsync(request, cancellationToken: ct);

    var result = new RecordingsResponseDto();

    result.Recordings = grpcResponse.Recordings.Select(r => new RecordingDto
    {
      FilePath = r.FilePath,
      StartTime = r.StartTime.ToDateTime(),
      EndTime = r.EndTime.ToDateTime()
    }).ToList();

    if (grpcResponse.Nearest!= null)
    {
      result.Nearest = new RecordingDto
      {
        FilePath = grpcResponse.Nearest.FilePath,
        StartTime = grpcResponse.Nearest.StartTime.ToDateTime(),
        EndTime = grpcResponse.Nearest.EndTime?.ToDateTime()
      };
    }
      


    return Ok(result);
  }

  private static ApiResponse ToApiResponse(OperationStatus status) => new(status.Success, status.Message);
}

public record ApiResponse(bool Success, string Message);


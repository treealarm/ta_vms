using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Mvc;
using Vms.Control;
using web_vms.Server.Dto;

namespace web_vms;

[ApiController]
[Route("api/[controller]")]
public class ArchController(
  CameraControlService.CameraControlServiceClient grpcClient) : ControllerBase
{

  private static string MapPlayerMode(PlayerMode mode) => mode switch
  {
    PlayerMode.Playing => "playing",
    PlayerMode.Paused => "paused",
    PlayerMode.Stopped => "stopped",
    _ => "unspecified"
  };

  [HttpPost("players")]
  public async Task<IActionResult> AddPlayer([FromBody] PlayerStateDto request, CancellationToken ct)
  {
      var dt = request.position_ts ?? DateTime.UtcNow;

      if (dt.Kind != DateTimeKind.Utc)
        dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);

      var StartTimeStamp = Timestamp.FromDateTime(dt);

      var startPlayResponse = await grpcClient.AddArchPlayerAsync(new PlayerState
      {
        CameraId = request.camera_id,
        PositionTs = StartTimeStamp,
        ClientId = request.client_id
      }, cancellationToken: ct);

      return Ok(ApiResponseMapper.ToApiResponse(startPlayResponse));
  }


  [HttpPost("players/state")]
  public async Task<IActionResult> SetPlayerState([FromBody] PlayerStateDto dto, CancellationToken ct)
  {
    if (dto == null)
      return BadRequest("Request body is missing");

    var request = new PlayerState
    {
      CameraId = dto.camera_id,
      ClientId = dto.client_id
    };

    if (dto.position_ts.HasValue)
    {
      var dt = dto.position_ts.Value;

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
        dtUtc = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
      }

      request.PositionTs = Timestamp.FromDateTime(dtUtc);

      if (dto.position_ms is uint value)
      {
        request.PositionMs = value;
      }
    }

    var grpcResponse = await grpcClient.SetPlayerStateAsync(request, cancellationToken: ct);

    return Ok(new
    {
      success = grpcResponse.Success,
      message = grpcResponse.Message
    });
  }

  [HttpGet("players/state")]
  public async Task<IActionResult> GetPlayerState([FromQuery] string clientId, CancellationToken ct)
  {
    if (string.IsNullOrEmpty(clientId))
      return BadRequest("clientId is required");

    try
    {
      PlayerProgressState playerState =
      await grpcClient.GetPlayerProgressStateAsync(new SourceRequest() { SourceId = clientId }, cancellationToken: ct);

      if (playerState == null)
        return NotFound();

      var dto = new PlayerProgressStateDto
      {
        camera_id = playerState.CameraId,
        client_id = playerState.ClientId,
        file_path = playerState.FilePath,
        mode = MapPlayerMode(playerState.Mode)
      };

      if (playerState.PositionTs != null)
        dto.position_ts = playerState.PositionTs.ToDateTime();

      dto.start_ts = playerState.StartTs.ToDateTime();
      dto.duration_ms = playerState.DurationMs;
      dto.position_ms = playerState.PositionMs;
      return Ok(dto);
    }
    catch (Exception ex)
    {
      Console.Error.WriteLine(ex.ToString());
      return BadRequest(ex.ToString());
    }
  }



  [HttpPost("players/command")]
  public async Task<IActionResult> SendPlayerCommand([FromBody] PlayerCommandDto dto, CancellationToken ct)
  {
    if (dto == null)
      return BadRequest("Request body is missing");

    if (string.IsNullOrWhiteSpace(dto.client_id))
      return BadRequest("client_id is required");

    if (string.IsNullOrWhiteSpace(dto.command))
      return BadRequest("command is required");

    var request = new PlayerCommand
    {
      ClientId = dto.client_id
    };

    switch (dto.command.Trim().ToLowerInvariant())
    {
      case "play":
        request.Play = new Play();
        if (dto.speed.HasValue)
          request.Play.Speed = dto.speed.Value;
        break;
      case "pause":
        request.Pause = new Pause();
        break;
      case "stop":
        request.Stop = new Stop();
        break;
      case "step":
        if (!dto.step_frames.HasValue || dto.step_frames.Value == 0)
          return BadRequest("step_frames is required for step command and must be non-zero");

        request.Step = new Step { StepFrames = dto.step_frames.Value };
        break;
      case "step_forward":
        request.Step = new Step { StepFrames = 1 };
        break;
      case "step_back":
        request.Step = new Step { StepFrames = -1 };
        break;
      default:
        return BadRequest("command must be one of: play, pause, stop, step, step_back, step_forward");
    }

    var grpcResponse = await grpcClient.SendPlayerCommandAsync(request, cancellationToken: ct);

    return Ok(ApiResponseMapper.ToApiResponse(grpcResponse));
  }

  [HttpGet("snapshot")]
  public async Task<IActionResult> GetArchiveSnapshotJpeg(
    [FromQuery] string cameraId,
    [FromQuery] DateTime timestamp,
    CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(cameraId))
      return BadRequest("cameraId is required");

    DateTime timestampUtc;
    if (timestamp.Kind == DateTimeKind.Utc)
    {
      timestampUtc = timestamp;
    }
    else if (timestamp.Kind == DateTimeKind.Local)
    {
      timestampUtc = timestamp.ToUniversalTime();
    }
    else
    {
      timestampUtc = DateTime.SpecifyKind(timestamp, DateTimeKind.Utc);
    }

    try
    {
      var grpcResponse = await grpcClient.GetArchiveFrameJpegAsync(new ArchiveFrameJpegRequest
      {
        CameraId = cameraId,
        Timestamp = Timestamp.FromDateTime(timestampUtc)
      }, cancellationToken: ct);

      if (!grpcResponse.Success)
        return NotFound(new { success = false, message = grpcResponse.Message });

      if (grpcResponse.Jpeg == null || grpcResponse.Jpeg.Length == 0)
        return NotFound(new { success = false, message = "jpeg frame is empty" });

      var mimeType = string.IsNullOrWhiteSpace(grpcResponse.MimeType)
        ? "image/jpeg"
        : grpcResponse.MimeType;

      return File(grpcResponse.Jpeg.ToByteArray(), mimeType);
    }
    catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
    {
      return NotFound(new { success = false, message = ex.Status.Detail });
    }
  }

  [HttpDelete("players/{clientId}")]
  public async Task<IActionResult> RemovePlayer(string clientId, CancellationToken ct)
  {
    var response = await grpcClient.RemoveArchPlayerAsync(new SourceRequest
    {
      SourceId = clientId
    }, cancellationToken: ct);

    return Ok(ApiResponseMapper.ToApiResponse(response));
  }

  [HttpGet("players")]
  public async Task<IActionResult> GetPlayers(CancellationToken ct)
  {
    var grpcResponse = await grpcClient.ListPlayersAsync(
        new ListPlayersRequest(),
        cancellationToken: ct);

    var result = grpcResponse.Players.Select(p => new PlayerProgressStateDto
    {
      camera_id = p.CameraId,
      client_id = p.ClientId,
      position_ts = p.PositionTs.ToDateTime(),
      mode = MapPlayerMode(p.Mode)
    });

    return Ok(result);
  }
}

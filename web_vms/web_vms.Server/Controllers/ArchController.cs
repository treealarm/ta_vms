using Google.Protobuf.WellKnownTypes;
using Dapr.Client;
using Grpc.Core;
using Microsoft.AspNetCore.Mvc;
using Vms.Control;
using web_vms.Types.Dto;

namespace web_vms;

[ApiController]
[Route("api/[controller]")]
public class ArchController(
  ICameraManager manager) : ControllerBase
{
  private static CameraControlService.CameraControlServiceClient GetMediaClient(string serverId) =>
    new(DaprClient.CreateInvocationInvoker(serverId));

  private static string MapPlayerMode(PlayerMode mode) => mode switch
  {
    PlayerMode.Playing => "playing",
    PlayerMode.Paused => "paused",
    PlayerMode.Stopped => "stopped",
    _ => "unspecified"
  };

  private static PlayerProgressStateDto ToDto(PlayerProgressState playerState) => new()
  {
    camera_id = playerState.CameraId,
    client_id = playerState.ClientId,
    file_path = playerState.FilePath,
    mode = MapPlayerMode(playerState.Mode),
    position_ts = playerState.PositionTs?.ToDateTime(),
    start_ts = playerState.StartTs?.ToDateTime(),
    duration_ms = playerState.DurationMs,
    position_ms = playerState.PositionMs
  };

  private async Task<string?> GetServerIdForCamera(string cameraId)
  {
    var camera = await manager.Get(cameraId);
    return camera?.ServerId;
  }

  [HttpPost("players")]
  public async Task<IActionResult> AddPlayer([FromBody] PlayerStateDto request, CancellationToken ct)
  {
      var serverId = await GetServerIdForCamera(request.camera_id);
      if (string.IsNullOrWhiteSpace(serverId))
        return NotFound();

      var dt = request.position_ts ?? DateTime.UtcNow;

      if (dt.Kind != DateTimeKind.Utc)
        dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);

      var StartTimeStamp = Timestamp.FromDateTime(dt);

      var startPlayResponse = await GetMediaClient(serverId).AddArchPlayerAsync(new PlayerState
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

    var serverId = await GetServerIdForCamera(dto.camera_id);
    if (string.IsNullOrWhiteSpace(serverId))
      return NotFound();

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

    var grpcResponse = await GetMediaClient(serverId).SetPlayerStateAsync(request, cancellationToken: ct);

    return Ok(new
    {
      success = grpcResponse.Success,
      message = grpcResponse.Message
    });
  }

  [HttpGet("players/state")]
  public async Task<IActionResult> GetPlayerState(
    [FromQuery] string clientId,
    [FromQuery] string cameraId,
    CancellationToken ct)
  {
    if (string.IsNullOrEmpty(clientId))
      return BadRequest("clientId is required");

    if (string.IsNullOrWhiteSpace(cameraId))
      return BadRequest("cameraId is required");

    var serverId = await GetServerIdForCamera(cameraId);
    if (string.IsNullOrWhiteSpace(serverId))
      return NotFound();

    try
    {
      PlayerProgressState playerState =
      await GetMediaClient(serverId).GetPlayerProgressStateAsync(new SourceRequest() { SourceId = clientId }, cancellationToken: ct);

      if (playerState == null)
        return NotFound();

      return Ok(ToDto(playerState));
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

    if (string.IsNullOrWhiteSpace(dto.camera_id))
      return BadRequest("camera_id is required");

    if (string.IsNullOrWhiteSpace(dto.command))
      return BadRequest("command is required");

    var serverId = await GetServerIdForCamera(dto.camera_id);
    if (string.IsNullOrWhiteSpace(serverId))
      return NotFound();

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

    var grpcResponse = await GetMediaClient(serverId).SendPlayerCommandAsync(request, cancellationToken: ct);

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

    var serverId = await GetServerIdForCamera(cameraId);
    if (string.IsNullOrWhiteSpace(serverId))
      return NotFound();

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
      var grpcResponse = await GetMediaClient(serverId).GetArchiveFrameJpegAsync(new ArchiveFrameJpegRequest
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
    catch (RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.NotFound)
    {
      return NotFound(new { success = false, message = ex.Status.Detail });
    }
  }

  [HttpDelete("players/{clientId}")]
  public async Task<IActionResult> RemovePlayer(
    string clientId,
    [FromQuery] string cameraId,
    CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(cameraId))
      return BadRequest("cameraId is required");

    var serverId = await GetServerIdForCamera(cameraId);
    if (string.IsNullOrWhiteSpace(serverId))
      return NotFound();

    var response = await GetMediaClient(serverId).RemoveArchPlayerAsync(new SourceRequest
    {
      SourceId = clientId
    }, cancellationToken: ct);

    return Ok(ApiResponseMapper.ToApiResponse(response));
  }

  [HttpGet("players")]
  public async Task<IActionResult> GetPlayers(CancellationToken ct)
  {
    var result = new List<PlayerProgressStateDto>();
    var servers = await manager.ListServers();
    foreach (var server in servers)
    {
      var grpcResponse = await GetMediaClient(server.ServerId).ListPlayersAsync(
          new ListPlayersRequest(),
          cancellationToken: ct);
      result.AddRange(grpcResponse.Players.Select(ToDto));
    }

    return Ok(result);
  }
}

using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Mvc;
using Vms.Control;

namespace web_vms;

[ApiController]
[Route("api/[controller]")]
public class ArchController(
  CameraControlService.CameraControlServiceClient grpcClient) : ControllerBase
{
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

      return Ok(ApiResponseMapper.ToApiResponse(startPlayResponse));
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

  [HttpGet("players/state")]
  public async Task<IActionResult> GetPlayerState([FromQuery] string clientId, CancellationToken ct)
  {
    if (string.IsNullOrEmpty(clientId))
      return BadRequest("clientId is required");

    PlayerState playerState =
      await grpcClient.GetPlayerStateAsync(new SourceRequest() { SourceId = clientId }, cancellationToken: ct);

    if (playerState == null)
      return NotFound();

    var dto = new PlayerStateDto
    {
      CameraId = playerState.CameraId,
      ClientId = playerState.ClientId,
      FilePath = playerState.FilePath
    };

    if (playerState.Position != null)
      dto.Position = playerState.Position.ToDateTime();

    return Ok(dto);
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

    var result = grpcResponse.Players.Select(p => new PlayerStateDto
    {
      CameraId = p.CameraId,
      ClientId = p.ClientId,
      Position = p.Position.ToDateTime()
    });

    return Ok(result);
  }
}

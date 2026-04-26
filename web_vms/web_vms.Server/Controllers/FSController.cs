using Google.Protobuf.WellKnownTypes;
using Dapr.Client;
using Microsoft.AspNetCore.Mvc;
using Vms.Control;
using web_vms.Types.Dto;

namespace web_vms.Server.Controllers
{
  [Route("api/[controller]")]
  [ApiController]
  public class FSController(
    ICameraManager manager) : ControllerBase
  {
    private static CameraControlService.CameraControlServiceClient GetMediaClient(string serverId) =>
      new(DaprClient.CreateInvocationInvoker(serverId));

    [HttpPost("recordings")]
    public async Task<IActionResult> GetRecordings(
      [FromBody] RecordingsQueryDto query,
      CancellationToken ct)
    {
      var camera = await manager.Get(query.CameraId);
      if (string.IsNullOrWhiteSpace(camera?.ServerId))
      {
        return NotFound();
      }

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

      var grpcResponse = await GetMediaClient(camera.ServerId).GetRecordingsAsync(request, cancellationToken: ct);

      var result = new RecordingsResponseDto();

      result.Recordings = grpcResponse.Recordings.Select(r => new RecordingDto
      {
        FilePath = r.FilePath,
        StartTime = r.StartTime.ToDateTime()
      }).ToList();

      if (grpcResponse.Nearest != null)
      {
        result.Nearest = new RecordingDto
        {
          FilePath = grpcResponse.Nearest.FilePath,
          StartTime = grpcResponse.Nearest.StartTime.ToDateTime()
        };
      }



      return Ok(result);
    }
  }
}

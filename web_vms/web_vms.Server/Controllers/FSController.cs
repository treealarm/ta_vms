using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Mvc;
using Vms.Control;

namespace web_vms.Server.Controllers
{
  [Route("api/[controller]")]
  [ApiController]
  public class FSController(
  CameraControlService.CameraControlServiceClient grpcClient) : ControllerBase
  {
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
        EndTime = r.EndTime?.ToDateTime()
      }).ToList();

      if (grpcResponse.Nearest != null)
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
  }
}

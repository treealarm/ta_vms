using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Mvc;
using web_vms.Types.Dto;
using Vms.Logger;

namespace web_vms
{
  [Route("api/[controller]")]
  [ApiController]
  public class LoggerController(LogService.LogServiceClient grpcClient) : ControllerBase
  {
    [HttpPost("logs")]
    public async Task<IActionResult> GetLogs([FromBody] LogQueryDto query, CancellationToken ct)
    {
      var request = new LogRequest
      {
        Limit = query.Limit ?? 100
      };

      if (query.From.HasValue)
      {
        request.From = Timestamp.FromDateTime(ToUtc(query.From.Value));
      }

      if (query.To.HasValue)
      {
        request.To = Timestamp.FromDateTime(ToUtc(query.To.Value));
      }

      if (!string.IsNullOrWhiteSpace(query.Level))
      {
        request.Level = query.Level;
      }

      if (!string.IsNullOrWhiteSpace(query.Source))
      {
        request.Source = query.Source;
      }

      var grpcResponse = await grpcClient.GetLogsAsync(request, cancellationToken: ct);

      var response = new LogResponseDto
      {
        HasMore = grpcResponse.HasMore,
        Logs = grpcResponse.Logs.Select(l => new LogEntryDto
        {
          Timestamp = l.Timestamp?.ToDateTime() ?? DateTime.UnixEpoch,
          Level = l.Level,
          File = l.File,
          Function = l.Function,
          Message = l.Message
        }).ToList()
      };

      return Ok(response);
    }

    private static DateTime ToUtc(DateTime value)
    {
      return value.Kind switch
      {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
      };
    }
  }
}

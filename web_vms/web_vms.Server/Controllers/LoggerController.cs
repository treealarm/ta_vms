using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Mvc;
using web_vms.Types.Dto;
using Vms.Logger;
using web_vms.Authorization;

namespace web_vms
{
  /// <summary>
  /// Reads service logs. Every VmsLogger is asked, not just the one next to this gateway: logs are
  /// files on local disk, so an instance can only see the machine it runs on, and on a multi-node
  /// deployment a single instance would silently answer for one machine while the operator believes
  /// they are looking at the system.
  /// </summary>
  [Route("api/[controller]")]
  [ApiController]
  public class LoggerController(
    ServerPairRegistry registry,
    ILogger<LoggerController> logger) : ControllerBase
  {
    private static readonly TimeSpan FanOutTimeout = TimeSpan.FromSeconds(5);

    private static readonly string CentralLoggerId =
      Environment.GetEnvironmentVariable("DAPR_LOGGER_APP_ID")
        ?? throw new InvalidOperationException("Required environment variable 'DAPR_LOGGER_APP_ID' is not set");

    [NoCameraAccess("service log sources, not camera video")]
    [HttpGet("sources")]
    public async Task<IActionResult> GetSources(CancellationToken ct)
    {
      // App ids are unique across the deployment (each node's services carry its suffix), so the
      // union needs no disambiguation — it is simply every source that exists anywhere.
      var perInstance = await FanOutAsync(
        (client, options) => client.ListLogSourcesAsync(new Empty(), options).ResponseAsync,
        ct);

      var sources = perInstance
        .SelectMany(r => r.AppIds)
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToList();

      return Ok(sources);
    }

    [NoCameraAccess("service logs, not camera video")]
    [HttpPost("logs")]
    public async Task<IActionResult> GetLogs([FromBody] LogQueryDto query, CancellationToken ct)
    {
      var limit = query.Limit ?? 100;
      var request = new LogRequest { Limit = limit };

      if (query.From.HasValue)
        request.From = Timestamp.FromDateTime(ToUtc(query.From.Value));

      if (query.To.HasValue)
        request.To = Timestamp.FromDateTime(ToUtc(query.To.Value));

      if (!string.IsNullOrWhiteSpace(query.Level))
        request.Level = query.Level;

      if (!string.IsNullOrWhiteSpace(query.Source))
        request.Source = query.Source;

      if (!string.IsNullOrWhiteSpace(query.AppId))
        request.AppId = query.AppId;

      var perInstance = await FanOutAsync(
        (client, options) => client.GetLogsAsync(request, options).ResponseAsync, ct);

      // Each instance returns its own newest `limit` entries, oldest first. The newest `limit` of
      // the union is therefore found by sorting the lot descending and cutting — taking the first
      // `limit` of a naive concatenation would favour whichever instance answered first.
      var merged = perInstance
        .SelectMany(r => r.Logs)
        .OrderByDescending(l => l.Timestamp?.ToDateTime() ?? DateTime.MinValue)
        .Take((int)limit)
        .OrderBy(l => l.Timestamp?.ToDateTime() ?? DateTime.MinValue)
        .ToList();

      var response = new LogResponseDto
      {
        // True when any instance had more, and also when the merge itself dropped entries — from
        // the caller's side both mean "there is older material behind this page".
        HasMore = perInstance.Any(r => r.HasMore)
          || perInstance.Sum(r => r.Logs.Count) > merged.Count,
        Logs = merged.Select(l => new LogEntryDto
        {
          Timestamp = l.Timestamp?.ToDateTime() ?? DateTime.UnixEpoch,
          Level = l.Level,
          AppId = l.AppId,
          File = l.File,
          Function = l.Function,
          Message = l.Message
        }).ToList()
      };

      return Ok(response);
    }

    /// Asks every logger in parallel and keeps whatever came back in time. An unreachable node
    /// leaves a hole in the answer rather than failing the request: a partial log view is more
    /// useful than none, and the node being down is usually the very thing being investigated.
    private async Task<IReadOnlyList<T>> FanOutAsync<T>(
      Func<LogService.LogServiceClient, CallOptions, Task<T>> call, CancellationToken ct)
    {
      var options = new CallOptions(deadline: DateTime.UtcNow.Add(FanOutTimeout), cancellationToken: ct);

      var tasks = registry.AllLoggerIds(CentralLoggerId).Select(async loggerId =>
      {
        try
        {
          return await call(registry.LogClientFor(loggerId), options);
        }
        catch (RpcException ex)
        {
          logger.LogWarning(ex, "Logs unavailable from '{LoggerId}'", loggerId);
          return default;
        }
      });

      return (await Task.WhenAll(tasks)).Where(r => r is not null).Select(r => r!).ToList();
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

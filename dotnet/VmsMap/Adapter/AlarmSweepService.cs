using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace VmsMap.Adapter
{
  // Once every three seconds, take ALARM away from the cameras whose episode has ended. The list
  // of running alarms lives in the database, not in a replica's memory: a message delivered to one
  // replica and lost on its restart would otherwise never be cleared. The sweep is idempotent, so
  // every replica runs it.
  internal sealed class AlarmSweepService : BackgroundService
  {
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<AlarmSweepService> _logger;

    public AlarmSweepService(
      IServiceScopeFactory scopes, ILogger<AlarmSweepService> logger)
    {
      _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
      _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
      while (!stoppingToken.IsCancellationRequested)
      {
        try
        {
          using var scope = _scopes.CreateScope();
          var alarms = scope.ServiceProvider.GetRequiredService<AlarmService>();
          await alarms.SweepAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
          return;
        }
        catch (Exception ex)
        {
          _logger.LogWarning(ex, "Alarm sweep failed");
        }

        try
        {
          await Task.Delay(Interval, stoppingToken);
        }
        catch (OperationCanceledException)
        {
          return;
        }
      }
    }
  }
}

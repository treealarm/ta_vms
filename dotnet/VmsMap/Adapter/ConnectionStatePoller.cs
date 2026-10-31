using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VmsMap.Clients;

namespace VmsMap.Adapter
{
  // There is no "camera is in the network" topic: video_srv publishes only debug state, and
  // GetCamState knows armed. The source of CONNECTED is the media server's path table, asked once
  // for every camera of the deployment per cycle. One call, not one per camera.
  internal sealed class ConnectionStatePoller : BackgroundService
  {
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopes;
    private readonly ICameraCatalog _catalog;
    private readonly ICameraConnectionSource _connections;
    private readonly ILogger<ConnectionStatePoller> _logger;

    public ConnectionStatePoller(
      IServiceScopeFactory scopes,
      ICameraCatalog catalog,
      ICameraConnectionSource connections,
      ILogger<ConnectionStatePoller> logger)
    {
      _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
      _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
      _connections = connections ?? throw new ArgumentNullException(nameof(connections));
      _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
      while (!stoppingToken.IsCancellationRequested)
      {
        try
        {
          await PollOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
          return;
        }
        catch (Exception ex)
        {
          _logger.LogWarning(ex, "Connectivity poll failed");
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

    internal async Task PollOnceAsync(CancellationToken ct)
    {
      var cameras = await _catalog.ListAsync(null, ct);
      if (cameras.Count == 0)
        return;

      var connected = await _connections.GetConnectedAsync(cameras, ct);

      using var scope = _scopes.CreateScope();
      var service = scope.ServiceProvider.GetRequiredService<ConnectionStateService>();
      await service.ApplyAsync(connected, ct);
      await service.ReconcileAsync(cameras, ct);
    }
  }
}

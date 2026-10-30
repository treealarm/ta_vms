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
  // The full camera set once at startup, retried until VmsCfg answers. Every replica does it: the
  // work is idempotent, and a map that started while VmsCfg was down must not stay empty until
  // somebody happens to edit a camera.
  internal sealed class StartupResyncService : BackgroundService
  {
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(10);

    private readonly IServiceScopeFactory _scopes;
    private readonly ICameraCatalog _catalog;
    private readonly ILogger<StartupResyncService> _logger;

    public StartupResyncService(
      IServiceScopeFactory scopes,
      ICameraCatalog catalog,
      ILogger<StartupResyncService> logger)
    {
      _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
      _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
      _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
      while (!stoppingToken.IsCancellationRequested)
      {
        try
        {
          using var scope = _scopes.CreateScope();
          var nodes = scope.ServiceProvider.GetRequiredService<CameraNodeService>();
          var cameras = await _catalog.ListAsync(null, stoppingToken);

          foreach (var camera in cameras)
            await nodes.EnsureCameraAsync(camera, stoppingToken);

          // A camera deleted while this replica was down has no event left to deliver.
          await nodes.RemoveUnknownAsync(
            cameras.Select(c => c.CameraId).ToHashSet(StringComparer.Ordinal), stoppingToken);

          _logger.LogInformation(
            "Map camera resync completed: {Count} camera(s)", cameras.Count);
          return;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
          return;
        }
        catch (Exception ex)
        {
          _logger.LogWarning(ex,
            "Map camera resync failed, retrying in {Seconds}s", RetryInterval.TotalSeconds);
        }

        try
        {
          await Task.Delay(RetryInterval, stoppingToken);
        }
        catch (OperationCanceledException)
        {
          return;
        }
      }
    }
  }
}

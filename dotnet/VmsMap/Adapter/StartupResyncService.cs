using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VmsMap.Clients;
using VmsMap.Domain.Arming;

namespace VmsMap.Adapter
{
  // The full camera set once at startup, retried until VmsCfg answers. Every replica does it: the
  // work is idempotent, and a map that started while VmsCfg was down must not stay empty until
  // somebody happens to edit a camera.
  //
  // The set is walked in batches with bounded parallelism: at a hundred thousand cameras, creating
  // one node at a time is minutes of startup, and one camera that fails must not take the rest of
  // the sweep with it. A failure is logged and left for the next reconcile -- the camera's id stays
  // in `known`, so the sweep does not mistake a failure for a deletion.
  internal sealed class StartupResyncService : BackgroundService
  {
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(10);

    /// Cameras per wave: enough to keep the parallelism busy without holding every camera's
    /// `CameraInfo` AND its node work in flight at once.
    private const int BatchSize = 500;

    /// How many cameras are reconciled at once. Each takes a database context of its own from a
    /// scope; four keeps the transaction and connection churn modest.
    private const int Parallelism = 4;

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
          var cameras = await _catalog.ListAsync(null, stoppingToken);

          for (var offset = 0; offset < cameras.Count; offset += BatchSize)
          {
            var batch = cameras.GetRange(offset, Math.Min(BatchSize, cameras.Count - offset));
            await Parallel.ForEachAsync(
              batch,
              new ParallelOptions
              {
                MaxDegreeOfParallelism = Parallelism,
                CancellationToken = stoppingToken,
              },
              async (camera, ct) =>
              {
                await using var scope = _scopes.CreateAsyncScope();
                var nodes = scope.ServiceProvider.GetRequiredService<DeviceNodeService>();
                try
                {
                  await nodes.EnsureCameraAsync(camera, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                  _logger.LogWarning(ex,
                    "Map resync failed for camera '{CameraId}'; it stays in the tree as it was",
                    camera.CameraId);
                }
              });
          }

          // A camera deleted while this replica was down has no event left to deliver.
          await using (var scope = _scopes.CreateAsyncScope())
          {
            var nodes = scope.ServiceProvider.GetRequiredService<DeviceNodeService>();
            await nodes.RemoveUnknownAsync(
              cameras.Select(c => c.CameraId).ToHashSet(StringComparer.Ordinal), stoppingToken);
          }

          // Once, before any flag is derived: the modes and alarm rules still living in VmsCfg's
          // columns move into the map, so an explicitly armed camera is not mistaken for one that
          // inherits its zone. A failure leaves the marker unset and the next start retries.
          await using (var scope = _scopes.CreateAsyncScope())
          {
            try
            {
              var importer = scope.ServiceProvider.GetRequiredService<LegacyArmingImporter>();
              await importer.EnsureImportedAsync(cameras, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
              return;
            }
            catch (Exception ex)
            {
              _logger.LogWarning(ex, "Importing the arming settings from VmsCfg failed");
            }
          }

          // A zone that was armed while this replica was down is written into the flags of the
          // cameras standing in it. Bounded by the zones that are set, not by the cameras.
          await using (var scope = _scopes.CreateAsyncScope())
          {
            try
            {
              var arming = scope.ServiceProvider.GetRequiredService<CameraArmingReconciler>();
              await arming.SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
              return;
            }
            catch (Exception ex)
            {
              // The cameras are in the tree either way; the next trigger writes the flags.
              _logger.LogWarning(ex, "Deriving the arming of the cameras in armed zones failed");
            }
          }

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

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace VmsMap.Demo
{
  /// Installs the world named by `DEMO_CONFIG` at startup. Empty means off; the install is the
  /// same idempotent one the administrator's button calls, so a restart after a clean install
  /// changes nothing. A flag naming a world this build does not have is an error worth seeing in
  /// the log rather than a world silently missing.
  public sealed class DemoInstallService(IServiceScopeFactory scopes, ILogger<DemoInstallService> log)
    : BackgroundService
  {
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
      var key = Environment.GetEnvironmentVariable("DEMO_CONFIG");
      if (string.IsNullOrWhiteSpace(key))
        return;

      var world = DemoWorlds.For(key);
      if (world is null)
      {
        log.LogError("DEMO_CONFIG names '{Key}', which is not a demo world this service knows", key);
        return;
      }

      try
      {
        await using var scope = scopes.CreateAsyncScope();
        var seeder = scope.ServiceProvider.GetRequiredService<DemoSeeder>();

        // A placement is checked against VmsCfg, which starts beside this service and may not be
        // answering yet. The install rolls back on that failure, so awaiting it a few times is
        // waiting for a neighbour, not retrying a half-written world. The same neighbour is what
        // provisions the demo's cameras, so a world installed before they exist has no placements
        // and no calibration: keep trying for a minute rather than leaving the demo half-built.
        const int attempts = 12;
        for (var attempt = 1; attempt <= attempts; attempt += 1)
        {
          try
          {
            var report = await seeder.InstallAsync(world, stoppingToken);

            log.LogInformation(
              "Demo world '{Key}' installed: {Created} created, {Updated} updated, {Removed} removed",
              report.Key, report.Created, report.Updated, report.Removed);

            if (report.SkippedCameras.Count > 0 && attempt < attempts
                && !stoppingToken.IsCancellationRequested)
            {
              log.LogInformation(
                "The demo world has no placement yet for {Count} camera(s): {Cameras}; waiting for " +
                "VmsCfg to provision them",
                report.SkippedCameras.Count, string.Join(", ", report.SkippedCameras));
              await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
              continue;
            }

            if (report.SkippedCameras.Count > 0)
            {
              log.LogWarning(
                "The demo world has no placement for {Count} camera(s) the deployment lacks: {Cameras}. " +
                "Install the demo again once they exist.",
                report.SkippedCameras.Count, string.Join(", ", report.SkippedCameras));
            }

            return;
          }
          catch (Exception) when (attempt < 5 && !stoppingToken.IsCancellationRequested)
          {
            log.LogWarning("The demo world '{Key}' is not installable yet; trying again", key);
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
          }
        }
      }
      catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
      {
        // Shutting down while the world was being written rolls it back; the next start does it.
      }
      catch (Exception ex)
      {
        log.LogError(ex, "The demo world '{Key}' could not be installed", key);
      }
    }
  }
}

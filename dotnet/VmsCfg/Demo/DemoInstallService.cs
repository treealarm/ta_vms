using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace VmsCfg.Demo;

/// Installs the demo's video wall at startup when `DEMO_CONFIG` names a world. The map world is
/// VmsMap's and is installed by it; this is the camera side of the same flag, so a standalone
/// install with the flag gets both without an administrator clicking anything.
public sealed class DemoInstallService(IServiceScopeFactory scopes, ILogger<DemoInstallService> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var key = Environment.GetEnvironmentVariable("DEMO_CONFIG");
        if (string.IsNullOrWhiteSpace(key))
            return;

        if (!string.Equals(key, DemoMonitor.DemoKey, StringComparison.Ordinal))
        {
            // VmsMap owns the list of worlds; this service only knows the one it can show.
            log.LogError("DEMO_CONFIG names '{Key}', which is not a demo world VmsCfg knows", key);
            return;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var plan = await scope.ServiceProvider.GetRequiredService<DemoMonitor>()
                .InstallAsync(stoppingToken);

            log.LogInformation(
                "Demo wall '{Name}' installed with {Placed} camera(s); {Skipped} not provisioned yet",
                plan.Name, plan.Cameras.Count, plan.Skipped.Count);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down; the next start installs the wall.
        }
        catch (Exception ex)
        {
            log.LogError(ex, "The demo wall could not be installed");
        }
    }
}

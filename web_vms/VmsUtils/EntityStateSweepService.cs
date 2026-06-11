namespace VmsUtils;

// Periodically removes debug-entity-state entries that haven't been refreshed
// for a long time, so the registry doesn't grow unbounded across restarts of producers.
public sealed class EntityStateSweepService(EntityStateRegistry registry) : BackgroundService
{
  private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    while (!stoppingToken.IsCancellationRequested)
    {
      registry.Sweep();
      await Task.Delay(SweepInterval, stoppingToken);
    }
  }
}

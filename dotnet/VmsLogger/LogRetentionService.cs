namespace VmsLogger;

// Deletes rotated log files older than RetentionPeriod across all services'
// VMS_LOG_PATH/<app_id>/ subfolders. Complements the size-based rotation each
// service's own logger (Serilog / spdlog) already performs.
public sealed class LogRetentionService(ILogger<LogRetentionService> logger) : BackgroundService
{
  private static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(2);
  private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    while (!stoppingToken.IsCancellationRequested)
    {
      Sweep();
      await Task.Delay(SweepInterval, stoppingToken);
    }
  }

  private void Sweep()
  {
    var logPath = Environment.GetEnvironmentVariable("VMS_LOG_PATH")
      ?? throw new InvalidOperationException("Required environment variable 'VMS_LOG_PATH' is not set");

    if (!Directory.Exists(logPath))
      return;

    var cutoff = DateTime.UtcNow - RetentionPeriod;
    foreach (var file in Directory.EnumerateFiles(logPath, "*", SearchOption.AllDirectories))
    {
      try
      {
        if (File.GetLastWriteTimeUtc(file) < cutoff)
        {
          File.Delete(file);
          logger.LogInformation("Deleted expired log file '{File}'", file);
        }
      }
      catch (IOException)
      {
        // File may be the active log file of a service that just wrote to it
        // (race with rotation/locking) — skip and retry on the next sweep.
      }
    }
  }
}

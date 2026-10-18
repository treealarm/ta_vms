namespace VmsFs;

/// <summary>
/// Deletes camera-side import exports once they age out.
/// </summary>
/// <remarks>
/// Exports are downloads, not archive: nobody owns them after the operator has fetched the file, so
/// they expire on wall-clock age rather than through retention rules. ClipCleaner cannot do this —
/// exports are deliberately absent from the clips table.
/// </remarks>
public sealed class EdgeExportCleaner(
  ExportFileStore exportStore,
  ILogger<EdgeExportCleaner> logger) : IHostedService, IAsyncDisposable
{
  private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

  private static readonly TimeSpan Ttl = TimeSpan.FromHours(
    int.TryParse(Environment.GetEnvironmentVariable("VMS_EDGE_EXPORT_TTL_HOURS"), out var hours)
      ? hours
      : throw new InvalidOperationException(
          "Required environment variable 'VMS_EDGE_EXPORT_TTL_HOURS' is not set or is not an integer"));

  private readonly CancellationTokenSource _cts = new();
  private Task? _task;

  public Task StartAsync(CancellationToken cancellationToken)
  {
    _task = RunAsync(_cts.Token);
    return Task.CompletedTask;
  }

  private async Task RunAsync(CancellationToken ct)
  {
    try
    {
      while (!ct.IsCancellationRequested)
      {
        await Task.Delay(Interval, ct);
        var removed = exportStore.DeleteOlderThan(DateTime.UtcNow - Ttl);
        if (removed > 0)
          logger.LogInformation("Removed {Count} expired camera-side export(s)", removed);
      }
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
      logger.LogError(ex, "EdgeExportCleaner fatal error");
    }
  }

  public async Task StopAsync(CancellationToken cancellationToken)
  {
    await _cts.CancelAsync();
    if (_task != null)
    {
      try { await _task; } catch (OperationCanceledException) { }
    }
  }

  public async ValueTask DisposeAsync()
  {
    await StopAsync(CancellationToken.None);
    _cts.Dispose();
  }
}

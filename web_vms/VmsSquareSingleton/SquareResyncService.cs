using SquareIntegrationClient;
using Vms.Cfg;

namespace VmsSquareSingleton;

// Retries the full Square resync (root object + every camera) on a short interval until it
// succeeds once. After that it stops — ongoing updates flow through CameraChangeSubscriber and
// SquareAlarmSubscriber, so there is no need to keep polling once the initial sync has landed.
// Only registered when Square integration is enabled (see Program.cs).
public sealed class SquareResyncService(
  IServiceProvider services,
  SquareProducerId producerId,
  SquareLinkState linkState,
  ILogger<SquareResyncService> logger) : IHostedService, IDisposable
{
  // "Not less often than every 30s" — retrying well inside that bound for faster convergence.
  private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(10);
  private readonly CancellationTokenSource _cts = new();
  private Task? _loopTask;

  public Task StartAsync(CancellationToken cancellationToken)
  {
    _loopTask = RunAsync(_cts.Token);
    return Task.CompletedTask;
  }

  private async Task RunAsync(CancellationToken ct)
  {
    while (!ct.IsCancellationRequested)
    {
      using var scope = services.CreateScope();
      var square = scope.ServiceProvider.GetRequiredService<ISquareIntegration>();
      var cfgClient = scope.ServiceProvider.GetRequiredService<CfgService.CfgServiceClient>();

      try
      {
        await SquareCameraPush.PushAllCamerasAsync(square, cfgClient, producerId.Value, logger, ct);
        // The whole tree is in Square now, which is what makes a link to one of its cameras
        // meaningful — see SquareStatusService.
        linkState.MarkSynced();
        logger.LogInformation("Square resync completed");
        return;
      }
      catch (Exception ex)
      {
        linkState.RecordFailure(ex);
        logger.LogWarning(ex, "Square resync failed, retrying in {Seconds}s", RetryInterval.TotalSeconds);
      }

      try
      {
        await Task.Delay(RetryInterval, ct);
      }
      catch (OperationCanceledException)
      {
        return;
      }
    }
  }

  public Task StopAsync(CancellationToken cancellationToken)
  {
    _cts.Cancel();
    return Task.CompletedTask;
  }

  public void Dispose() => _cts.Dispose();
}

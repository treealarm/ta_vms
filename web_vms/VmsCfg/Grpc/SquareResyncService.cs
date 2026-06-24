using SquareIntegrationClient;

namespace VmsCfg;

// Retries the full Square resync (root object + every camera) on a short interval until it
// succeeds once. After that it stops — ongoing updates flow through the existing per-change
// pushes (CfgServiceImpl.PushCameraToSquare/PushCameraStateToSquare, SquareAlarmSubscriber), so
// there is no need to keep polling once the initial sync has landed. Only registered when Square
// integration is enabled (see Program.cs).
public sealed class SquareResyncService(
  IServiceProvider services,
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
    var appId = Environment.GetEnvironmentVariable("APP_ID") ?? "vmscfg";

    while (!ct.IsCancellationRequested)
    {
      using var scope = services.CreateScope();
      var square = scope.ServiceProvider.GetRequiredService<ISquareIntegration>();
      var storage = scope.ServiceProvider.GetRequiredService<CameraStorage>();

      try
      {
        await SquareCameraPush.PushAllCamerasAsync(square, storage, appId, logger, ct);
        logger.LogInformation("Square resync completed");
        return;
      }
      catch (Exception ex)
      {
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

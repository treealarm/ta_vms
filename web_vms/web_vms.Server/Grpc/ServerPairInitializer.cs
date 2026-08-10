namespace web_vms;

/// <summary>
/// Reads the server↔FS pairing from VmsCfg in the background, retrying until it answers, then keeps
/// re-reading it so a media server added or re-paired later is picked up without a restart. VmsCfg
/// announces such changes on <c>server-config-changed</c>, but the gateway may run in several
/// replicas and each event would reach only one of them, so it polls instead — the map is a handful
/// of rows and rarely changes.
///
/// Deliberately not awaited during startup: doing that kept Kestrel from listening at all while
/// VmsCfg was down, so the browser only ever saw a bare 502 from the dev proxy and the real cause
/// (VmsCfg unreachable, its database missing) lived exclusively in another service's console. The
/// gateway now serves immediately, reports the dependency through <c>GET /api/health</c>, and fails
/// the requests that genuinely need the pairing with 503 plus the reason.
/// </summary>
public sealed class ServerPairInitializer(
  ServerPairRegistry registry,
  ILogger<ServerPairInitializer> logger) : IHostedService, IDisposable
{
  private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
  private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

  private readonly CancellationTokenSource _cts = new();

  public Task StartAsync(CancellationToken cancellationToken)
  {
    _ = RunAsync(_cts.Token);
    return Task.CompletedTask;
  }

  private async Task RunAsync(CancellationToken ct)
  {
    var attempt = 0;
    var loaded = false;

    while (!ct.IsCancellationRequested)
    {
      try
      {
        await registry.InitAsync(ct);
        // Only the first load is worth a line; after that this runs every RefreshInterval and the
        // map almost never changes. A failed refresh keeps the previous map (InitAsync throws
        // before assigning), so the gateway degrades to staleness, never to "not ready".
        if (!loaded)
        {
          logger.LogInformation("Server/FS pairing loaded from VmsCfg");
          loaded = true;
        }
        attempt = 0;
      }
      catch (OperationCanceledException)
      {
        return;
      }
      catch (Exception ex)
      {
        attempt++;
        // Loud once, quiet afterwards: this retries forever and a healthy startup race resolves in
        // a couple of attempts, but a genuine outage should not bury the log.
        if (attempt is 1 or 12 || attempt % 60 == 0)
          logger.LogWarning(ex, "Loading server/FS pairing from VmsCfg failed (attempt {Attempt})", attempt);
      }

      try { await Task.Delay(loaded ? RefreshInterval : RetryDelay, ct); }
      catch (OperationCanceledException) { return; }
    }
  }

  public Task StopAsync(CancellationToken cancellationToken)
  {
    _cts.Cancel();
    return Task.CompletedTask;
  }

  public void Dispose() => _cts.Dispose();
}

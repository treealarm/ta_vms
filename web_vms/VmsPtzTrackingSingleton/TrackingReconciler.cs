namespace VmsPtzTrackingSingleton;

/// <summary>
/// Keeps the running sessions equal to what the configuration asks for.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes self-guidance a property of a camera rather than of whoever is looking at it.
/// Nothing an operator does starts or stops a session; the set of them is derived, over and over,
/// from <c>ptz_tracking_policy</c>. So a camera comes back tracking after this service restarts,
/// and stops tracking the moment an administrator says so, whether or not anyone has the monitor
/// open.
/// </para>
/// <para>
/// It reconciles on three occasions, and needs all three. At startup, because that is where the
/// sessions come from. On <c>server-config-changed</c>, so a change takes effect at once instead of
/// when a cache happens to expire. And on a slow timer, because the first two can fail: VmsCfg may
/// be starting up alongside this service and not answer yet, and a bus event can be lost. The timer
/// is the one that makes those recoverable rather than permanent.
/// </para>
/// </remarks>
public sealed class TrackingReconciler(
  TrackingPolicyCache policies,
  TrackingSessionManager manager,
  ILogger<TrackingReconciler> logger) : IHostedService, IDisposable
{
  /// Long enough to be a backstop rather than a polling loop: events do the timely work.
  private static readonly TimeSpan Idle = TimeSpan.FromMinutes(2);
  /// After a failed read there is nothing to lose by asking again soon.
  private static readonly TimeSpan Retry = TimeSpan.FromSeconds(10);

  private readonly CancellationTokenSource _cts = new();
  // A signal rather than a queue: several events arriving together want one reconcile, not four.
  private readonly SemaphoreSlim _wake = new(0, 1);
  private Task? _loop;

  /// <summary>Asks for a reconcile as soon as one can be run.</summary>
  public void Trigger()
  {
    // Release only when nothing is already pending, so repeated calls collapse into one pass.
    if (_wake.CurrentCount == 0)
    {
      try { _wake.Release(); } catch (SemaphoreFullException) { }
    }
  }

  public Task StartAsync(CancellationToken cancellationToken)
  {
    _loop = RunAsync(_cts.Token);
    return Task.CompletedTask;
  }

  private async Task RunAsync(CancellationToken ct)
  {
    while (!ct.IsCancellationRequested)
    {
      var delay = await ReconcileOnceAsync(ct) ? Idle : Retry;
      try
      {
        await _wake.WaitAsync(delay, ct);
      }
      catch (OperationCanceledException)
      {
        break;
      }
    }
  }

  /// <returns>False when the configuration could not be read, so it is worth retrying sooner.</returns>
  private async Task<bool> ReconcileOnceAsync(CancellationToken ct)
  {
    try
    {
      var wanted = await policies.ListAsync(ct);
      if (wanted is null)
        return false;

      await manager.ReconcileAsync(wanted, ct);
      return true;
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
      return true;
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "Could not reconcile tracking sessions with the configuration");
      return false;
    }
  }

  public async Task StopAsync(CancellationToken cancellationToken)
  {
    await _cts.CancelAsync();
    if (_loop is not null)
    {
      try { await _loop.WaitAsync(cancellationToken); } catch (OperationCanceledException) { }
    }
  }

  public void Dispose()
  {
    _cts.Dispose();
    _wake.Dispose();
  }
}

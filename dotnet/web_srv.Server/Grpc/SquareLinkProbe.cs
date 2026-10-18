using Grpc.Core;
using Vms.Square;

namespace web_srv;

/// <summary>
/// Whether this deployment is integrated with Square, asked of the only service that knows.
/// </summary>
/// <remarks>
/// <para>
/// Cached because the one caller is <c>/api/config/frontend</c>, which is anonymous and fetched
/// before login — a gRPC round trip per page load, for an answer that changes when a deployment is
/// reconfigured, has no business there.
/// </para>
/// <para>
/// Fails closed. An unreachable VmsSquareSingleton means nothing can be said about the integration,
/// and hiding a link that would lead nowhere is the better of the two mistakes.
/// </para>
/// </remarks>
/// <param name="Client">
/// Null when DAPR_SQUARE_SINGLETON_APP_ID is unset — a deployment that does not run the
/// integration at all. A record rather than a nullable service registration, which the container
/// cannot express.
/// </param>
public sealed record SquareStatusClient(SquareStatusService.SquareStatusServiceClient? Client);

public sealed class SquareLinkProbe(
  SquareStatusClient status,
  ILogger<SquareLinkProbe> logger)
{
  private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);
  // Short: this sits in front of an anonymous endpoint, so a hung singleton must not hold the SPA's
  // first request open.
  private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(2);

  private readonly SemaphoreSlim _gate = new(1, 1);
  private bool _integrated;
  private DateTime _checkedAt = DateTime.MinValue;

  public async Task<bool> IsIntegratedAsync(CancellationToken ct)
  {
    // DAPR_SQUARE_SINGLETON_APP_ID unset is a deployment that does not run the integration at all.
    if (status.Client is null)
      return false;

    if (DateTime.UtcNow - _checkedAt < Ttl)
      return _integrated;

    await _gate.WaitAsync(ct);
    try
    {
      if (DateTime.UtcNow - _checkedAt < Ttl)
        return _integrated;

      var response = await status.Client.GetStatusAsync(
        new GetSquareStatusRequest(),
        new CallOptions(deadline: DateTime.UtcNow.Add(Deadline), cancellationToken: ct));

      // Both halves are needed. Configured says an operator switched the integration on; synced
      // says it actually reached Square, which is what makes a per-camera link resolve.
      _integrated = response.Configured && response.Synced;
      _checkedAt = DateTime.UtcNow;
      return _integrated;
    }
    catch (RpcException ex)
    {
      logger.LogDebug(ex, "Square integration status unavailable");
      _checkedAt = DateTime.UtcNow;  // do not hammer a service that is down
      _integrated = false;
      return false;
    }
    finally
    {
      _gate.Release();
    }
  }
}

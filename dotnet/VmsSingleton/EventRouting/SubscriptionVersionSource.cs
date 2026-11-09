using Dapr.Client;

namespace VmsSingleton.EventRouting;

/// <summary>
/// The shared <c>subscriptions-version</c> key VmsCfg writes after every subscription change.
/// </summary>
/// <remarks>
/// The value is opaque: only "different from the last one I read" matters. A missing key is a
/// valid answer (nothing was ever saved), so the first read establishes a baseline rather than
/// looking like a change.
/// </remarks>
public interface ISubscriptionVersionSource
{
  Task<string?> ReadAsync(CancellationToken ct);
}

/// <summary>
/// Reads the version through this service's own sidecar.
/// </summary>
/// <remarks>
/// The read sits in front of every routed event, so it is bounded: a sidecar that stopped
/// answering must cost the router a timeout, not a stalled message. Two seconds is the same bound
/// OnvifRouter and RightsService use for their version probes.
/// </remarks>
public sealed class DaprSubscriptionVersionSource(DaprClient dapr, string stateStoreName)
  : ISubscriptionVersionSource
{
  /// <summary>VmsCfg's key. The name is shared by alarm definitions, which is why it is not
  /// per-subscription.</summary>
  public const string VersionKey = "subscriptions-version";

  private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);

  public async Task<string?> ReadAsync(CancellationToken ct)
  {
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
    timeout.CancelAfter(ReadTimeout);

    return await dapr.GetStateAsync<string>(
      stateStoreName, VersionKey, cancellationToken: timeout.Token);
  }
}

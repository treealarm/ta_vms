using System;
using System.Threading;
using System.Threading.Tasks;
using Dapr.Client;
using Microsoft.Extensions.Logging;

namespace VmsMap.PubSub
{
  // Writes a new value to the rights-version key. The value carries no meaning: only "differs from
  // the last one seen" matters.
  internal sealed class DaprRightsVersionNotifier : IRightsVersionNotifier
  {
    public const string Key = "rights-version";

    private readonly DaprClient _dapr;
    private readonly ILogger<DaprRightsVersionNotifier> _logger;
    private readonly string _stateStoreName;

    public DaprRightsVersionNotifier(
      DaprClient dapr, ILogger<DaprRightsVersionNotifier> logger, string stateStoreName)
    {
      _dapr = dapr ?? throw new ArgumentNullException(nameof(dapr));
      _logger = logger ?? throw new ArgumentNullException(nameof(logger));
      _stateStoreName = stateStoreName ?? throw new ArgumentNullException(nameof(stateStoreName));
    }

    public async Task BumpAsync(CancellationToken ct)
    {
      try
      {
        await _dapr.SaveStateAsync(
          _stateStoreName, Key, Guid.NewGuid().ToString("N"), cancellationToken: ct);
      }
      catch (Exception ex)
      {
        // The rights themselves are already committed; only how fast a replica notices suffers,
        // and its own TTL is the backstop.
        _logger.LogWarning(ex, "Bumping rights-version failed; replicas will pick the change up by TTL");
      }
    }
  }
}

using System.Threading;
using System.Threading.Tasks;

namespace VmsMap.PubSub
{
  // Rewrites the shared state-store key web_srv watches to know its cached rights are stale. A
  // pub/sub event would reach one replica of web_srv; the key reaches all of them, which is why
  // CLAUDE.md uses this mechanism for replicable consumers.
  public interface IRightsVersionNotifier
  {
    Task BumpAsync(CancellationToken ct);
  }
}

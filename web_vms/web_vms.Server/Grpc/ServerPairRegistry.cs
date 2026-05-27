using Dapr.Client;
using Vms.Fs;

namespace web_vms;

public class ServerPairRegistry
{
  private readonly Dictionary<string, string> _serverToFs;

  public ServerPairRegistry()
  {
    _serverToFs = ParseServerPairs(
      Environment.GetEnvironmentVariable("MEDIA_SERVER_PAIRS")
        ?? throw new InvalidOperationException("Required environment variable 'MEDIA_SERVER_PAIRS' is not set"));
  }

  public FsService.FsServiceClient GetFsClient(string? serverId)
  {
    if (string.IsNullOrWhiteSpace(serverId))
      throw new InvalidOperationException("serverId must not be empty for FS client lookup");

    if (!_serverToFs.TryGetValue(serverId, out var fsId) || string.IsNullOrWhiteSpace(fsId))
      throw new InvalidOperationException($"No FS paired for server '{serverId}'");

    return new FsService.FsServiceClient(DaprClient.CreateInvocationInvoker(fsId));
  }

  public IReadOnlyCollection<string> AllFsIds =>
    _serverToFs.Values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().ToList();

  private static Dictionary<string, string> ParseServerPairs(string raw)
  {
    var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var entry in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
      var parts = entry.Split(':', 2);
      if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
        throw new InvalidOperationException($"Invalid MEDIA_SERVER_PAIRS entry: '{entry}'. Expected format: 'server-id:fs-id'");
      pairs[parts[0]] = parts[1];
    }
    if (pairs.Count == 0)
      throw new InvalidOperationException("MEDIA_SERVER_PAIRS must contain at least one entry");
    return pairs;
  }
}

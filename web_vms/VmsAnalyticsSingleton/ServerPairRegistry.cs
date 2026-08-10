using Dapr.Client;
using Vms.Cfg;
using Vms.Control;
using Vms.Fs;

namespace VmsAnalyticsSingleton;

public class ServerPairRegistry(CfgService.CfgServiceClient cfg)
{
  private IReadOnlyDictionary<string, string>? _serverToFs;

  public async Task InitAsync(CancellationToken ct = default)
  {
    var resp = await cfg.ListMediaServersAsync(new ListMediaServersRequest(), cancellationToken: ct);
    _serverToFs = resp.Servers
      .Where(s => !string.IsNullOrWhiteSpace(s.FsId))
      .ToDictionary(s => s.ServerId, s => s.FsId, StringComparer.OrdinalIgnoreCase);
  }

  public FsService.FsServiceClient GetFsClient(string? serverId)
  {
    var map = _serverToFs ?? throw new InvalidOperationException("ServerPairRegistry not initialized");

    if (string.IsNullOrWhiteSpace(serverId))
      throw new InvalidOperationException("serverId must not be empty for FS client lookup");

    if (!map.TryGetValue(serverId, out var fsId) || string.IsNullOrWhiteSpace(fsId))
      throw new InvalidOperationException($"No FS paired for server '{serverId}'");

    return new FsService.FsServiceClient(DaprClient.CreateInvocationInvoker(fsId));
  }

  public CameraControlService.CameraControlServiceClient GetMediaClient(string? serverId)
  {
    if (string.IsNullOrWhiteSpace(serverId))
      throw new InvalidOperationException("serverId must not be empty for media server requests");

    return new CameraControlService.CameraControlServiceClient(DaprClient.CreateInvocationInvoker(serverId));
  }
}

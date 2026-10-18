using Dapr.Client;
using Vms.Cfg;
using Vms.Fs;

namespace VmsOnvif;

/// The VmsFs this instance talks to.
///
/// One entry rather than a map: VmsOnvif is pinned to a node now, and the only cameras it ever sees
/// are that node's, so every FS call goes to that node's VmsFs. Resolved from VmsCfg at startup so
/// the pairing stays declared in one place (MEDIA_SERVER_PAIRS) instead of a second env var that
/// could drift from it.
public class ServerPairRegistry(CfgService.CfgServiceClient cfg, string ownServerId)
{
  private string? _fsId;

  public async Task InitAsync(CancellationToken ct = default)
  {
    var resp = await cfg.ListMediaServersAsync(new ListMediaServersRequest(), cancellationToken: ct);

    var own = resp.Servers.FirstOrDefault(
      s => string.Equals(s.ServerId, ownServerId, StringComparison.OrdinalIgnoreCase));
    if (own is null)
      throw new InvalidOperationException(
        $"Server '{ownServerId}' is not listed in MEDIA_SERVER_PAIRS — check OWN_SERVER_ID");
    if (string.IsNullOrWhiteSpace(own.FsId))
      throw new InvalidOperationException($"Server '{ownServerId}' has no FS paired in MEDIA_SERVER_PAIRS");

    _fsId = own.FsId;
  }

  public FsService.FsServiceClient GetFsClient()
  {
    var fsId = _fsId ?? throw new InvalidOperationException("ServerPairRegistry not initialized");
    return new FsService.FsServiceClient(DaprClient.CreateInvocationInvoker(fsId));
  }
}

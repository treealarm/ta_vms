using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vms.Cfg;

namespace VmsMap.Clients
{
  // Which VmsFs serves a node. VmsMap holds no filesystem app id of its own: the registry in VmsCfg
  // is the one place that knows, and it follows a camera moved between nodes.
  public interface IMediaServerRegistry
  {
    Task<string> FsForServerAsync(string serverId, CancellationToken ct);
  }

  internal sealed class VmsCfgMediaServerRegistry : IMediaServerRegistry
  {
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly CfgService.CfgServiceClient _cfg;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, string> _fsByServer = new(StringComparer.Ordinal);
    private DateTime _fetchedAtUtc = DateTime.MinValue;

    public VmsCfgMediaServerRegistry(CfgService.CfgServiceClient cfg)
    {
      _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
    }

    public async Task<string> FsForServerAsync(string serverId, CancellationToken ct)
    {
      await _gate.WaitAsync(ct);
      try
      {
        if (DateTime.UtcNow - _fetchedAtUtc >= CacheTtl)
        {
          var servers = await _cfg.ListMediaServersAsync(
            new ListMediaServersRequest(), cancellationToken: ct);
          _fsByServer = servers.Servers
            .Where(s => !string.IsNullOrWhiteSpace(s.ServerId))
            .ToDictionary(s => s.ServerId, s => s.FsId, StringComparer.Ordinal);
          _fetchedAtUtc = DateTime.UtcNow;
        }

        if (!_fsByServer.TryGetValue(serverId, out var fsId) || string.IsNullOrWhiteSpace(fsId))
          throw new InvalidOperationException(
            $"Server '{serverId}' has no VmsFs in the registry");

        return fsId;
      }
      finally
      {
        _gate.Release();
      }
    }
  }
}

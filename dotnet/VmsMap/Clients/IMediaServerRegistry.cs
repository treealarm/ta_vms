using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vms.Cfg;

namespace VmsMap.Clients
{
  // Which VmsFs serves a node, and which VmsOnvif can reach its cameras. VmsMap holds no app id of
  // its own: the registry in VmsCfg is the one place that knows, and it follows a camera moved
  // between nodes.
  public interface IMediaServerRegistry
  {
    Task<string> FsForServerAsync(string serverId, CancellationToken ct);

    /// <summary>
    /// The VmsOnvif of a node. ONVIF reaches a camera over the same local subnet RTSP does, so a
    /// call for a camera must go to its own node's instance — a central one has no route to it.
    /// </summary>
    Task<string> OnvifForServerAsync(string serverId, CancellationToken ct);

    /// <summary>
    /// The VmsOnvif of a node, or null when the deployment has none for it. Throws only when the
    /// registry itself could not be read — "no such channel" and "could not ask" are not the same.
    /// </summary>
    Task<string?> TryOnvifForServerAsync(string serverId, CancellationToken ct);

    /// <summary>Every server the registry knows, so a full pass can walk the nodes.</summary>
    Task<IReadOnlyList<string>> ServerIdsAsync(CancellationToken ct);
  }

  internal sealed class VmsCfgMediaServerRegistry : IMediaServerRegistry
  {
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly CfgService.CfgServiceClient _cfg;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, string> _fsByServer = new(StringComparer.Ordinal);
    private Dictionary<string, string> _onvifByServer = new(StringComparer.Ordinal);
    private IReadOnlyList<string> _serverIds = [];
    private DateTime _fetchedAtUtc = DateTime.MinValue;

    public VmsCfgMediaServerRegistry(CfgService.CfgServiceClient cfg)
    {
      _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
    }

    public async Task<string> FsForServerAsync(string serverId, CancellationToken ct)
    {
      await RefreshAsync(ct);

      if (!_fsByServer.TryGetValue(serverId, out var fsId) || string.IsNullOrWhiteSpace(fsId))
        throw new InvalidOperationException(
          $"Server '{serverId}' has no VmsFs in the registry");

      return fsId;
    }

    public async Task<string> OnvifForServerAsync(string serverId, CancellationToken ct)
    {
      await RefreshAsync(ct);

      if (!_onvifByServer.TryGetValue(serverId, out var onvifId) || string.IsNullOrWhiteSpace(onvifId))
        throw new InvalidOperationException(
          $"Server '{serverId}' has no VmsOnvif in the registry");

      return onvifId;
    }

    public async Task<string?> TryOnvifForServerAsync(string serverId, CancellationToken ct)
    {
      await RefreshAsync(ct);

      return _onvifByServer.TryGetValue(serverId, out var onvifId)
             && !string.IsNullOrWhiteSpace(onvifId)
        ? onvifId
        : null;
    }

    public async Task<IReadOnlyList<string>> ServerIdsAsync(CancellationToken ct)
    {
      await RefreshAsync(ct);
      return _serverIds;
    }

    // One fetch feeds both questions: they come from the same registry rows and change together.
    private async Task RefreshAsync(CancellationToken ct)
    {
      await _gate.WaitAsync(ct);
      try
      {
        if (DateTime.UtcNow - _fetchedAtUtc < CacheTtl)
          return;

        var servers = await _cfg.ListMediaServersAsync(
          new ListMediaServersRequest(), cancellationToken: ct);
        var rows = servers.Servers
          .Where(s => !string.IsNullOrWhiteSpace(s.ServerId))
          .ToList();
        _serverIds = rows.Select(s => s.ServerId).Distinct(StringComparer.Ordinal).ToList();
        _fsByServer = rows
          .Where(s => !string.IsNullOrWhiteSpace(s.FsId))
          .ToDictionary(s => s.ServerId, s => s.FsId, StringComparer.Ordinal);
        _onvifByServer = rows
          .Where(s => !string.IsNullOrWhiteSpace(s.OnvifId))
          .ToDictionary(s => s.ServerId, s => s.OnvifId, StringComparer.Ordinal);
        _fetchedAtUtc = DateTime.UtcNow;
      }
      finally
      {
        _gate.Release();
      }
    }
  }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapr.Client;
using Google.Protobuf.WellKnownTypes;
using Vms.Cfg;
using Vms.Fs;

namespace VmsMap.Clients
{
  // The retention journal lives in the node's VmsFs, and the camera names the node. The node's
  // fs_id comes from VmsCfg's server registry, so the map holds no filesystem app id of its own:
  // a camera moved between nodes is followed by this lookup.
  internal sealed class VmsFsRetentionEventSource : IRetentionEventSource
  {
    private static readonly TimeSpan ServerCacheTtl = TimeSpan.FromSeconds(30);

    private readonly CfgService.CfgServiceClient _cfg;
    private readonly ICameraCatalog _catalog;
    private readonly DaprClient _dapr;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, string> _fsByServer = new(StringComparer.Ordinal);
    private DateTime _fetchedAtUtc = DateTime.MinValue;

    public VmsFsRetentionEventSource(
      CfgService.CfgServiceClient cfg, ICameraCatalog catalog, DaprClient dapr)
    {
      _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
      _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
      _dapr = dapr ?? throw new ArgumentNullException(nameof(dapr));
    }

    public async Task<RetentionEpisode?> FindAsync(
      string cameraId, DateTime alarmStartTs, CancellationToken ct)
    {
      var camera = await _catalog.GetAsync(cameraId, ct)
        ?? throw new InvalidOperationException($"Camera '{cameraId}' is not in VmsCfg");
      var fsId = await FsForServerAsync(camera.ServerId, ct);

      var client = new FsService.FsServiceClient(
        DaprClient.CreateInvocationInvoker(fsId));

      var start = DateTime.SpecifyKind(alarmStartTs, DateTimeKind.Utc);
      var response = await client.ListRetentionEventsAsync(
        new ListRetentionEventsRequest
        {
          CameraId = cameraId,
          FromAlarmStartTs = Timestamp.FromDateTime(start),
          Limit = 50,
        },
        cancellationToken: ct);

      // The episode the event named is the one whose start matches; a renewal carries the same
      // start, so this is stable across the open and close messages.
      var row = response.Events
        .Where(e => e.AlarmStartTs is not null)
        .OrderBy(e => Math.Abs((e.AlarmStartTs.ToDateTime() - start).TotalSeconds))
        .FirstOrDefault();

      if (row is null)
        return null;

      return new RetentionEpisode(
        row.AlarmStartTs.ToDateTime(),
        row.AlarmEndTs is not null ? row.AlarmEndTs.ToDateTime() : null,
        row.HasStreamName ? row.StreamName : null,
        row.HasTrigger ? row.Trigger : null,
        row.EventUuid);
    }

    private async Task<string> FsForServerAsync(string serverId, CancellationToken ct)
    {
      await _gate.WaitAsync(ct);
      try
      {
        if (DateTime.UtcNow - _fetchedAtUtc >= ServerCacheTtl)
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

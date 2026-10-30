using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapr.Client;
using Microsoft.Extensions.Logging;
using Vms.Fs;

namespace VmsMap.Clients
{
  // Reads the open source outages of each node's VmsFs. One call per node, not per camera: the
  // outage list is small and the cameras of a node share the journal.
  internal sealed class VmsFsCameraConnectionSource : ICameraConnectionSource
  {
    private readonly IMediaServerRegistry _servers;
    private readonly ILogger<VmsFsCameraConnectionSource> _logger;

    public VmsFsCameraConnectionSource(
      IMediaServerRegistry servers, ILogger<VmsFsCameraConnectionSource> logger)
    {
      _servers = servers ?? throw new ArgumentNullException(nameof(servers));
      _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<Dictionary<string, bool>> GetConnectedAsync(
      IReadOnlyCollection<CameraInfo> cameras, CancellationToken ct)
    {
      var result = new Dictionary<string, bool>(StringComparer.Ordinal);

      foreach (var group in cameras
        .Where(c => !string.IsNullOrWhiteSpace(c.ServerId))
        .GroupBy(c => c.ServerId, StringComparer.Ordinal))
      {
        try
        {
          var fsId = await _servers.FsForServerAsync(group.Key, ct);
          var client = new FsService.FsServiceClient(
            DaprClient.CreateInvocationInvoker(fsId));

          var outages = await client.ListSourceOutagesAsync(
            new ListSourceOutagesRequest { Limit = 1000 },
            cancellationToken: ct);

          var open = outages.Outages
            .Where(o => o.EndedTs is null)
            .Select(o => o.CameraId)
            .ToHashSet(StringComparer.Ordinal);

          foreach (var camera in group)
            result[camera.CameraId] = !open.Contains(camera.CameraId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
          throw;
        }
        catch (Exception ex)
        {
          // The node's VmsFs could not be asked. Its cameras stay out of the answer and keep
          // whatever state they had; the next cycle asks again.
          _logger.LogWarning(ex,
            "Source liveness lookup failed for server '{ServerId}'", group.Key);
        }
      }

      return result;
    }
  }
}

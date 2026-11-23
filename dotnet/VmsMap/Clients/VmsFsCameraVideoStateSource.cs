using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapr.Client;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Vms.Fs;

namespace VmsMap.Clients
{
  // Reads the video state of each camera's streams from the node's VmsFs. Batched by camera: a node
  // of a hundred thousand cameras is asked in bounded chunks, not in one message. A node that
  // cannot be asked leaves its cameras out of the answer, so the caller sees "unknown" and changes
  // nothing.
  internal sealed class VmsFsCameraVideoStateSource : ICameraVideoStateSource
  {
    private const int BatchSize = 1000;
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);

    private readonly IMediaServerRegistry _servers;
    private readonly ILogger<VmsFsCameraVideoStateSource> _logger;
    private readonly ConcurrentDictionary<string, FsService.FsServiceClient> _clients =
      new(StringComparer.Ordinal);

    public VmsFsCameraVideoStateSource(
      IMediaServerRegistry servers, ILogger<VmsFsCameraVideoStateSource> logger)
    {
      _servers = servers ?? throw new ArgumentNullException(nameof(servers));
      _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, VideoChannelState>>>
      GetAsync(IReadOnlyCollection<CameraInfo> cameras, CancellationToken ct)
    {
      var result = new Dictionary<string, IReadOnlyDictionary<string, VideoChannelState>>(
        StringComparer.Ordinal);

      foreach (var group in cameras
        .Where(c => !string.IsNullOrWhiteSpace(c.ServerId))
        .GroupBy(c => c.ServerId, StringComparer.Ordinal))
      {
        try
        {
          var fsId = await _servers.FsForServerAsync(group.Key, ct);
          var client = Client(fsId);
          var cameraIds = group.Select(c => c.CameraId).ToList();

          for (var offset = 0; offset < cameraIds.Count; offset += BatchSize)
          {
            var request = new ListSourceStatesRequest();
            request.CameraIds.AddRange(cameraIds.Skip(offset).Take(BatchSize));

            var response = await client.ListSourceStatesAsync(
              request,
              new CallOptions(deadline: DateTime.UtcNow.Add(CallTimeout), cancellationToken: ct));

            Merge(result, response);
          }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
          throw;
        }
        catch (Exception ex)
        {
          // The node's VmsFs could not be asked. Its cameras stay out of the answer and keep
          // whatever state they had; the next event or pass asks again.
          _logger.LogWarning(ex,
            "Source video states failed for server '{ServerId}'", group.Key);
        }
      }

      return result;
    }

    private static void Merge(
      Dictionary<string, IReadOnlyDictionary<string, VideoChannelState>> result,
      ListSourceStatesResponse response)
    {
      foreach (var stream in response.Streams)
      {
        if (!result.TryGetValue(stream.CameraId, out var byStream))
        {
          byStream = new Dictionary<string, VideoChannelState>(StringComparer.Ordinal);
          result[stream.CameraId] = byStream;
        }

        ((Dictionary<string, VideoChannelState>)byStream)[stream.StreamName] = ToState(stream.State);
      }
    }

    private static VideoChannelState ToState(SourceVideoState state) => state switch
    {
      SourceVideoState.Alive => VideoChannelState.Alive,
      SourceVideoState.NoVideo => VideoChannelState.NoVideo,
      _ => VideoChannelState.Unknown,
    };

    // One invoker per node, reused: an event walk and a sweep may both ask the same VmsFs.
    private FsService.FsServiceClient Client(string fsAppId) =>
      _clients.GetOrAdd(fsAppId, appId => new FsService.FsServiceClient(
        DaprClient.CreateInvocationInvoker(appId)));
  }
}

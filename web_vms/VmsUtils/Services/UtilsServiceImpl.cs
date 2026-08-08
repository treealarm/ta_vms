using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Vms.Utils;

namespace VmsUtils;

public class UtilsServiceImpl(
  MediaMtxServerCache serverCache,
  MediaMtxPathSnapshotCache snapshotCache,
  EntityStateRegistry entityStateRegistry,
  ILogger<UtilsServiceImpl> logger)
  : UtilsService.UtilsServiceBase
{
  /// <summary>Carries the vms_read Basic credentials MediaMTX's Control API requires.</summary>
  public const string MediaMtxHttpClientName = "mediamtx-api";

  public override Task<ListEntityStatesResponse> ListEntityStates(Empty request, ServerCallContext context)
  {
    var response = new ListEntityStatesResponse();
    foreach (var (key, entry) in entityStateRegistry.GetAll())
    {
      response.Entities.Add(new EntityState
      {
        Service = key.Service,
        EntityType = key.EntityType,
        EntityId = key.EntityId,
        StateJson = entry.StateJson,
        LastSeen = Timestamp.FromDateTime(DateTime.SpecifyKind(entry.LastSeenUtc, DateTimeKind.Utc)),
        Stale = entry.Stale,
      });
    }
    return Task.FromResult(response);
  }

  public override async Task<GetMediaMtxPathsInfoResponse> GetMediaMtxPathsInfo(
    GetMediaMtxPathsInfoRequest request,
    ServerCallContext context)
  {
    var ct = context.CancellationToken;
    var response = new GetMediaMtxPathsInfoResponse();

    // Resolve every camera's server first, then take one snapshot per distinct server: the caller's
    // paths usually all live on the same MediaMTX, so this is a single list request for the batch.
    var servers = new Dictionary<string, MediaMtxServerRef?>(StringComparer.Ordinal);
    foreach (var pathRef in request.Paths)
    {
      if (servers.ContainsKey(pathRef.CameraId))
        continue;

      try
      {
        var server = await serverCache.GetAsync(pathRef.CameraId, ct);
        servers[pathRef.CameraId] = server is null
          ? null
          : new MediaMtxServerRef(
              server.ApiPort > 0 ? $"http://{server.Host}:{server.ApiPort}" : null);
      }
      catch (Exception ex)
      {
        logger.LogWarning(ex, "Resolving the MediaMTX server for camera '{CameraId}' failed", pathRef.CameraId);
        servers[pathRef.CameraId] = null;
      }
    }

    var snapshots = new Dictionary<string, IReadOnlyDictionary<string, MediaMtxPathState>?>(StringComparer.Ordinal);
    foreach (var apiUrl in servers.Values.OfType<MediaMtxServerRef>()
               .Select(s => s.ApiUrl).OfType<string>().Distinct(StringComparer.Ordinal))
    {
      snapshots[apiUrl] = await snapshotCache.GetAsync(apiUrl, ct);
    }

    foreach (var pathRef in request.Paths)
    {
      var info = new MediaMtxPathInfo
      {
        CameraId   = pathRef.CameraId,
        StreamName = pathRef.StreamName,
      };

      // No server assigned, or resolving it failed: nothing can be said about viewers.
      if (servers[pathRef.CameraId] is not { } server)
      {
        response.Paths.Add(info);
        continue;
      }

      // No control API on this server: an answer about viewers is unobtainable, and saying "nobody
      // is watching" would be a guess. Reported as a successful answer of a different kind, so the
      // caller can tell it apart from the API being momentarily unreachable.
      if (server.ApiUrl is null)
      {
        info.Success = true;
        response.Paths.Add(info);
        continue;
      }

      if (snapshots[server.ApiUrl] is not { } paths)
      {
        response.Paths.Add(info);
        continue;
      }

      info.Success = true;
      info.ApiConfigured = true;
      // A path exists only while something publishes to it or someone waits on it, so one missing
      // from the snapshot means both are true of nobody.
      if (paths.TryGetValue($"{pathRef.CameraId}/{pathRef.StreamName}", out var state))
      {
        info.ReadersCount = state.ReadersCount;
        info.HasSource = state.HasSource;
      }

      response.Paths.Add(info);
    }

    return response;
  }

  /// ApiUrl is null when the assigned server has no control API port.
  private sealed record MediaMtxServerRef(string? ApiUrl);
}

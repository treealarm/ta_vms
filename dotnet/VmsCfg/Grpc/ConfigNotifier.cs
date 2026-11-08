using System.Text.Json.Serialization;
using Dapr.Client;

namespace VmsCfg;

/// <summary>
/// The signals VmsCfg sends when camera configuration changes: a change on a node, a camera that
/// may have moved between nodes, and the statement that everything on a node may be stale.
/// </summary>
/// <remarks>
/// A service of its own rather than private helpers of <see cref="CfgServiceImpl"/>, because the
/// demo installer writes cameras the same way an administrator does and has to announce them the
/// same way — and the installer cannot depend on the gRPC service, which depends on it.
/// </remarks>
public sealed class ConfigNotifier(ScheduleDb scheduleDb, DaprClient daprClient, ILogger<ConfigNotifier> logger)
{
  // One event for "something changed on this node, here is what". Replaces the old pair of
  // per-camera and per-server topics: the bulk paths published both anyway, one message per
  // camera, and every subscriber received every message. Now a schedule boundary is one message
  // per affected node instead of one per camera, and a node-scoped subscriber can tell at a glance
  // whether an event concerns it — before, deciding that cost a GetCamera round trip per instance.
  //
  // Payload: { "server_id": "...", "camera_ids": [...] }. An empty camera_ids means everything on
  // that node. The ids are addressing, not state: a receiver still re-reads the configuration it
  // needs, so a late or reordered message cannot make it act on a stale snapshot.
  private const string ServerConfigChangedTopic = "server-config-changed";

  // Bumped when a camera's media server changes. web_srv.Server caches camera → node to route
  // ONVIF calls, and it runs in several replicas, so a pub/sub event would reach exactly one of
  // them; a key everyone reads has no such asymmetry. Same mechanism as the permission version.
  private const string CamNodeVersionKey = "cam-node-version";

  // Field names are spelled out rather than left to a naming policy: DaprClient serializes with
  // web defaults (camelCase), the subscribers — one of them a hand-rolled C++ reader — expect
  // snake_case, and the mismatch does not fail. It deserializes into an all-null record and the
  // event is dropped in silence by every consumer at once.
  private sealed record ServerConfigChanged(
    [property: JsonPropertyName("server_id")] string ServerId,
    [property: JsonPropertyName("camera_ids")] IReadOnlyList<string> CameraIds);

  public string PubSubName { get; } =
    Environment.GetEnvironmentVariable("PUBSUB_NAME")
      ?? throw new InvalidOperationException("Required environment variable 'PUBSUB_NAME' is not set");

  public static string StateStoreName { get; } =
    Environment.GetEnvironmentVariable("DAPR_STATE_STORE_NAME")
      ?? throw new InvalidOperationException("Required environment variable 'DAPR_STATE_STORE_NAME' is not set");

  /// <summary>
  /// One camera changed. The owning node is resolved here rather than threaded through every
  /// write handler: it is one indexed lookup on a path a human triggers, and it keeps all twelve
  /// call sites unaware of the topic's shape.
  /// </summary>
  public void InvalidateFsCache(string cameraId)
  {
    _ = Task.Run(async () =>
    {
      try
      {
        var servers = await scheduleDb.ListCameraServersAsync([cameraId], CancellationToken.None);
        servers.TryGetValue(cameraId, out var serverId);
        await PublishServerConfigChangedAsync(serverId ?? string.Empty, [cameraId], CancellationToken.None);
      }
      catch (Exception ex)
      {
        logger.LogWarning(ex, "Camera config invalidation publish failed for '{CameraId}'", cameraId);
      }
    });
  }

  public async Task PublishServerConfigChangedAsync(
    string serverId, IReadOnlyList<string> cameraIds, CancellationToken ct)
  {
    await daprClient.PublishEventAsync(
      PubSubName, ServerConfigChangedTopic, new ServerConfigChanged(serverId, cameraIds), ct);
  }

  /// <summary>
  /// Tells web_srv replicas that a camera may have moved between nodes, so their ONVIF routing
  /// cache stops pointing at the old one. Awaited: camera moves are rare admin actions, and a lost
  /// bump leaves PTZ going to the wrong node until the cache's own TTL expires.
  /// </summary>
  public async Task BumpCamNodeVersionAsync(CancellationToken ct)
  {
    try
    {
      await daprClient.SaveStateAsync(
        StateStoreName, CamNodeVersionKey, Guid.NewGuid().ToString("N"), cancellationToken: ct);
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "Bumping the camera node version failed — replicas will pick the move up by TTL");
    }
  }

  /// <summary>
  /// One camera changed on a node the caller already knows — used where InvalidateFsCache cannot
  /// resolve it from the database, either because the row is gone or because the node it names is
  /// the one the camera just left.
  /// </summary>
  public void NotifyCameraChanged(string serverId, string cameraId)
  {
    if (string.IsNullOrWhiteSpace(serverId)) return;
    _ = Task.Run(async () =>
    {
      try
      {
        await PublishServerConfigChangedAsync(serverId, [cameraId], CancellationToken.None);
      }
      catch (Exception ex)
      {
        logger.LogWarning(ex, "Camera config notification failed for '{CameraId}'", cameraId);
      }
    });
  }
}

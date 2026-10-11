using System.Text.Json.Serialization;
using Dapr.Client;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Npgsql;
using Vms.Cfg;

namespace VmsCfg
{
  public partial class CfgServiceImpl(
    CameraStorage storage,
    StorageDb storageDb,
    ScheduleDb scheduleDb,
    ScheduleCache scheduleCache,
    PermissionDb permissionDb,
    SpatialDb spatialDb,
    DaprClient daprClient,
    ILogger<CfgServiceImpl> logger) : CfgService.CfgServiceBase
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
    private const string CameraStateChangedTopic       = "camera-state-changed";
    private const string AnalyticsWatchChangedTopic    = "analytics-watch-changed";
    // Wakes the ticker in VmsSingleton after a schedule edit. It has its own app id, hence its own
    // subscription, so a topic reaches it — unlike VmsCfg's own replicas, which share one consumer
    // group and use version keys instead. Without it a new window opening in seconds would wait for
    // the ticker's next poll.
    private const string RecordingScheduleChangedTopic = "recording-schedule-changed";

    // Shared state store key whose value changes on every permission or ACL-mode write; web_vms
    // watches it to know when to drop its cached grants. See BumpCameraPermissionsVersionAsync.
    private const string CameraPermissionsVersionKey   = "cam-perm-version";

    // Bumped when a camera's media server changes. web_vms.Server caches camera → node to route
    // ONVIF calls, and it runs in several replicas, so a pub/sub event would reach exactly one of
    // them; a key everyone reads has no such asymmetry. Same mechanism as the permission version.
    private const string CameraNodeVersionKey          = "cam-node-version";

    // Investigation boards are operator-created and never expire on their own, so the number one
    // person can accumulate is capped. Walls are unaffected — they are admin-managed.
    private const int MaxInvestigationBoardsPerOwner = 8;

    private readonly string _pubSubName =
      Environment.GetEnvironmentVariable("PUBSUB_NAME")
        ?? throw new InvalidOperationException("Required environment variable 'PUBSUB_NAME' is not set");

    // Field names are spelled out rather than left to a naming policy: DaprClient serializes with
    // web defaults (camelCase), the subscribers — one of them a hand-rolled C++ reader — expect
    // snake_case, and the mismatch does not fail. It deserializes into an all-null record and the
    // event is dropped in silence by every consumer at once.
    private sealed record ServerConfigChanged(
      [property: JsonPropertyName("server_id")] string ServerId,
      [property: JsonPropertyName("camera_ids")] IReadOnlyList<string> CameraIds);

    /// One camera changed. The owning node is resolved here rather than threaded through every
    /// write handler: it is one indexed lookup on a path a human triggers, and it keeps all twelve
    /// call sites unaware of the topic's shape.
    private void InvalidateFsCache(string cameraId)
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

    private async Task PublishServerConfigChangedAsync(
      string serverId, IReadOnlyList<string> cameraIds, CancellationToken ct)
    {
      await daprClient.PublishEventAsync(
        _pubSubName, ServerConfigChangedTopic, new ServerConfigChanged(serverId, cameraIds), ct);
    }

    private void NotifyCameraStateChanged(string cameraId)
    {
      _ = Task.Run(async () =>
      {
        try
        {
          await daprClient.PublishEventAsync(_pubSubName, CameraStateChangedTopic, cameraId);
        }
        catch (Exception ex)
        {
          logger.LogWarning(ex, "Camera state invalidation publish failed for '{CameraId}'", cameraId);
        }
      });
    }

    private void NotifyAnalyticsWatchChanged(string watchId)
    {
      _ = Task.Run(async () =>
      {
        try
        {
          await daprClient.PublishEventAsync(_pubSubName, AnalyticsWatchChangedTopic, watchId);
        }
        catch (Exception ex)
        {
          logger.LogWarning(ex, "Analytics watch change notification failed for '{WatchId}'", watchId);
        }
      });
    }

    /// <summary>
    /// Marks every cached permission decision in every web_vms replica as stale by writing a new
    /// value to a shared key. Deliberately not a pub/sub event: all replicas of a service share one
    /// Dapr app id, hence one consumer group, so an event would reach exactly one of them and the
    /// rest would keep enforcing revoked grants until their TTL expired. A key everybody reads has
    /// no such asymmetry. The value carries no meaning — only "differs from the last one seen".
    ///
    /// Awaited rather than fire-and-forget: permission writes are rare admin actions, and a lost
    /// bump silently degrades revocation to the consumer's TTL backstop.
    /// </summary>
    private async Task BumpCameraPermissionsVersionAsync(CancellationToken ct)
    {
      try
      {
        await daprClient.SaveStateAsync(
          StateStoreName, CameraPermissionsVersionKey, Guid.NewGuid().ToString("N"), cancellationToken: ct);
      }
      catch (Exception ex)
      {
        // The permissions themselves are already committed; only their propagation speed suffers.
        logger.LogWarning(ex, "Bumping the camera permission version failed — replicas will pick the change up by TTL");
      }
    }

    /// Tells web_vms replicas that a camera may have moved between nodes, so their ONVIF routing
    /// cache stops pointing at the old one. Awaited: camera moves are rare admin actions, and a lost
    /// bump leaves PTZ going to the wrong node until the cache's own TTL expires.
    private async Task BumpCameraNodeVersionAsync(CancellationToken ct)
    {
      try
      {
        await daprClient.SaveStateAsync(
          StateStoreName, CameraNodeVersionKey, Guid.NewGuid().ToString("N"), cancellationToken: ct);
      }
      catch (Exception ex)
      {
        logger.LogWarning(ex, "Bumping the camera node version failed — replicas will pick the move up by TTL");
      }
    }

    /// Tells the other VmsCfg replicas that their schedule snapshot is stale. Awaited, like the
    /// permission version: schedule edits are rare admin actions, and a lost bump leaves a replica
    /// answering with the old windows until its TTL runs out.
    private async Task BumpScheduleVersionAsync(CancellationToken ct)
    {
      try
      {
        await daprClient.SaveStateAsync(
          StateStoreName, ScheduleCache.VersionKey, Guid.NewGuid().ToString("N"), cancellationToken: ct);
      }
      catch (Exception ex)
      {
        logger.LogWarning(ex, "Bumping the schedule version failed — other replicas will reload by TTL");
      }
    }

    private void NotifyRecordingScheduleChanged()
    {
      _ = Task.Run(async () =>
      {
        try
        {
          await daprClient.PublishEventAsync(_pubSubName, RecordingScheduleChangedTopic, string.Empty);
        }
        catch (Exception ex)
        {
          logger.LogWarning(ex, "Recording schedule change notification failed");
        }
      });
    }

    /// One camera changed on a node the caller already knows — used where InvalidateFsCache cannot
    /// resolve it from the database, either because the row is gone or because the node it names is
    /// the one the camera just left.
    private void NotifyCameraChanged(string serverId, string cameraId)
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

    /// Everything on this node may have changed — an empty camera list says so.
    private void NotifyMediaServer(string serverId)
    {
      if (string.IsNullOrWhiteSpace(serverId)) return;
      _ = Task.Run(async () =>
      {
        try
        {
          await PublishServerConfigChangedAsync(serverId, [], CancellationToken.None);
        }
        catch (Exception ex)
        {
          logger.LogWarning(ex, "Media server config notification failed for '{ServerId}'", serverId);
        }
      });
    }

    /// Tells everyone downstream that these cameras' effective recording state may have changed:
    /// VmsFs drops its config caches, and each camera's media server reloads its configuration.
    /// Used by the schedule paths, where nothing in the camera's own config row changed.
    ///
    /// Unlike the fire-and-forget helpers above this awaits the publishes and reports failure, so
    /// the schedule ticker can retry a window boundary instead of losing it: media_server only
    /// learns about the new policy from these events.
    internal async Task<bool> NotifyCamerasChangedAsync(IReadOnlyList<string> cameraIds, CancellationToken ct)
    {
      if (cameraIds.Count == 0)
        return true;

      try
      {
        var servers = await scheduleDb.ListCameraServersAsync(cameraIds, ct);

        // Grouped by node, so a boundary that flips every camera in the system costs one message per
        // node rather than one per camera — which is also what keeps this loop from turning into
        // thousands of sequential publishes to the sidecar.
        var byServer = cameraIds
          .Where(id => servers.TryGetValue(id, out var s) && !string.IsNullOrWhiteSpace(s))
          .GroupBy(id => servers[id], StringComparer.Ordinal);

        foreach (var group in byServer)
          await PublishServerConfigChangedAsync(group.Key, group.ToList(), ct);

        return true;
      }
      catch (Exception ex)
      {
        logger.LogWarning(ex, "Notifying cameras of a recording-schedule change failed: [{CameraIds}]",
          string.Join(", ", cameraIds));
        return false;
      }
    }

    // A MediaMTX server's connection info (rtsp_url etc.) can be referenced by cameras on
    // any recording server, so notify all of them to re-resolve the WebRTC push URL.
    private void NotifyAllMediaServers()
    {
      foreach (var (serverId, _, _, _, _) in storage.GetServerPairs())
        NotifyMediaServer(serverId);
    }




    private static readonly string StateStoreName =
      Environment.GetEnvironmentVariable("DAPR_STATE_STORE_NAME")
        ?? throw new InvalidOperationException("Required environment variable 'DAPR_STATE_STORE_NAME' is not set");

    public override async Task<OperationStatus> SetupCamera(CameraSetup request, ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.CameraId))
        return new OperationStatus { Success = false, Message = "camera_id must not be empty" };

      if (string.IsNullOrWhiteSpace(request.ServerId))
        return new OperationStatus { Success = false, Message = "server_id must not be empty" };

      if (!storage.IsValidServerId(request.ServerId))
        return new OperationStatus { Success = false, Message = $"server_id '{request.ServerId}' is not a known media server" };

      // Get old server before updating so we can notify it if server_id changed
      var (_, existing) = await storage.GetCameraAsync(request.CameraId, context.CancellationToken);

      await storage.SetupCameraAsync(request, context.CancellationToken);
      logger.LogInformation("Camera '{CameraId}' configured (server={ServerId})", request.CameraId, request.ServerId);
      await EnsureCameraHasStorageAsync(request.CameraId, context.CancellationToken);
      // The camera's own event already names this node — InvalidateFsCache resolves it from the
      // row just written — so there is no second, node-wide notification here. That one would make
      // every subscriber drop its whole cache on an ordinary camera edit.
      InvalidateFsCache(request.CameraId);

      // The node it left hears about the camera by name, so it can release it at once instead of
      // waiting for its next reconciliation sweep.
      if (existing is not null && existing.ServerId != request.ServerId)
        NotifyCameraChanged(existing.ServerId, request.CameraId);

      // A new camera has no cached route yet; a moved one has a stale one.
      if (existing is null || existing.ServerId != request.ServerId)
        await BumpCameraNodeVersionAsync(context.CancellationToken);

      return new OperationStatus { Success = true };
    }


    /// <summary>
    /// Gives a camera with no storage assignment the first configured storage.
    /// </summary>
    /// <remarks>
    /// A camera without one silently records nothing and cannot receive an archive import — the
    /// failure only surfaces much later as "no writable storage", long after whoever added the
    /// camera has moved on. An explicit assignment is still respected: this only fills a gap, and
    /// only when a storage exists to fill it with.
    /// </remarks>
    private async Task EnsureCameraHasStorageAsync(string cameraId, CancellationToken ct)
    {
      try
      {
        var assigned = await storageDb.GetCameraStoragesAsync(cameraId, ct);
        if (assigned.Count > 0)
          return;

        var storages = await storageDb.ListStoragesAsync(ct);
        if (storages.Count == 0)
        {
          logger.LogWarning(
            "Camera '{CameraId}' has no storage assigned and none is configured — it will record nothing",
            cameraId);
          return;
        }

        var fallback = storages[0];
        await storageDb.SetCameraStoragesAsync(
          cameraId,
          [new CameraStorageAssignment { StorageId = fallback.StorageId, Priority = 0 }],
          ct);
        logger.LogInformation(
          "Camera '{CameraId}' had no storage assigned; defaulted it to '{StorageId}'",
          cameraId, fallback.StorageId);
      }
      catch (Exception ex)
      {
        // Never fail camera setup over this — the camera is still usable, just without a place to
        // record until someone assigns one.
        logger.LogWarning(ex, "Could not assign a default storage to camera '{CameraId}'", cameraId);
      }
    }

    /// Everything the schedule ticker in VmsSingleton needs, resolved here against the wall clock.
    /// The evaluator and the schedule cache stay in this service — the ticker only spots transitions
    /// and announces them, so it carries no copy of the rules.
    public override async Task<RecordingScheduleStateResponse> GetRecordingScheduleState(
      Google.Protobuf.WellKnownTypes.Empty request,
      ServerCallContext context)
    {
      var ct = context.CancellationToken;
      await scheduleCache.ReloadAsync(ct);

      var now = DateTime.UtcNow;
      var response = new RecordingScheduleStateResponse();

      foreach (var (cameraId, _) in await scheduleDb.ListCamerasWithSchedulesAsync(ct))
      {
        response.ScheduledCameraIds.Add(cameraId);
        if (scheduleCache.IsCameraActive(cameraId, now))
          response.ActiveCameraIds.Add(cameraId);
      }

      var next = scheduleCache.NextTransitionUtc(now);
      if (next is not null)
        response.NextTransition = Timestamp.FromDateTime(DateTime.SpecifyKind(next.Value, DateTimeKind.Utc));

      return response;
    }

    public override async Task<OperationStatus> NotifyCamerasChanged(
      NotifyCamerasChangedRequest request,
      ServerCallContext context)
    {
      var ok = await NotifyCamerasChangedAsync(request.CameraIds, context.CancellationToken);
      return new OperationStatus { Success = ok, Message = ok ? string.Empty : "publish failed" };
    }

    /// Clears the overrides whose window has elapsed. Driven from VmsSingleton: the sweep is
    /// periodic global work and does not belong in a replicated service, while the SQL does belong
    /// here, over VmsCfg's own table. ClearAlarmOverrideAsync is a compare-and-swap on
    /// alarm_override_until, so only the caller that actually cleared a row hears about it — this
    /// stays correct even if two sweeps ever overlap.
    ///
    /// Announcing is left to the caller, which batches: every notification makes a media server
    /// re-read its whole configuration, and a steady stream of alarms would otherwise reload it on
    /// every tick.
    public override async Task<ExpireAlarmOverridesResponse> ExpireAlarmOverrides(
      Empty request,
      ServerCallContext context)
    {
      var response = new ExpireAlarmOverridesResponse();
      foreach (var (cameraId, streamName) in await storage.ListAlarmOverrideStreamsAsync(context.CancellationToken))
      {
        if (!await storage.ClearAlarmOverrideAsync(cameraId, streamName, context.CancellationToken))
          continue;

        logger.LogInformation(
          "Alarm recording expired for camera '{CameraId}' stream='{StreamName}'", cameraId, streamName);
        response.Expired.Add(new ExpiredAlarmOverride { CameraId = cameraId, StreamName = streamName });
      }
      return response;
    }

    public override async Task<ListMediaServersResponse> ListMediaServers(
      ListMediaServersRequest request,
      ServerCallContext context)
    {
      var response = new ListMediaServersResponse();
      foreach (var (serverId, fsId, onvifId, loggerId, roiId) in storage.GetServerPairs())
      {
        // A missing key is the normal "no heartbeat within its TTL" answer. Anything else — the
        // state store being down, or a heartbeat written in a shape this cannot read — is reported
        // as offline too, but it is a fault and must not pass silently: swallowing it is exactly
        // how every media server came to be displayed as offline for as long as it did.
        string status;
        try
        {
          status = await daprClient.GetStateAsync<string>(StateStoreName, $"ms-status-{serverId}", cancellationToken: context.CancellationToken) ?? "offline";
        }
        catch (Exception ex)
        {
          logger.LogWarning(ex, "Reading the heartbeat of media server '{ServerId}' failed; reporting it offline", serverId);
          status = "offline";
        }
        response.Servers.Add(new MediaServerNode
        {
          ServerId = serverId, FsId = fsId, OnvifId = onvifId, LoggerId = loggerId, RoiId = roiId,
          Status = status,
        });
      }
      return response;
    }

    public override async Task<ListCamerasResponse> ListCameras(ListCamerasRequest request, ServerCallContext context)
    {
      var response = new ListCamerasResponse();
      response.Cameras.AddRange(await storage.ListCamerasAsync(request.ServerId, context.CancellationToken));
      return response;
    }

    public override async Task<OperationStatus> RemoveCamera(SourceRequest request, ServerCallContext context)
    {
      // Get server_id before removing so we can notify the right media server
      var (_, existing) = await storage.GetCameraAsync(request.SourceId, context.CancellationToken);
      var removed = await storage.RemoveCameraAsync(request.SourceId, context.CancellationToken);
      if (removed)
      {
        logger.LogInformation("Camera '{CameraId}' removed", request.SourceId);
        if (existing is not null)
          NotifyCameraChanged(existing.ServerId, request.SourceId);
      }
      return new OperationStatus { Success = removed };
    }

    public override async Task<GetCameraResponse> GetCamera(SourceRequest request, ServerCallContext context)
    {
      var (found, setup) = await storage.GetCameraAsync(request.SourceId, context.CancellationToken);
      if (!found || setup is null)
        return new GetCameraResponse { Found = false };

      return new GetCameraResponse { Found = true, Setup = setup };
    }

    public override async Task<ListRecordingProfilesResponse> ListRecordingProfiles(
      ListRecordingProfilesRequest request,
      ServerCallContext context)
    {
      var response = new ListRecordingProfilesResponse();
      response.Profiles.AddRange(await storage.ListRecordingProfilesAsync(context.CancellationToken));
      return response;
    }

    public override async Task<OperationStatus> UpsertRecordingProfile(
      RecordingProfile request,
      ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.Name))
        return new OperationStatus { Success = false, Message = "name must not be empty" };

      await storage.UpsertRecordingProfileAsync(request, context.CancellationToken);
      logger.LogInformation("Recording profile '{Name}' upserted (id={Id}, mode={Mode})", request.Name, request.Id, request.Mode);
      return new OperationStatus { Success = true };
    }

    public override async Task<OperationStatus> RemoveRecordingProfile(
      RecordingProfileRequest request,
      ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.Id))
        return new OperationStatus { Success = false, Message = "id must not be empty" };

      var removed = await storage.RemoveRecordingProfileAsync(request.Id, context.CancellationToken);
      if (removed)
        logger.LogInformation("Recording profile '{Id}' removed", request.Id);
      return new OperationStatus { Success = removed };
    }

    public override async Task<OperationStatus> SetStreamOverride(
      SetStreamOverrideRequest request,
      ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.CameraId) || string.IsNullOrWhiteSpace(request.StreamName))
        return new OperationStatus { Success = false, Message = "camera_id and stream_name are required" };

      if (string.IsNullOrWhiteSpace(request.ProfileId) || !Guid.TryParse(request.ProfileId, out _))
        return new OperationStatus { Success = false, Message = "profile_id must be a valid UUID" };

      var allowed = await storage.IsRecordingProfileAllowedAsync(
        request.CameraId, request.StreamName, request.ProfileId, context.CancellationToken);
      if (allowed is null)
        return new OperationStatus { Success = false, Message = "unknown stream or recording profile" };
      if (!allowed.Value)
        return new OperationStatus
        {
          Success = false,
          Message = "stream is not available for recording; enable that first",
        };

      await storage.SetStreamOverrideAsync(request.CameraId, request.StreamName, request.ProfileId, context.CancellationToken);
      logger.LogInformation("Stream override set: camera='{CameraId}' stream='{StreamName}' profile='{ProfileId}'", request.CameraId, request.StreamName, request.ProfileId);
      InvalidateFsCache(request.CameraId);
      return new OperationStatus { Success = true };
    }

    public override async Task<OperationStatus> ClearStreamOverride(
      ClearStreamOverrideRequest request,
      ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.CameraId) || string.IsNullOrWhiteSpace(request.StreamName))
        return new OperationStatus { Success = false, Message = "camera_id and stream_name are required" };

      await storage.ClearStreamOverrideAsync(request.CameraId, request.StreamName, context.CancellationToken);
      logger.LogInformation("Stream override cleared: camera='{CameraId}' stream='{StreamName}'", request.CameraId, request.StreamName);
      InvalidateFsCache(request.CameraId);
      return new OperationStatus { Success = true };
    }

    public override async Task<OperationStatus> SetStreamRecordingProfile(
      SetStreamRecordingProfileRequest request,
      ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.CameraId) || string.IsNullOrWhiteSpace(request.StreamName))
        return new OperationStatus { Success = false, Message = "camera_id and stream_name are required" };

      if (string.IsNullOrWhiteSpace(request.ProfileId) || !Guid.TryParse(request.ProfileId, out _))
        return new OperationStatus { Success = false, Message = "profile_id must be a valid UUID" };

      var allowed = await storage.IsRecordingProfileAllowedAsync(
        request.CameraId, request.StreamName, request.ProfileId, context.CancellationToken);
      if (allowed is null)
        return new OperationStatus { Success = false, Message = "unknown stream or recording profile" };
      if (!allowed.Value)
        return new OperationStatus
        {
          Success = false,
          Message = "stream is not available for recording; enable that first",
        };

      await storage.SetStreamRecordingProfileAsync(request.CameraId, request.StreamName, request.ProfileId, context.CancellationToken);
      logger.LogInformation("Stream recording profile set: camera='{CameraId}' stream='{StreamName}' profile='{ProfileId}'", request.CameraId, request.StreamName, request.ProfileId);
      InvalidateFsCache(request.CameraId);
      return new OperationStatus { Success = true };
    }

    public override async Task<SetCameraContinuousRecordingResponse> SetCameraContinuousRecording(
      SetCameraContinuousRecordingRequest request,
      ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.CameraId))
        return new SetCameraContinuousRecordingResponse { Success = false, Message = "camera_id is required" };

      var changed = await storage.SetCameraContinuousRecordingAsync(
        request.CameraId, request.On, context.CancellationToken);

      // Nothing moved: either every stream already held the target profile, or the camera has no
      // stream that may be recorded at all. Neither is an error, and neither is worth a cache drop.
      if (changed.Count == 0)
        return new SetCameraContinuousRecordingResponse { Success = true };

      logger.LogInformation(
        "Camera continuous recording {State}: camera='{CameraId}' streams=[{Streams}]",
        request.On ? "on" : "off", request.CameraId, string.Join(", ", changed));
      InvalidateFsCache(request.CameraId);

      var response = new SetCameraContinuousRecordingResponse { Success = true };
      response.ChangedStreams.AddRange(changed);
      return response;
    }

    public override async Task<ListRecordingSchedulesResponse> ListRecordingSchedules(
      ListRecordingSchedulesRequest request,
      ServerCallContext context)
    {
      var response = new ListRecordingSchedulesResponse();
      response.Schedules.AddRange(await scheduleDb.ListSchedulesAsync(context.CancellationToken));
      return response;
    }

    public override async Task<OperationStatus> UpsertRecordingSchedule(
      RecordingSchedule request,
      ServerCallContext context)
    {
      var invalid = ValidateSchedule(request);
      if (invalid is not null)
        return new OperationStatus { Success = false, Message = invalid };

      var ct = context.CancellationToken;

      Guid id;
      try
      {
        id = await scheduleDb.UpsertScheduleAsync(request, ct);
      }
      catch (Exception ex) when (IsUniqueViolation(ex))
      {
        return new OperationStatus
        {
          Success = false,
          Message = $"a recording schedule named '{request.Name}' already exists",
        };
      }

      logger.LogInformation(
        "Recording schedule '{Name}' upserted (id={Id}, tz={Timezone}, windows={Windows}, lead_in={LeadIn}s)",
        request.Name, id, request.Timezone, request.Windows.Count, request.LeadInSeconds);

      await scheduleCache.ReloadAsync(ct);
      await BumpScheduleVersionAsync(ct);
      NotifyRecordingScheduleChanged();
      await NotifyCamerasChangedAsync(await scheduleDb.ListCamerasUsingScheduleAsync(id, ct), ct);
      return new OperationStatus { Success = true };
    }

    /// Returns an error message, or null when the schedule is valid. Everything the evaluator would
    /// otherwise have to guess about is rejected here instead: a bad timezone would silently shift
    /// every window, and a zero-length window would read as a full 24 hours.
    private static string? ValidateSchedule(RecordingSchedule schedule)
    {
      if (string.IsNullOrWhiteSpace(schedule.Name))
        return "name must not be empty";

      if (string.IsNullOrWhiteSpace(schedule.Timezone))
        return "timezone must not be empty";

      if (!RecordingScheduleEvaluator.IsKnownTimeZone(schedule.Timezone))
        return $"unknown timezone '{schedule.Timezone}': expected an IANA id such as 'Europe/Moscow'";

      if (schedule.LeadInSeconds > SecondsPerDay)
        return "lead_in_seconds must not exceed one day";

      foreach (var w in schedule.Windows)
      {
        if (w.StartSecond >= SecondsPerDay)
          return $"window start {w.StartSecond} is out of range (0..{SecondsPerDay - 1})";

        if (w.EndSecond > SecondsPerDay)
          return $"window end {w.EndSecond} is out of range (0..{SecondsPerDay})";

        // end < start is a window wrapping past midnight; end == start would be indistinguishable
        // from a full day, so it is rejected rather than guessed.
        if (w.EndSecond == w.StartSecond)
          return "window end must differ from its start";

        if (w.Kind == ScheduleWindowKind.ScheduleWindowDate)
        {
          if (!DateOnly.TryParse(w.Date, out _))
            return $"window date '{w.Date}' is not a valid YYYY-MM-DD date";
        }
        else if (w.DayOfWeek > 6)
        {
          return $"window day_of_week {w.DayOfWeek} is out of range (0=Sunday..6=Saturday)";
        }
      }

      return null;
    }

    private const uint SecondsPerDay = 24 * 60 * 60;

    /// Postgres unique-violation (23505) anywhere in the exception chain — EF wraps it in
    /// DbUpdateException.
    private static bool IsUniqueViolation(Exception ex) => HasSqlState(ex, "23505");

    /// Postgres foreign-key violation (23503).
    private static bool IsForeignKeyViolation(Exception ex) => HasSqlState(ex, "23503");

    private static bool HasSqlState(Exception? ex, string sqlState)
    {
      for (; ex is not null; ex = ex.InnerException)
      {
        if (ex is PostgresException pg && pg.SqlState == sqlState)
          return true;
      }
      return false;
    }

    public override async Task<OperationStatus> RemoveRecordingSchedule(
      RecordingScheduleRequest request,
      ServerCallContext context)
    {
      if (!Guid.TryParse(request.Id, out var id))
        return new OperationStatus { Success = false, Message = "id must be a valid UUID" };

      var ct = context.CancellationToken;
      // Collected before the delete cascades camera_schedule away.
      var affected = await scheduleDb.ListCamerasUsingScheduleAsync(id, ct);

      var removed = await scheduleDb.RemoveScheduleAsync(id, ct);
      if (!removed)
        return new OperationStatus { Success = false, Message = "schedule not found" };

      logger.LogInformation("Recording schedule '{Id}' removed", id);
      await scheduleCache.ReloadAsync(ct);
      await BumpScheduleVersionAsync(ct);
      NotifyRecordingScheduleChanged();
      await NotifyCamerasChangedAsync(affected, ct);
      return new OperationStatus { Success = true };
    }

    public override async Task<OperationStatus> SetCameraSchedules(
      SetCameraSchedulesRequest request,
      ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.CameraId))
        return new OperationStatus { Success = false, Message = "camera_id must not be empty" };

      foreach (var raw in request.ScheduleIds)
      {
        if (!Guid.TryParse(raw, out _))
          return new OperationStatus { Success = false, Message = $"schedule id '{raw}' is not a valid UUID" };
      }

      var ct = context.CancellationToken;
      try
      {
        await scheduleDb.SetCameraSchedulesAsync(request.CameraId, request.ScheduleIds, ct);
      }
      catch (Exception ex) when (IsForeignKeyViolation(ex))
      {
        // Either the camera or one of the schedules was removed between listing and assigning.
        return new OperationStatus
        {
          Success = false,
          Message = "camera or schedule no longer exists",
        };
      }

      logger.LogInformation(
        "Camera '{CameraId}' schedules set: [{ScheduleIds}]",
        request.CameraId, string.Join(", ", request.ScheduleIds));

      await scheduleCache.ReloadAsync(ct);
      await BumpScheduleVersionAsync(ct);
      NotifyRecordingScheduleChanged();
      await NotifyCamerasChangedAsync([request.CameraId], ct);
      return new OperationStatus { Success = true };
    }

    public override async Task<ActivateAlarmOverrideResponse> ActivateAlarmOverride(
      ActivateAlarmOverrideRequest request,
      ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.CameraId) || request.Until is null)
        return new ActivateAlarmOverrideResponse { Activated = false };

      var streamName = request.HasStreamName && !string.IsNullOrWhiteSpace(request.StreamName)
        ? request.StreamName : null;
      var until = request.Until.ToDateTime().ToUniversalTime();

      var outcome = await storage.ActivateAlarmOverrideAsync(request.CameraId, streamName, until, context.CancellationToken);

      if (outcome.Started)
      {
        logger.LogInformation("Alarm override activated: camera='{CameraId}' stream='{StreamName}' until={Until}", request.CameraId, streamName, until);
        InvalidateFsCache(request.CameraId);
      }
      else if (outcome.Extended)
      {
        // Deliberately quiet, and deliberately without InvalidateFsCache: the stream is already
        // recording, so a config change here would make every re-fire of a motion detector reload
        // every camera on the node for no visible effect. The new deadline matters only to
        // AlarmOverrideExpiryService, which reads the table directly.
        logger.LogDebug("Alarm override extended: camera='{CameraId}' stream='{StreamName}' until={Until}", request.CameraId, streamName, until);
      }

      // Reported as activated only on the transition — the caller (VmsFs) pushes camera state on
      // the strength of this, and re-pushing an unchanged state is exactly the storm above.
      return new ActivateAlarmOverrideResponse { Activated = outcome.Started };
    }

    public override async Task<ListMediaMtxServersResponse> ListMediaMtxServers(
      ListMediaMtxServersRequest request,
      ServerCallContext context)
    {
      var response = new ListMediaMtxServersResponse();
      response.Servers.AddRange(await storage.ListMediaMtxServersAsync(context.CancellationToken));
      return response;
    }

    public override async Task<OperationStatus> UpsertMediaMtxServer(
      MediaMtxServer request,
      ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.Name))
        return new OperationStatus { Success = false, Message = "name must not be empty" };

      if (string.IsNullOrWhiteSpace(request.Host))
        return new OperationStatus { Success = false, Message = "host must not be empty" };

      await storage.UpsertMediaMtxServerAsync(request, context.CancellationToken);
      logger.LogInformation("MediaMTX server '{Name}' upserted (id={Id})", request.Name, request.Id);
      NotifyAllMediaServers();
      return new OperationStatus { Success = true };
    }

    public override async Task<OperationStatus> RemoveMediaMtxServer(
      MediaMtxServerRequest request,
      ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.Id))
        return new OperationStatus { Success = false, Message = "id must not be empty" };

      var removed = await storage.RemoveMediaMtxServerAsync(request.Id, context.CancellationToken);
      if (removed)
      {
        logger.LogInformation("MediaMTX server '{Id}' removed", request.Id);
        NotifyAllMediaServers();
      }
      return new OperationStatus { Success = removed };
    }

    public override async Task<GetOrAssignMediaMtxServerResponse> GetOrAssignMediaMtxServer(
      SourceRequest request,
      ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.SourceId))
        return new GetOrAssignMediaMtxServerResponse { Found = false };

      var server = await storage.GetOrAssignMediaMtxServerAsync(request.SourceId, context.CancellationToken);
      if (server is null)
        return new GetOrAssignMediaMtxServerResponse { Found = false };

      return new GetOrAssignMediaMtxServerResponse { Found = true, Server = server };
    }

    public override async Task<ListMonitorsResponse> ListMonitors(
      ListMonitorsRequest request,
      ServerCallContext context)
    {
      var response = new ListMonitorsResponse();
      response.Monitors.AddRange(await storage.ListMonitorsAsync(request.Owner, context.CancellationToken));
      return response;
    }

    public override async Task<MonitorSetup> UpsertMonitor(MonitorSetup request, ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.Name))
        throw new RpcException(new Status(StatusCode.InvalidArgument, "name must not be empty"));

      if (string.IsNullOrWhiteSpace(request.Layout))
        throw new RpcException(new Status(StatusCode.InvalidArgument, "layout must not be empty"));

      // Boards are created freely by operators, so the cap is enforced here rather than left to
      // the UI — only on creation, so an existing set of boards stays editable.
      if (request.Kind == CameraStorage.MonitorKindInvestigation && string.IsNullOrEmpty(request.MonitorId))
      {
        var existing = await storage.CountInvestigationBoardsAsync(request.Owner, context.CancellationToken);
        if (existing >= MaxInvestigationBoardsPerOwner)
          throw new RpcException(new Status(
            StatusCode.FailedPrecondition,
            $"investigation board limit reached ({MaxInvestigationBoardsPerOwner})"));
      }

      MonitorSetup? result;
      try
      {
        result = await storage.UpsertMonitorAsync(request, context.CancellationToken);
      }
      // ON CONFLICT resolves a clash on the id; a clash on (name, kind, owner) is a different row
      // entirely and surfaces as a unique violation. Reachable in ordinary use — two tabs both
      // auto-naming a new board pick "Investigation 1" from their own stale list — so it has to
      // come back as a stated reason, not as an unhandled 500.
      catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
      {
        throw new RpcException(new Status(StatusCode.AlreadyExists, $"a monitor named '{request.Name}' already exists"));
      }

      if (result is null)
        throw new RpcException(new Status(StatusCode.PermissionDenied, "monitor belongs to another user"));

      logger.LogInformation("Monitor '{Name}' upserted (id={Id}, layout={Layout}, kind={Kind})",
        result.Name, result.MonitorId, result.Layout, result.Kind);
      return result;
    }

    public override async Task<OperationStatus> RemoveMonitor(RemoveMonitorRequest request, ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.MonitorId))
        return new OperationStatus { Success = false, Message = "monitor_id must not be empty" };

      var removed = await storage.RemoveMonitorAsync(request.MonitorId, request.Owner, context.CancellationToken);
      if (removed)
        logger.LogInformation("Monitor '{Id}' removed", request.MonitorId);
      return new OperationStatus { Success = removed, Message = removed ? "ok" : "not found" };
    }

    public override async Task<OperationStatus> SetMonitorCameras(
      SetMonitorCamerasRequest request,
      ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.MonitorId))
        return new OperationStatus { Success = false, Message = "monitor_id must not be empty" };

      var ok = await storage.SetMonitorCamerasAsync(
        request.MonitorId, request.CameraIds, request.Owner, context.CancellationToken);
      if (!ok)
        return new OperationStatus { Success = false, Message = "monitor not found" };

      logger.LogInformation("Monitor '{MonitorId}' camera pool set ({Count} cameras)", request.MonitorId, request.CameraIds.Count);
      return new OperationStatus { Success = true };
    }

    public override async Task<OperationStatus> SetMonitorSlot(
      SetMonitorSlotRequest request,
      ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.MonitorId) || string.IsNullOrWhiteSpace(request.CameraId))
        return new OperationStatus { Success = false, Message = "monitor_id and camera_id must not be empty" };

      var ok = await storage.SetMonitorSlotAsync(
        request.MonitorId, request.SlotIndex, request.CameraId, request.Owner, context.CancellationToken);
      if (!ok)
        return new OperationStatus { Success = false, Message = "monitor not found" };

      logger.LogInformation("Monitor '{MonitorId}' slot {SlotIndex} set to camera '{CameraId}'", request.MonitorId, request.SlotIndex, request.CameraId);
      return new OperationStatus { Success = true };
    }

    public override async Task<OperationStatus> SetMonitorSlotStream(
      SetMonitorSlotStreamRequest request,
      ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.MonitorId) || !Guid.TryParse(request.MonitorId, out var monitorId))
        return new OperationStatus { Success = false, Message = "monitor_id must be a valid UUID" };

      // No ownership check: this writes only the caller's own row, and a wall is deliberately
      // visible to everyone who can see the monitor.
      await storage.SetMonitorSlotStreamAsync(
        request.Owner, monitorId, request.SlotIndex, request.StreamName, context.CancellationToken);
      return new OperationStatus { Success = true };
    }

    public override async Task<OperationStatus> ClearMonitorSlot(
      ClearMonitorSlotRequest request,
      ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.MonitorId))
        return new OperationStatus { Success = false, Message = "monitor_id must not be empty" };

      // Deleting a cell that is already empty is not a failure — dragging the last camera out of a
      // slot and dragging it back is a perfectly ordinary thing to do.
      await storage.ClearMonitorSlotAsync(
        request.MonitorId, request.SlotIndex, request.Owner, context.CancellationToken);
      logger.LogInformation("Monitor '{MonitorId}' slot {SlotIndex} cleared", request.MonitorId, request.SlotIndex);
      return new OperationStatus { Success = true };
    }

    public override async Task<MonitorSetup> CopyMonitor(CopyMonitorRequest request, ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.MonitorId))
        throw new RpcException(new Status(StatusCode.InvalidArgument, "monitor_id must not be empty"));

      var existing = await storage.CountInvestigationBoardsAsync(request.TargetOwner, context.CancellationToken);
      if (existing >= MaxInvestigationBoardsPerOwner)
        throw new RpcException(new Status(
          StatusCode.FailedPrecondition,
          $"recipient already has {MaxInvestigationBoardsPerOwner} investigation boards"));

      var copy = await storage.CopyMonitorAsync(
                   request.MonitorId, request.Owner, request.TargetOwner, context.CancellationToken)
                 ?? throw new RpcException(new Status(StatusCode.PermissionDenied, "monitor is not yours to share"));

      logger.LogInformation("Monitor '{Id}' copied to '{Target}' as '{Name}'",
        request.MonitorId, request.TargetOwner, copy.Name);
      return copy;
    }

    public override async Task<ListOnvifAlarmFiltersResponse> ListOnvifAlarmFilters(
      ListOnvifAlarmFiltersRequest request,
      ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.CameraId))
        return new ListOnvifAlarmFiltersResponse();

      var filters = await storage.ListOnvifAlarmFiltersAsync(request.CameraId, context.CancellationToken);
      var response = new ListOnvifAlarmFiltersResponse();
      response.Filters.AddRange(filters);
      return response;
    }

    public override async Task<OnvifAlarmFilter> UpsertOnvifAlarmFilter(
      OnvifAlarmFilter request,
      ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.CameraId))
        throw new RpcException(new Status(StatusCode.InvalidArgument, "camera_id is required"));
      if (string.IsNullOrWhiteSpace(request.FilterText))
        throw new RpcException(new Status(StatusCode.InvalidArgument, "filter_text is required"));
      if (request.TimeoutSeconds <= 0)
        request.TimeoutSeconds = 30;

      var result = await storage.UpsertOnvifAlarmFilterAsync(request, context.CancellationToken);
      logger.LogInformation("ONVIF alarm filter '{Id}' upserted for camera '{CameraId}'", result.Id, result.CameraId);
      InvalidateFsCache(result.CameraId);
      return result;
    }

    public override async Task<OperationStatus> RemoveOnvifAlarmFilter(
      OnvifAlarmFilterRequest request,
      ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.Id))
        return new OperationStatus { Success = false, Message = "id is required" };

      var (removed, cameraId) = await storage.RemoveOnvifAlarmFilterAsync(request.Id, context.CancellationToken);
      if (removed && cameraId is not null)
      {
        logger.LogInformation("ONVIF alarm filter '{Id}' removed (camera='{CameraId}')", request.Id, cameraId);
        InvalidateFsCache(cameraId);
      }
      return new OperationStatus { Success = removed };
    }

    public override async Task<OperationStatus> SubscribeOnvifEvents(
      OnvifSubscriptionRequest request, ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.CameraId))
        return new OperationStatus { Success = false, Message = "camera_id is required" };
      await storage.SubscribeOnvifEventsAsync(request.CameraId, context.CancellationToken);
      logger.LogInformation("ONVIF event subscription enabled for camera '{CameraId}'", request.CameraId);
      InvalidateFsCache(request.CameraId);
      return new OperationStatus { Success = true };
    }

    public override async Task<OperationStatus> UnsubscribeOnvifEvents(
      OnvifSubscriptionRequest request, ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.CameraId))
        return new OperationStatus { Success = false, Message = "camera_id is required" };
      await storage.UnsubscribeOnvifEventsAsync(request.CameraId, context.CancellationToken);
      logger.LogInformation("ONVIF event subscription disabled for camera '{CameraId}'", request.CameraId);
      InvalidateFsCache(request.CameraId);
      return new OperationStatus { Success = true };
    }

    public override async Task<OnvifSubscriptionsResponse> ListOnvifSubscriptions(
      Google.Protobuf.WellKnownTypes.Empty request, ServerCallContext context)
    {
      var list = await storage.ListOnvifSubscriptionsAsync(context.CancellationToken);
      var response = new OnvifSubscriptionsResponse();
      response.Cameras.AddRange(list.Select(x => new OnvifCameraEntry
      {
        CameraId           = x.CameraId,
        Subscribed         = x.Subscribed,
        PollTimeoutSeconds = x.PollTimeoutSeconds,
        MessageLimit       = x.MessageLimit,
      }));
      return response;
    }

    public override async Task<OperationStatus> SetOnvifSubscribed(
      SetOnvifSubscribedRequest request, ServerCallContext context)
    {
      await storage.SetOnvifSubscribedAsync(request.CameraId, request.Subscribed, context.CancellationToken);
      logger.LogInformation("Camera '{CameraId}' ONVIF event subscription set to {Subscribed}", request.CameraId, request.Subscribed);
      InvalidateFsCache(request.CameraId);
      return new OperationStatus { Success = true };
    }

    public override async Task<OperationStatus> SetOnvifPollConfig(
      SetOnvifPollConfigRequest request, ServerCallContext context)
    {
      if (request.PollTimeoutSeconds <= 0 || request.MessageLimit <= 0)
        return new OperationStatus { Success = false, Message = "poll_timeout_seconds and message_limit must be positive" };

      await storage.SetOnvifPollConfigAsync(request.CameraId, request.PollTimeoutSeconds, request.MessageLimit, context.CancellationToken);
      logger.LogInformation("Camera '{CameraId}' ONVIF poll config set: timeout={Timeout}s limit={Limit}", request.CameraId, request.PollTimeoutSeconds, request.MessageLimit);
      InvalidateFsCache(request.CameraId);
      return new OperationStatus { Success = true };
    }

    public override async Task<GetCamStateResponse> GetCamState(
      GetCamStateRequest request, ServerCallContext context)
    {
      var armed = await storage.GetCameraArmedAsync(request.CameraId, context.CancellationToken);
      return new GetCamStateResponse { Armed = armed };
    }

    public override async Task<OperationStatus> SetCamState(
      SetCamStateRequest request, ServerCallContext context)
    {
      await storage.SetCameraArmedAsync(request.CameraId, request.Armed, context.CancellationToken);
      logger.LogInformation("Camera '{CameraId}' armed={Armed}", request.CameraId, request.Armed);
      NotifyCameraStateChanged(request.CameraId);
      return new OperationStatus { Success = true };
    }

    public override async Task<ListAnalyticsWatchesResponse> ListAnalyticsWatches(
      ListAnalyticsWatchesRequest request, ServerCallContext context)
    {
      var cameraId = request.HasCameraId ? request.CameraId : null;
      var watches = await storage.ListAnalyticsWatchesAsync(cameraId, context.CancellationToken);
      var response = new ListAnalyticsWatchesResponse();
      response.Watches.AddRange(watches);
      return response;
    }

    public override async Task<OperationStatus> UpsertAnalyticsWatch(
      UpsertAnalyticsWatchRequest request, ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.Watch?.CameraId))
        return new OperationStatus { Success = false, Message = "camera_id must not be empty" };

      var result = await storage.UpsertAnalyticsWatchAsync(request.Watch, context.CancellationToken);
      logger.LogInformation("Analytics watch for camera '{CameraId}' upserted", result.CameraId);
      NotifyAnalyticsWatchChanged(result.CameraId);
      return new OperationStatus { Success = true };
    }

    public override async Task<OperationStatus> RemoveAnalyticsWatch(
      RemoveAnalyticsWatchRequest request, ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.CameraId))
        return new OperationStatus { Success = false, Message = "camera_id must not be empty" };

      var removed = await storage.RemoveAnalyticsWatchAsync(request.CameraId, context.CancellationToken);
      if (removed)
      {
        logger.LogInformation("Analytics watch for camera '{CameraId}' removed", request.CameraId);
        NotifyAnalyticsWatchChanged(request.CameraId);
      }
      return new OperationStatus { Success = removed };
    }

    public override async Task<CameraAnalyticsSourcesResponse> GetCameraAnalyticsSources(
      CameraAnalyticsSourcesRequest request, ServerCallContext context)
    {
      var sources = await storage.GetCameraAnalyticsSourcesAsync(request.CameraIds, context.CancellationToken);
      var response = new CameraAnalyticsSourcesResponse();
      response.Cameras.AddRange(sources);
      return response;
    }

    public override async Task<GetPtzCalibrationResponse> GetPtzCalibration(
      GetPtzCalibrationRequest request, ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.CameraId))
        return new GetPtzCalibrationResponse();

      var calibration = await storage.GetPtzCalibrationAsync(request.CameraId, context.CancellationToken);
      if (calibration is null)
        return new GetPtzCalibrationResponse();

      return new GetPtzCalibrationResponse { Found = true, Calibration = calibration };
    }

    public override async Task<OperationStatus> UpsertPtzCalibration(
      UpsertPtzCalibrationRequest request, ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.Calibration?.CameraId))
        return new OperationStatus { Success = false, Message = "camera_id must not be empty" };

      var result = await storage.UpsertPtzCalibrationAsync(request.Calibration, context.CancellationToken);
      logger.LogInformation("PTZ calibration for camera '{CameraId}' upserted", result.CameraId);
      InvalidateFsCache(result.CameraId);
      return new OperationStatus { Success = true };
    }

    public override async Task<OperationStatus> RemovePtzCalibration(
      RemovePtzCalibrationRequest request, ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.CameraId))
        return new OperationStatus { Success = false, Message = "camera_id must not be empty" };

      var removed = await storage.RemovePtzCalibrationAsync(request.CameraId, context.CancellationToken);
      if (removed)
      {
        logger.LogInformation("PTZ calibration for camera '{CameraId}' removed", request.CameraId);
        InvalidateFsCache(request.CameraId);
      }
      return new OperationStatus { Success = removed };
    }

    // ── Self-guidance policy ──────────────────────────────────────────────────

    public override async Task<GetPtzTrackingPolicyResponse> GetPtzTrackingPolicy(
      GetPtzTrackingPolicyRequest request, ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.CameraId))
        return new GetPtzTrackingPolicyResponse();

      var policy = await storage.GetPtzTrackingPolicyAsync(request.CameraId, context.CancellationToken);
      if (policy is null)
        return new GetPtzTrackingPolicyResponse();

      return new GetPtzTrackingPolicyResponse { Found = true, Policy = policy };
    }

    public override async Task<OperationStatus> UpsertPtzTrackingPolicy(
      UpsertPtzTrackingPolicyRequest request, ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.Policy?.CameraId))
        return new OperationStatus { Success = false, Message = "camera_id must not be empty" };

      var result = await storage.UpsertPtzTrackingPolicyAsync(request.Policy, context.CancellationToken);
      logger.LogInformation(
        "Self-guidance for camera '{CameraId}' is now {State}",
        result.CameraId, result.Enabled ? "on" : "off");
      // The same signal every other camera write sends. VmsPtzTrackingSingleton is subscribed to it
      // and reconciles its sessions against the new configuration -- which is what makes switching
      // the mode on start a camera tracking without anything else being pressed.
      InvalidateFsCache(result.CameraId);
      return new OperationStatus { Success = true };
    }

    public override async Task<ListPtzTrackingPoliciesResponse> ListPtzTrackingPolicies(
      Google.Protobuf.WellKnownTypes.Empty request, ServerCallContext context)
    {
      var response = new ListPtzTrackingPoliciesResponse();
      response.Policies.AddRange(
        await storage.ListPtzTrackingPoliciesAsync(context.CancellationToken));
      return response;
    }

    // ── Master/slave binding ──────────────────────────────────────────────────

    public override async Task<GetPtzSlaveBindingResponse> GetPtzSlaveBinding(
      GetPtzSlaveBindingRequest request, ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.SlaveCameraId))
        return new GetPtzSlaveBindingResponse();

      var binding = await storage.GetPtzSlaveBindingAsync(
        request.SlaveCameraId, context.CancellationToken);
      if (binding is null)
        return new GetPtzSlaveBindingResponse();

      return new GetPtzSlaveBindingResponse { Found = true, Binding = binding };
    }

    public override async Task<OperationStatus> UpsertPtzSlaveBinding(
      UpsertPtzSlaveBindingRequest request, ServerCallContext context)
    {
      var binding = request.Binding;
      if (string.IsNullOrWhiteSpace(binding?.SlaveCameraId))
        return new OperationStatus { Success = false, Message = "slave_camera_id must not be empty" };
      if (string.IsNullOrWhiteSpace(binding.MasterCameraId))
        return new OperationStatus { Success = false, Message = "master_camera_id must not be empty" };
      if (binding.MasterCameraId == binding.SlaveCameraId)
        return new OperationStatus
        {
          Success = false,
          // Not merely useless: it would have the open-loop aiming law fight the closed-loop one
          // over the same motor, each moving the picture the other is measuring.
          Message = "A camera cannot be its own master -- switch self-guidance on instead",
        };

      var result = await storage.UpsertPtzSlaveBindingAsync(binding, context.CancellationToken);
      logger.LogInformation(
        "Slave binding for camera '{Slave}' -> master '{Master}' upserted: {Points} point(s), method {Method}",
        result.SlaveCameraId, result.MasterCameraId, result.Points.Count, result.Method);
      InvalidateFsCache(result.SlaveCameraId);
      return new OperationStatus { Success = true };
    }

    public override async Task<OperationStatus> RemovePtzSlaveBinding(
      RemovePtzSlaveBindingRequest request, ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.SlaveCameraId))
        return new OperationStatus { Success = false, Message = "slave_camera_id must not be empty" };

      var removed = await storage.RemovePtzSlaveBindingAsync(
        request.SlaveCameraId, context.CancellationToken);
      if (removed)
      {
        logger.LogInformation("Slave binding for camera '{Slave}' removed", request.SlaveCameraId);
        InvalidateFsCache(request.SlaveCameraId);
      }
      return new OperationStatus { Success = removed };
    }

    public override async Task<ListPtzSlaveBindingsResponse> ListPtzSlaveBindings(
      Google.Protobuf.WellKnownTypes.Empty request, ServerCallContext context)
    {
      var response = new ListPtzSlaveBindingsResponse();
      response.Bindings.AddRange(
        await storage.ListPtzSlaveBindingsAsync(context.CancellationToken));
      return response;
    }

    // ── Per-camera permissions ────────────────────────────────────────────────

    public override async Task<ListCameraPermissionsResponse> ListCameraPermissions(
      ListCameraPermissionsRequest request, ServerCallContext context)
    {
      var username = request.HasUsername ? request.Username : null;
      var response = new ListCameraPermissionsResponse();
      response.Users.AddRange(await permissionDb.ListAsync(username, context.CancellationToken));
      return response;
    }

    public override async Task<OperationStatus> SetUserCameraPermissions(
      SetUserCameraPermissionsRequest request, ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.Permissions?.Username))
        return new OperationStatus { Success = false, Message = "username must not be empty" };

      await permissionDb.ReplaceUserAsync(request.Permissions, context.CancellationToken);
      logger.LogInformation(
        "Camera permissions for '{Username}' replaced: {CameraCount} cameras, blanket grant {Blanket}",
        request.Permissions.Username,
        request.Permissions.Cameras.Count,
        request.Permissions.AllCameras is null ? "none" : "set");
      await BumpCameraPermissionsVersionAsync(context.CancellationToken);
      return new OperationStatus { Success = true };
    }

    public override async Task<GetCameraAclModeResponse> GetCameraAclMode(
      Google.Protobuf.WellKnownTypes.Empty request, ServerCallContext context)
    {
      return new GetCameraAclModeResponse { Mode = await permissionDb.GetModeAsync(context.CancellationToken) };
    }

    public override async Task<OperationStatus> SetCameraAclMode(
      SetCameraAclModeRequest request, ServerCallContext context)
    {
      await permissionDb.SetModeAsync(request.Mode, context.CancellationToken);
      logger.LogInformation("Camera ACL mode set to {Mode}", request.Mode);
      await BumpCameraPermissionsVersionAsync(context.CancellationToken);
      return new OperationStatus { Success = true };
    }
  }
}

using Dapr.Client;
using Grpc.Core;
using Npgsql;
using SquareIntegrationClient;
using Vms.Cfg;

namespace VmsCfg
{
  public class CfgServiceImpl(
    CameraStorage storage,
    ScheduleDb scheduleDb,
    ScheduleCache scheduleCache,
    DaprClient daprClient,
    ISquareIntegration square,
    ILogger<CfgServiceImpl> logger) : CfgService.CfgServiceBase
  {
    private const string CameraConfigChangedTopic      = "camera-config-changed";
    private const string MediaServerConfigChangedTopic = "media-server-config-changed";
    private const string CameraStateChangedTopic       = "camera-state-changed";
    private const string AnalyticsWatchChangedTopic    = "analytics-watch-changed";

    // Investigation boards are operator-created and never expire on their own, so the number one
    // person can accumulate is capped. Walls are unaffected — they are admin-managed.
    private const int MaxInvestigationBoardsPerOwner = 8;

    private readonly string _pubSubName =
      Environment.GetEnvironmentVariable("PUBSUB_NAME")
        ?? throw new InvalidOperationException("Required environment variable 'PUBSUB_NAME' is not set");

    private void InvalidateFsCache(string cameraId)
    {
      _ = Task.Run(async () =>
      {
        try
        {
          await daprClient.PublishEventAsync(_pubSubName, CameraConfigChangedTopic, cameraId);
        }
        catch (Exception ex)
        {
          logger.LogWarning(ex, "Camera config invalidation publish failed for '{CameraId}'", cameraId);
        }
      });
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

    private void NotifyMediaServer(string serverId)
    {
      if (string.IsNullOrWhiteSpace(serverId)) return;
      _ = Task.Run(async () =>
      {
        try
        {
          await daprClient.PublishEventAsync(_pubSubName, MediaServerConfigChangedTopic, serverId);
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

        foreach (var cameraId in cameraIds)
          await daprClient.PublishEventAsync(_pubSubName, CameraConfigChangedTopic, cameraId, ct);

        foreach (var serverId in servers.Values.Distinct().Where(s => !string.IsNullOrWhiteSpace(s)))
          await daprClient.PublishEventAsync(_pubSubName, MediaServerConfigChangedTopic, serverId, ct);

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
      foreach (var (serverId, _) in storage.GetServerPairs())
        NotifyMediaServer(serverId);
    }

    private static readonly string SquareAppId = Environment.GetEnvironmentVariable("APP_ID") ?? "vmscfg";

    // Best-effort push to Square (see SquareIntegrationClient/SquareCameraPush) — never blocks or
    // fails the camera-setup RPC. No-op when SQUARE_INTEGRATION_SINK_APP_ID is unset
    // (square.Enabled == false). Re-fetches camera_name from storage since the raw request
    // doesn't carry the DB-joined recording profile name needed for the push.
    private void PushCameraToSquare(string cameraId)
    {
      if (!square.Enabled) return;

      _ = Task.Run(async () =>
      {
        try
        {
          var (found, setup) = await storage.GetCameraAsync(cameraId, CancellationToken.None);
          if (!found || setup is null) return;

          await SquareCameraPush.PushCameraAsync(square, setup, SquareAppId);
        }
        catch (Exception ex)
        {
          logger.LogWarning(ex, "Square push failed for camera '{CameraId}'", cameraId);
        }
      });
    }

    private void PushCameraStateToSquare(string cameraId, bool armed)
    {
      if (!square.Enabled) return;

      _ = Task.Run(async () =>
      {
        try
        {
          await SquareCameraPush.PushPropertiesAsync(square, cameraId, new Dictionary<string, string>
          {
            ["armed"] = armed.ToString(),
          });
        }
        catch (Exception ex)
        {
          logger.LogWarning(ex, "Square property push failed for camera '{CameraId}'", cameraId);
        }
      });
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
      InvalidateFsCache(request.CameraId);
      NotifyMediaServer(request.ServerId);
      PushCameraToSquare(request.CameraId);

      // Notify old server so it removes the camera
      if (existing is not null && existing.ServerId != request.ServerId)
        NotifyMediaServer(existing.ServerId);

      return new OperationStatus { Success = true };
    }

    public override async Task<ListMediaServersResponse> ListMediaServers(
      ListMediaServersRequest request,
      ServerCallContext context)
    {
      var response = new ListMediaServersResponse();
      foreach (var (serverId, fsId) in storage.GetServerPairs())
      {
        string status;
        try
        {
          status = await daprClient.GetStateAsync<string>(StateStoreName, $"ms-status-{serverId}", cancellationToken: context.CancellationToken) ?? "offline";
        }
        catch
        {
          status = "offline";
        }
        response.Servers.Add(new MediaServerNode { ServerId = serverId, FsId = fsId, Status = status });
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
        InvalidateFsCache(request.SourceId);
        if (existing is not null)
          NotifyMediaServer(existing.ServerId);
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

      await storage.SetStreamOverrideAsync(request.CameraId, request.StreamName, request.ProfileId, context.CancellationToken);
      logger.LogInformation("Stream override set: camera='{CameraId}' stream='{StreamName}' profile='{ProfileId}'", request.CameraId, request.StreamName, request.ProfileId);
      InvalidateFsCache(request.CameraId);
      var (_, cam) = await storage.GetCameraAsync(request.CameraId, context.CancellationToken);
      if (cam is not null) NotifyMediaServer(cam.ServerId);
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
      var (_, cam) = await storage.GetCameraAsync(request.CameraId, context.CancellationToken);
      if (cam is not null) NotifyMediaServer(cam.ServerId);
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

      await storage.SetStreamRecordingProfileAsync(request.CameraId, request.StreamName, request.ProfileId, context.CancellationToken);
      logger.LogInformation("Stream recording profile set: camera='{CameraId}' stream='{StreamName}' profile='{ProfileId}'", request.CameraId, request.StreamName, request.ProfileId);
      InvalidateFsCache(request.CameraId);
      var (_, cam) = await storage.GetCameraAsync(request.CameraId, context.CancellationToken);
      if (cam is not null) NotifyMediaServer(cam.ServerId);
      return new OperationStatus { Success = true };
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

      await scheduleCache.ReloadAndSignalAsync(ct);
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
      await scheduleCache.ReloadAndSignalAsync(ct);
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

      await scheduleCache.ReloadAndSignalAsync(ct);
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

      var activated = await storage.ActivateAlarmOverrideAsync(request.CameraId, streamName, until, context.CancellationToken);
      if (activated)
      {
        logger.LogInformation("Alarm override activated: camera='{CameraId}' stream='{StreamName}' until={Until}", request.CameraId, streamName, until);
        InvalidateFsCache(request.CameraId);
      }
      return new ActivateAlarmOverrideResponse { Activated = activated };
    }

    public override async Task<OperationStatus> ClearAlarmOverride(
      ClearAlarmOverrideRequest request,
      ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.CameraId) || string.IsNullOrWhiteSpace(request.StreamName))
        return new OperationStatus { Success = false, Message = "camera_id and stream_name are required" };

      var cleared = await storage.ClearAlarmOverrideAsync(request.CameraId, request.StreamName, context.CancellationToken);
      if (cleared)
      {
        logger.LogInformation("Alarm override cleared: camera='{CameraId}' stream='{StreamName}'", request.CameraId, request.StreamName);
        InvalidateFsCache(request.CameraId);
      }
      return new OperationStatus { Success = true };
    }

    public override async Task<ListAlarmOverrideStreamsResponse> ListAlarmOverrideStreams(
      ListAlarmOverrideStreamsRequest request,
      ServerCallContext context)
    {
      var streams = await storage.ListAlarmOverrideStreamsAsync(context.CancellationToken);
      var response = new ListAlarmOverrideStreamsResponse();
      response.Streams.AddRange(streams.Select(s => new AlarmOverrideStream
      {
        CameraId   = s.CameraId,
        StreamName = s.StreamName,
      }));
      return response;
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

    public override async Task<OperationStatus> SetOnvifArmed(
      SetOnvifArmedRequest request, ServerCallContext context)
    {
      await storage.SetOnvifArmedAsync(request.CameraId, request.Armed, context.CancellationToken);
      logger.LogInformation("Camera '{CameraId}' armed state set to {Armed}", request.CameraId, request.Armed);
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
      PushCameraStateToSquare(request.CameraId, request.Armed);
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
  }
}
